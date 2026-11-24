// Trusted plugin process ownership and lifecycle. Windows only; not a sandbox.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AppShelf
{
    internal sealed class PluginJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessTime, PerJobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int kind,
            ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        private SafeFileHandle handle;

        internal PluginJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error);
            }
            ExtendedLimits limits = new ExtendedLimits();
            limits.Basic.Flags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error);
            }
        }

        internal void Attach(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            SafeFileHandle previous = Interlocked.Exchange(ref handle, null);
            if (previous != null) previous.Dispose();
        }
    }

    internal sealed class PluginChild
    {
        private readonly Process process;
        private readonly PluginJob job;
        private readonly PluginProtocolSession session;
        private readonly int timeout;
        private readonly object stopGate = new object();
        private bool stopped;
        private volatile bool ready;
        internal PluginCommand[] Commands { get; private set; }
        internal int ProcessId { get; private set; }

        internal PluginChild(PluginManifest manifest, string executable, int timeoutMs)
        {
            timeout = timeoutMs;
            job = new PluginJob();
            process = new Process();
            try
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (!process.Start()) throw new IOException("Не удалось запустить процесс плагина.");
                ProcessId = process.Id;
                // Trusted plugins must not spawn children before hello; attach immediately.
                // If job assignment fails, do not proceed with an unmanaged process.
                job.Attach(process);
                session = new PluginProtocolSession(process.StandardInput.BaseStream,
                    process.StandardOutput.BaseStream, ForceTerminate, manifest.Id, manifest.Version, manifest.Capabilities);
                DrainErrors(process.StandardError.BaseStream);
            }
            catch
            {
                ForceTerminate(); process.Dispose(); throw;
            }
        }

        private static async void DrainErrors(Stream stream)
        {
            // Bound memory and avoid copying plugin-controlled diagnostic/private text to host logs.
            byte[] buffer = new byte[4096];
            try { while (await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) != 0) { } }
            catch (Exception) { }
        }

        internal bool HasExited
        {
            get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
        }

        internal void Initialize(PluginManifest manifest, string data, CancellationToken cancel)
        {
            PluginResponse hello = session.HelloAsync(
                "ru-RU",
                data, timeout, cancel).GetAwaiter().GetResult();
            Commands = hello.Commands ?? new PluginCommand[0];
            if (Array.IndexOf(manifest.Capabilities, "background") >= 0)
                session.StartBackgroundAsync(timeout, cancel).GetAwaiter().GetResult();
            ready = true;
        }

        internal void Ping(CancellationToken cancel)
        {
            session.PingAsync(timeout, cancel).GetAwaiter().GetResult();
        }

        internal void ForceTerminate()
        {
            job.Dispose();
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        // One owner performs cooperative shutdown; any concurrent host shutdown may
        // still force the job closed if the bounded grace period expires.
        internal void Stop()
        {
            lock (stopGate)
            {
                if (stopped) return;
                try
                {
                    if (ready && !session.Failed && !HasExited)
                    {
                        try
                        {
                            session.ShutdownAsync(Math.Min(timeout, 1000), CancellationToken.None).GetAwaiter().GetResult();
                            process.WaitForExit(500);
                        }
                        catch (Exception) { }
                    }
                }
                finally { ForceTerminate(); }
                // Do not let an update/remove proceed if the owned process is still alive.
                if (!process.WaitForExit(1000))
                    throw new IOException("Не удалось подтвердить остановку процесса плагина.");
                try { process.StandardInput.BaseStream.Dispose(); } catch (Exception) { }
                try { process.StandardOutput.BaseStream.Dispose(); } catch (Exception) { }
                try { process.StandardError.BaseStream.Dispose(); } catch (Exception) { }
                process.Dispose();
                stopped = true;
            }
        }
    }

    internal sealed class PluginRuntimeState
    {
        internal string Status { get; set; }
        internal string Error { get; set; }
    }

    internal sealed class PluginRuntime : IDisposable
    {
        private sealed class Slot
        {
            internal PluginChild Child;
            internal string Status = "idle";
            internal string Error;
        }

        private sealed class Reservation : IDisposable
        {
            private SemaphoreSlim gate;
            internal Reservation(SemaphoreSlim gate) { this.gate = gate; }
            public void Dispose()
            {
                SemaphoreSlim previous = Interlocked.Exchange(ref gate, null);
                if (previous != null) previous.Release();
            }
        }

        private readonly PluginPackageStore store;
        private readonly int timeout;
        private readonly object sync = new object();
        private readonly SemaphoreSlim operations = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool closing;

        internal PluginRuntime(PluginPackageStore store, int timeoutMs = 5000)
        {
            if (timeoutMs < 100 || timeoutMs > 60000) throw new ArgumentOutOfRangeException("timeoutMs");
            this.store = store; timeout = timeoutMs;
        }

        private Slot GetSlot(string id)
        {
            lock (sync)
            {
                Slot slot;
                if (!slots.TryGetValue(id, out slot)) { slot = new Slot(); slots.Add(id, slot); }
                return slot;
            }
        }

        internal PluginRuntimeState State(string id)
        {
            lock (sync)
            {
                Slot slot;
                if (!slots.TryGetValue(id, out slot)) return new PluginRuntimeState { Status = "idle" };
                if (slot.Status == "running" && slot.Child != null && slot.Child.HasExited)
                    return new PluginRuntimeState { Status = "error", Error = "Процесс плагина завершился." };
                return new PluginRuntimeState { Status = slot.Status, Error = slot.Error };
            }
        }

        private void SetState(Slot slot, string state, string error = null)
        {
            lock (sync) { slot.Status = state; slot.Error = error; }
        }

        private void CheckOpen()
        {
            lock (sync) { if (closing) throw new OperationCanceledException("AppShelf завершает работу."); }
        }

        private void StopSlot(Slot slot)
        {
            PluginChild child;
            lock (sync) { child = slot.Child; }
            if (child != null)
            {
                SetState(slot, "stopping");
                try { child.Stop(); }
                catch (Exception error) { SetState(slot, "error", error.Message); throw; }
                lock (sync) { if (slot.Child == child) slot.Child = null; }
            }
            SetState(slot, "idle");
        }

        private void FailSlot(Slot slot, Exception error)
        {
            try { StopSlot(slot); }
            finally { SetState(slot, "error", error.Message); }
        }

        private void StartCore(string id, bool restart, bool backgroundOnly)
        {
            CheckOpen();
            Slot slot = GetSlot(id);
            if (slot.Child != null)
            {
                if (!restart && !slot.Child.HasExited) return;
                StopSlot(slot);
            }
            string executable;
            PluginManifest manifest = store.ReadEnabled(id, out executable);
            if (backgroundOnly && Array.IndexOf(manifest.Capabilities, "background") < 0) return;
            // Startup/enable scanning must never retry a failed plugin automatically.
            if (backgroundOnly && slot.Status == "error") return;
            SetState(slot, "starting");
            try
            {
                string data = store.GetDataDirectory(id);
                PluginChild child;
                lock (sync)
                {
                    if (closing) throw new OperationCanceledException();
                    child = new PluginChild(manifest, executable, timeout);
                    slot.Child = child; // track before any handshake or wait
                }
                child.Initialize(manifest, data, lifetime.Token);
                CheckOpen();
                if (child.HasExited) throw new IOException("Процесс плагина завершился при запуске.");
                SetState(slot, "running");
            }
            catch (Exception error) { FailSlot(slot, error); throw; }
        }

        private Task Run(Action action)
        {
            return Task.Run(delegate
            {
                operations.Wait(lifetime.Token);
                try { CheckOpen(); action(); }
                finally { operations.Release(); }
            });
        }

        internal Task StartAsync(string id, bool restart = false)
        {
            return Run(delegate { StartCore(id, restart, false); });
        }

        internal Task SetEnabledAsync(string id, bool enabled)
        {
            return Run(delegate
            {
                Slot slot = GetSlot(id);
                if (!enabled) StopSlot(slot);
                store.SetEnabled(id, enabled);
                if (enabled)
                {
                    SetState(slot, "idle");
                    StartCore(id, false, true);
                }
            });
        }

        internal Task RemoveAsync(string id)
        {
            return Run(delegate { StopSlot(GetSlot(id)); store.Remove(id); });
        }

        // Called on the installer worker AFTER trust confirmation, before package swap.
        // Hold the operation gate until the swap is done so no process can reopen old code.
        internal IDisposable BeforePackageCommit(string id)
        {
            operations.Wait(lifetime.Token);
            Reservation reservation = new Reservation(operations);
            try { CheckOpen(); StopSlot(GetSlot(id)); return reservation; }
            catch { reservation.Dispose(); throw; }
        }

        internal async Task StartBackgroundsAsync()
        {
            if (!store.HasInstalledDirectory) return;
            List<InstalledPlugin> installed = await Task.Run(delegate { return store.List(); }).ConfigureAwait(false);
            foreach (InstalledPlugin plugin in installed)
            {
                if (!plugin.Enabled || plugin.Error != null ||
                    Array.IndexOf(plugin.Manifest.Capabilities, "background") < 0) continue;
                try { await Run(delegate { StartCore(plugin.Id, false, true); }).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { /* Per-plugin error, not a host startup failure. */ }
            }
        }

        internal Task CheckHealthAsync()
        {
            return Task.Run(delegate
            {
                if (!operations.Wait(0)) return; // no accumulated timer work
                try
                {
                    CheckOpen();
                    List<Slot> running;
                    lock (sync) running = new List<Slot>(slots.Values);
                    foreach (Slot slot in running)
                    {
                        PluginChild child = slot.Child;
                        if (child == null || slot.Status != "running") continue;
                        try
                        {
                            if (child.HasExited) throw new IOException("Процесс плагина завершился.");
                            child.Ping(lifetime.Token);
                        }
                        catch (Exception error) { FailSlot(slot, error); }
                    }
                }
                catch (OperationCanceledException) { }
                finally { operations.Release(); }
            });
        }

        public void Dispose()
        {
            List<PluginChild> children = new List<PluginChild>();
            lock (sync)
            {
                if (closing) return;
                closing = true;
                foreach (Slot slot in slots.Values)
                    if (slot.Child != null) children.Add(slot.Child);
            }
            lifetime.Cancel();
            // Do not wait for the package/operation gate or UI callbacks on host exit.
            // Stop independent children in parallel, then force any remaining jobs closed.
            List<Task> shutdowns = new List<Task>();
            foreach (PluginChild child in children)
            {
                PluginChild owned = child;
                shutdowns.Add(Task.Run(delegate { owned.Stop(); }));
            }
            try { Task.WaitAll(shutdowns.ToArray(), 2500); } catch (AggregateException) { }
            finally { foreach (PluginChild child in children) child.ForceTerminate(); }
        }
    }
}
