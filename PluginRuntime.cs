// Trusted plugin process ownership and lifecycle. Windows only; not a sandbox.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
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
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
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

        internal void Attach(SafeFileHandle process)
        {
            if (!AssignProcessToJobObject(handle, process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            SafeFileHandle previous = Interlocked.Exchange(ref handle, null);
            if (previous != null) previous.Dispose();
        }
    }

    // Raw Win32 pipes avoid the .NET Framework Process.StandardInput StreamWriter
    // preamble. The suspended process joins the kill-on-close job before it can run.
    internal sealed class NativePluginProcess : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
            public ushort ShowWindow, ReservedLength;
            public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo Startup;
            public IntPtr Attributes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo
        {
            public IntPtr Process, Thread;
            public uint ProcessId, ThreadId;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write,
            ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
            IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags,
            IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(SafeFileHandle process, uint code);

        private SafeFileHandle handle;
        internal Stream Input { get; private set; }
        internal Stream Output { get; private set; }
        internal Stream Error { get; private set; }
        internal int Id { get; private set; }

        private static void Require(bool result)
        {
            if (!result) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void Release(SafeFileHandle handle)
        {
            if (handle != null) handle.Dispose();
        }

        internal NativePluginProcess(string executable, PluginJob job)
        {
            SafeFileHandle childIn = null, childOut = null, childErr = null;
            SafeFileHandle parentIn = null, parentOut = null, parentErr = null, thread = null;
            IntPtr attributes = IntPtr.Zero, inherited = IntPtr.Zero;
            bool attributesReady = false;
            try
            {
                SecurityAttributes sa = new SecurityAttributes
                { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
                Require(CreatePipe(out childIn, out parentIn, ref sa, 0));
                Require(CreatePipe(out parentOut, out childOut, ref sa, 0));
                Require(CreatePipe(out parentErr, out childErr, ref sa, 0));
                Require(SetHandleInformation(parentIn, 1, 0));
                Require(SetHandleInformation(parentOut, 1, 0));
                Require(SetHandleInformation(parentErr, 1, 0));

                // Pass only the three child pipe handles. No unrelated inheritable
                // handles (in particular no job handles) may leak into the plugin.
                UIntPtr size = UIntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == UIntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                attributes = Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
                Require(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                attributesReady = true;
                inherited = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(inherited, 0, childIn.DangerousGetHandle());
                Marshal.WriteIntPtr(inherited, IntPtr.Size, childOut.DangerousGetHandle());
                Marshal.WriteIntPtr(inherited, IntPtr.Size * 2, childErr.DangerousGetHandle());
                Require(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020002),
                    inherited, new UIntPtr((uint)(IntPtr.Size * 3)), IntPtr.Zero, IntPtr.Zero));

                StartupInfoEx startup = new StartupInfoEx();
                startup.Startup.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                startup.Startup.Flags = 0x100; // STARTF_USESTDHANDLES
                startup.Startup.StdInput = childIn.DangerousGetHandle();
                startup.Startup.StdOutput = childOut.DangerousGetHandle();
                startup.Startup.StdError = childErr.DangerousGetHandle();
                startup.Attributes = attributes;
                ProcessInfo info;
                // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED
                Require(CreateProcess(executable, new StringBuilder("\"" + executable + "\""),
                    IntPtr.Zero, IntPtr.Zero, true, 0x08080004, IntPtr.Zero,
                    Path.GetDirectoryName(executable), ref startup, out info));
                handle = new SafeFileHandle(info.Process, true);
                thread = new SafeFileHandle(info.Thread, true);
                Id = checked((int)info.ProcessId);
                job.Attach(handle);
                Release(childIn); childIn = null;
                Release(childOut); childOut = null;
                Release(childErr); childErr = null;
                Input = new FileStream(parentIn, FileAccess.Write, 4096, false); parentIn = null;
                Output = new FileStream(parentOut, FileAccess.Read, 4096, false); parentOut = null;
                Error = new FileStream(parentErr, FileAccess.Read, 4096, false); parentErr = null;
                if (ResumeThread(thread) == UInt32.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch
            {
                Kill();
                Dispose();
                throw;
            }
            finally
            {
                Release(thread);
                Release(childIn); Release(childOut); Release(childErr);
                Release(parentIn); Release(parentOut); Release(parentErr);
                if (attributesReady) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (inherited != IntPtr.Zero) Marshal.FreeHGlobal(inherited);
            }
        }

        internal bool HasExited { get { return WaitForExit(0); } }

        internal bool WaitForExit(int milliseconds)
        {
            SafeFileHandle owned = handle;
            if (owned == null || owned.IsClosed) return true;
            uint result;
            try { result = WaitForSingleObject(owned, (uint)milliseconds); }
            catch (ObjectDisposedException) { return true; }
            if (result == 0) return true;
            if (result == 258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal void Kill()
        {
            SafeFileHandle owned = handle;
            if (owned == null || owned.IsClosed) return;
            try { TerminateProcess(owned, 1); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            try { if (Input != null) Input.Dispose(); } catch (Exception) { }
            try { if (Output != null) Output.Dispose(); } catch (Exception) { }
            try { if (Error != null) Error.Dispose(); } catch (Exception) { }
            Release(Interlocked.Exchange(ref handle, null));
        }
    }

    internal sealed class PluginChild
    {
        private readonly NativePluginProcess process;
        private readonly PluginJob job;
        private readonly PluginProtocolSession session;
        private readonly int timeout;
        private readonly object stopGate = new object();
        private bool stopped;
        private volatile bool ready;
        internal PluginCommand[] Commands { get; private set; }
        internal int ProcessId { get; private set; }
        internal PluginManifest Manifest { get; private set; }

        internal PluginChild(PluginManifest manifest, string executable, int timeoutMs)
        {
            timeout = timeoutMs;
            Manifest = manifest;
            job = new PluginJob();
            try
            {
                process = new NativePluginProcess(executable, job);
                ProcessId = process.Id;
                session = new PluginProtocolSession(process.Input, process.Output,
                    ForceTerminate, manifest.Id, manifest.Version, manifest.Capabilities);
                DrainErrors(process.Error);
            }
            catch
            {
                job.Dispose();
                if (process != null) { process.Kill(); process.Dispose(); }
                throw;
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

        internal void Invoke(string id, CancellationToken cancel)
        {
            session.InvokeAsync(id, timeout, cancel).GetAwaiter().GetResult();
        }

        internal void OpenSettings(CancellationToken cancel)
        {
            session.SettingsAsync(timeout, cancel).GetAwaiter().GetResult();
        }

        internal PluginWidget[] GetWidgets(CancellationToken cancel)
        {
            return session.GetWidgetsAsync(timeout, cancel).GetAwaiter().GetResult().Widgets;
        }

        internal PluginNotice[] Poll(CancellationToken cancel)
        {
            return session.PollAsync(timeout, cancel).GetAwaiter().GetResult().Notifications ?? new PluginNotice[0];
        }

        internal bool Failed { get { return session.Failed; } }

        internal void ForceTerminate()
        {
            job.Dispose();
            try { process.Kill(); }
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
                process.Dispose();
                stopped = true;
            }
        }
    }

    internal sealed class PluginWidgetSnapshot
    {
        internal int ProcessId { get; set; }
        internal string Version { get; set; }
        internal PluginWidget[] Widgets { get; set; }
    }

    internal sealed class PluginRuntimeState
    {
        internal string Status { get; set; }
        internal string Error { get; set; }
        internal string Version { get; set; }
        internal int ProcessId { get; set; }
        internal PluginCommand[] Commands { get; set; }
    }

    internal sealed class PluginDelivery
    {
        internal string Id { get; set; }
        internal string Version { get; set; }
        internal string Name { get; set; }
        internal int ProcessId { get; set; }
        internal PluginNotice Notice { get; set; }
    }

    internal sealed class PluginRuntime : IDisposable
    {
        private sealed class Slot
        {
            internal PluginChild Child;
            internal string Status = "idle";
            internal string Error;
            internal DateTime LastNoticeUtc;
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
                return new PluginRuntimeState { Status = slot.Status, Error = slot.Error,
                    Version = slot.Child == null ? null : slot.Child.Manifest.Version,
                    ProcessId = slot.Child == null ? 0 : slot.Child.ProcessId,
                    Commands = slot.Status == "running" && slot.Child != null ?
                        CopyCommands(slot.Child.Commands) : new PluginCommand[0] };
            }
        }

        private static PluginCommand[] CopyCommands(PluginCommand[] source)
        {
            if (source == null) return new PluginCommand[0];
            PluginCommand[] copy = new PluginCommand[source.Length];
            for (int i = 0; i < source.Length; i++)
                copy[i] = new PluginCommand { Id = source[i].Id, Label = source[i].Label };
            return copy;
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
            string executable;
            PluginManifest manifest = store.ReadEnabled(id, out executable);
            if (backgroundOnly && Array.IndexOf(manifest.Capabilities, "background") < 0) return;
            if (slot.Child != null)
            {
                if (!restart && slot.Status == "running" && !slot.Child.HasExited &&
                    slot.Child.Manifest.Version == manifest.Version) return;
                StopSlot(slot);
            }
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

        // A click from a stale menu must not execute a different version of a plugin.
        private PluginManifest CheckActionVersion(string id, string version)
        {
            string executable;
            PluginManifest manifest = store.ReadEnabled(id, out executable);
            if (manifest.Version != version)
                throw new InvalidOperationException("Версия плагина изменилась. Откройте меню заново.");
            return manifest;
        }

        internal async Task<PluginCommand[]> GetCommandsAsync(string id, string version)
        {
            PluginCommand[] result = null;
            await Run(delegate
            {
                PluginManifest manifest = CheckActionVersion(id, version);
                if (Array.IndexOf(manifest.Capabilities, "command") < 0 &&
                    Array.IndexOf(manifest.Capabilities, "window") < 0)
                    throw new InvalidOperationException("Плагин не объявляет команды.");
                StartCore(id, false, false);
                result = CopyCommands(GetSlot(id).Child.Commands);
            }).ConfigureAwait(false);
            return result;
        }

        private void ExecuteAction(string id, string version, string command, bool settings)
        {
            PluginManifest manifest = CheckActionVersion(id, version);
            if (settings && Array.IndexOf(manifest.Capabilities, "settings") < 0)
                throw new InvalidOperationException("Плагин не предоставляет настройки.");
            if (!settings && (String.IsNullOrEmpty(command) ||
                (Array.IndexOf(manifest.Capabilities, "command") < 0 &&
                 Array.IndexOf(manifest.Capabilities, "window") < 0)))
                throw new InvalidOperationException("Плагин не объявляет команды.");
            StartCore(id, false, false);
            Slot slot = GetSlot(id);
            PluginChild child = slot.Child;
            if (!settings && !Array.Exists(child.Commands, delegate(PluginCommand item) { return item.Id == command; }))
                throw new InvalidOperationException("Команда недоступна. Откройте меню заново.");
            try
            {
                if (settings) child.OpenSettings(lifetime.Token);
                else child.Invoke(command, lifetime.Token);
            }
            catch (Exception error)
            {
                if (child.Failed || child.HasExited) FailSlot(slot, error);
                throw;
            }
        }

        // A snapshot and its process identity are captured under the same runtime gate.
        internal async Task<PluginWidgetSnapshot> ReadWidgetSnapshotAsync(string id, string version, bool activate)
        {
            PluginWidgetSnapshot result = null;
            await Run(delegate
            {
                string executable;
                // Only an explicit activation may select the current installed version.
                PluginManifest manifest = activate && version == null
                    ? store.ReadEnabled(id, out executable) : CheckActionVersion(id, version);
                if (Array.IndexOf(manifest.Capabilities, "widget") < 0)
                    throw new InvalidOperationException("Плагин не предоставляет виджеты.");
                if (activate) StartCore(id, false, false);
                Slot slot = GetSlot(id);
                PluginChild child = slot.Child;
                if (slot.Status != "running" || child == null || child.HasExited ||
                    child.Manifest.Version != manifest.Version)
                    throw new InvalidOperationException("Плагин виджетов не запущен.");
                try
                {
                    result = new PluginWidgetSnapshot { ProcessId = child.ProcessId, Version = manifest.Version,
                        Widgets = child.GetWidgets(lifetime.Token) };
                }
                catch (Exception error)
                {
                    if (child.Failed || child.HasExited) FailSlot(slot, error);
                    throw;
                }
            }).ConfigureAwait(false);
            return result;
        }

        internal async Task<PluginWidget[]> GetWidgetsAsync(string id, string version)
        {
            return (await ReadWidgetSnapshotAsync(id, version, false).ConfigureAwait(false)).Widgets;
        }

        // A stale widget must never start a child or invoke a replacement process.
        internal Task InvokeWidgetAsync(string id, string version, int processId, string command)
        {
            return Run(delegate
            {
                PluginManifest manifest = CheckActionVersion(id, version);
                Slot slot = GetSlot(id);
                PluginChild child = slot.Child;
                if (Array.IndexOf(manifest.Capabilities, "widget") < 0 ||
                    slot.Status != "running" || child == null || child.HasExited ||
                    child.ProcessId != processId || child.Manifest.Version != version ||
                    !Array.Exists(child.Commands, delegate(PluginCommand item) { return item.Id == command; }))
                    throw new InvalidOperationException("Виджет устарел. Обновите его.");
                try { child.Invoke(command, lifetime.Token); }
                catch (Exception error)
                {
                    if (child.Failed || child.HasExited) FailSlot(slot, error);
                    throw;
                }
            });
        }

        internal Task InvokeAsync(string id, string version, string command)
        {
            return Run(delegate { ExecuteAction(id, version, command, false); });
        }

        internal Task OpenSettingsAsync(string id, string version)
        {
            return Run(delegate { ExecuteAction(id, version, null, true); });
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

        internal Task RestoreDataAsync(string id, string version, string backupRoot)
        {
            return Run(delegate
            {
                StopSlot(GetSlot(id));
                store.ApplyRecoveredData(id, version, backupRoot);
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

        internal Task<List<PluginDelivery>> CheckHealthAsync()
        {
            return Task.Run(delegate
            {
                List<PluginDelivery> deliveries = new List<PluginDelivery>();
                if (!operations.Wait(0)) return deliveries; // no accumulated timer work
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
                            if (Array.IndexOf(child.Manifest.Capabilities, "notification") >= 0)
                            {
                                // Poll drains the plugin's batch. A malicious or noisy trusted
                                // plugin must not flood the desktop or an unbounded queue.
                                PluginNotice[] notices;
                                try { notices = child.Poll(lifetime.Token); }
                                catch (PluginRemoteException) { continue; }
                                DateTime now = DateTime.UtcNow;
                                if (notices.Length > 0 && deliveries.Count < 8 &&
                                    now - slot.LastNoticeUtc >= TimeSpan.FromSeconds(15))
                                {
                                    slot.LastNoticeUtc = now;
                                    deliveries.Add(new PluginDelivery { Id = child.Manifest.Id,
                                        Version = child.Manifest.Version, Name = child.Manifest.Name, ProcessId = child.ProcessId, Notice = notices[0] });
                                }
                            }
                        }
                        catch (Exception error) { FailSlot(slot, error); }
                    }
                }
                catch (OperationCanceledException) { }
                finally { operations.Release(); }
                return deliveries;
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
