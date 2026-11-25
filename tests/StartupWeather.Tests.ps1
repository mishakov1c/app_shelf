# Windows PowerShell 5.1 -STA; local sockets and isolated probe processes only.
param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-StartupWeather-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$children = @()
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs','PluginWidgetView.cs')) {
        if ($Baseline -and $file -in @('PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs','PluginWidgetView.cs')) { continue }
    if ($Baseline -and $file -in @('AppUpdate.cs','AppUpdateWindow.cs')) { continue }
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($Baseline -and $file -in @('Collections.cs','CollectionsWindow.cs')) { continue }
        if ($Baseline -and $file -eq 'Reminders.cs') {
            # Baseline AppShelf has no notification sound service.
            $text = $text.Replace('NotificationAudio.PlayReminder(_config);', '')
        }
        if ($Baseline -and $file -eq 'WindowsIntegration.cs') { continue }
        if ($Baseline -and $file -in @('AppShelf.cs','WeatherProviders.cs')) {
            $git = New-Object Diagnostics.Process
            $git.StartInfo.FileName = 'git'
            $git.StartInfo.WorkingDirectory = $root
            $git.StartInfo.Arguments = "show c104fc50b1360c92f97811ffbe53fa08d75506d2:$file"
            $git.StartInfo.UseShellExecute = $false
            $git.StartInfo.RedirectStandardOutput = $true
            $git.StartInfo.RedirectStandardError = $true
            $git.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
            try {
                [void]$git.Start()
                $text = $git.StandardOutput.ReadToEnd()
                [void]$git.StandardError.ReadToEnd()
                $git.WaitForExit()
                if ($git.ExitCode -ne 0) { throw 'Cannot read baseline.' }
            } finally { $git.Dispose() }
        }
        if ($file -eq 'AppShelf.cs') {
            $storage = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($storage), [StringSplitOptions]::None).Length -ne 2) { throw 'Data isolation anchor missing.' }
            $text = $text.Replace($storage, 'Probe.DataRoot, "AppShelf"')
            # Keep the actual entry-point lock and finally; replace only the application UI boundary.
            if ($Baseline) {
                $index = $text.LastIndexOf('            try { NetworkTls.Configure(); }')
                if ($index -lt 0) { throw 'Startup boundary missing.' }
                $text = $text.Substring(0, $index) + "            Probe.Run();`n        }`n    }`n}"
            } else {
                # Keep RunApplication (including render-failure protection) compiled unchanged.
                $boundary = '                RunApplication();'
                if ($text.Split(@($boundary), [StringSplitOptions]::None).Length -ne 2) { throw 'Startup boundary missing.' }
                $text = $text.Replace($boundary, '                Probe.Run();')
            }
            $index = $text.IndexOf('    public static class Program')
            $text = $text.Substring(0, $index) + $text.Substring($index).Replace('MessageBox.Show(', 'Probe.Notify(')
            # Unique test identity: never contend with a user's real running AppShelf.
            if (!$Baseline) {
                $mutexAnchor = 'AppShelf.Profile.'
                if ($text.Split(@($mutexAnchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Mutex isolation anchor missing.' }
                $text = $text.Replace($mutexAnchor, ('AppShelf.Test.' + [IO.Path]::GetFileName($temp) + '.'))
                $text = $text.Replace('WindowsIdentity.GetCurrent()', 'Probe.Identity()')
                $text = $text.Replace('instance.ReleaseMutex()', 'Probe.Release(instance)')
                $responseAnchor = 'request.GetResponse()'
                if ($text.Split(@($responseAnchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Response injection anchor missing.' }
                $text = $text.Replace($responseAnchor, 'Probe.Response(request)')
            }
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $text.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using AppShelf;

[DataContract] public class Payload
{
    [DataMember(Name="value")] public string Value;
    public static readonly ManualResetEvent ParseFinished = new ManualResetEvent(false);
    [OnDeserialized] private void Parsed(StreamingContext context)
    {
        if (Value != "parse-delay") return;
        try { Thread.Sleep(11000); }
        finally { ParseFinished.Set(); }
    }
}
public static class Probe
{
    public static string DataRoot;
    private static string id;
    private static bool hidden;
    private static int tlsAttempts;
    private static Microsoft.Win32.SafeHandles.SafeWaitHandle releasedHandle;
    public static void Release(Mutex mutex)
    {
        if (id == "release-error")
        {
            releasedHandle = mutex.SafeWaitHandle;
            // Drop CLR ownership bookkeeping before injecting the exception; otherwise
            // the runtime may pin its SafeHandle until this owning thread exits.
            mutex.ReleaseMutex();
            throw new InvalidOperationException("Injected release failure");
        }
        mutex.ReleaseMutex();
    }
    public static WindowsIdentity Identity()
    {
        if (id == "identity-error") throw new InvalidOperationException("Injected identity failure");
        return WindowsIdentity.GetCurrent();
    }
    public static WebResponse Response(HttpWebRequest request)
    {
        if (request.RequestUri.AbsolutePath.Contains("tls-retry") && Interlocked.Increment(ref tlsAttempts) == 1)
        {
            Thread.Sleep(6500);
            throw new WebException("Injected TLS failure", WebExceptionStatus.SecureChannelFailure);
        }
        return request.GetResponse();
    }
    public static void Notify(string text, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        File.WriteAllText(Path.Combine(DataRoot, id + ".notice"), text);
    }
    public static void Run()
    {
        if (id == "startup-error" || id == "release-error") throw new InvalidOperationException("Injected startup failure");
        Directory.CreateDirectory(ConfigStore.DirectoryPath);
        File.AppendAllText(Path.Combine(ConfigStore.DirectoryPath, "writes"), id + "\n");
        ConfigStore.Save(new AppConfig { WeatherCity = id, ShowWeather = false, ShowNetwork = false });
        ReminderStore.Save(new ReminderDocument());
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Window window = new Window { Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        if (hidden) window.Hide();
        File.WriteAllText(Path.Combine(DataRoot, id + ".ready"), window.IsVisible.ToString());
        DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += delegate { if (File.Exists(Path.Combine(DataRoot, id + ".stop"))) app.Shutdown(); };
        timer.Start();
        app.Run();
        timer.Stop();
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static int failures;
    private static void Weather(string mode)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ManualResetEvent release = new ManualResetEvent(false);
        Exception serverError = null;
        Thread server = new Thread(delegate()
        {
            try
            {
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 3000;
                    int matched = 0;
                    while (matched < 4)
                    {
                        int b = stream.ReadByte();
                        if (b < 0) return;
                        int expected = matched % 2 == 0 ? 13 : 10;
                        matched = b == expected ? matched + 1 : (b == 13 ? 1 : 0);
                    }
                    string body = "{\"value\":\"ok\"}";
                    if (mode == "parse-delay") body = "{\"value\":\"parse-delay\"}";
                    if (mode == "malformed") body = "{invalid";
                    if (mode == "limit") body += new string(' ', 2 * 1024 * 1024 - body.Length);
                    if (mode == "oversize" || mode == "unknown-size") body = new string(' ', 2 * 1024 * 1024 + 1);
                    if (mode == "headers-stall") { release.WaitOne(12000); return; }
                    if (mode == "tls-retry" && release.WaitOne(4500)) return;
                    string length = mode == "unknown-size" ? "" : "Content-Length: " +
                        ((mode == "disconnect") ? body.Length + 100 : body.Length) + "\r\n";
                    string status = mode == "http-error" ? "503 Service Unavailable" : "200 OK";
                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\n" + length + "Connection: close\r\n\r\n");
                    stream.Write(headers, 0, headers.Length);
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    if (mode == "stall") { stream.Write(bytes, 0, 1); release.WaitOne(12000); return; }
                    if (mode == "slow")
                    {
                        foreach (byte b in bytes) { stream.WriteByte(b); if (release.WaitOne(100)) return; }
                    }
                    else if (mode == "trickle")
                    {
                        foreach (byte b in bytes) { stream.WriteByte(b); if (release.WaitOne(1000)) return; }
                    }
                    else stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException) { } // Abort/oversize intentionally closes the client socket.
            catch (SocketException) { }
            catch (Exception ex) { serverError = ex; }
        });
        server.IsBackground = true;
        server.Start();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Stopwatch clock = Stopwatch.StartNew();
        long last = 0, gap = 0;
        int ticks = 0;
        DispatcherTimer heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        heartbeat.Tick += delegate { long now = clock.ElapsedMilliseconds; gap = Math.Max(gap, now - last); last = now; ticks++; };
        heartbeat.Start();
        try
        {
            MethodInfo read = typeof(WeatherClient).GetMethod("ReadJson", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(Payload));
            object[] arguments = new object[] { "http://127.0.0.1:" + port + "/" + mode, "local-test", mode == "tls-retry" };
            if (read.GetParameters().Length == 4) arguments = new object[] { arguments[0], arguments[1], arguments[2], null }; // Historical baseline.
            Task<Payload> task = (Task<Payload>)read.Invoke(null, arguments);
            DispatcherFrame frame = new DispatcherFrame();
            task.ContinueWith(delegate { dispatcher.BeginInvoke(new Action(delegate { frame.Continue = false; })); });
            Dispatcher.PushFrame(frame);
            gap = Math.Max(gap, clock.ElapsedMilliseconds - last);
            bool success = mode == "valid" || mode == "slow" || mode == "limit";
            Assert(success ? task.Status == TaskStatus.RanToCompletion && task.Result.Value == "ok" : task.IsFaulted, "Unexpected result: " + task.Status);
            Exception error = task.Exception == null ? null : task.Exception.InnerException;
            if (mode == "http-error") Assert(task.Exception.ToString().Contains("local-test: HTTP 503"), "HTTP diagnostics lost");
            bool timeout = mode == "stall" || mode == "headers-stall" || mode == "trickle" || mode == "parse-delay" || mode == "tls-retry";
            if (timeout) Assert(error is TimeoutException && clock.ElapsedMilliseconds < 11000, "Total deadline not enforced: " + clock.ElapsedMilliseconds + "ms / " + error);
            if (mode == "oversize" || mode == "unknown-size") Assert(error is InvalidDataException, "Size limit not enforced");
            if (mode == "tls-retry") Assert(tlsAttempts == 2, "TLS retry was not exercised");
            MethodInfo classify = typeof(MainWindow).GetMethod("IsExpectedWeatherNetworkFailure",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (classify != null && error != null)
            {
                object[] classification = new object[] { error, false };
                bool expected = (bool)classify.Invoke(null, classification);
                bool network = timeout || mode == "disconnect" || mode == "http-error";
                Assert(expected == network, "Incorrect network/error-data classification for " + mode + ": " + error);
            }
            Assert(gap < 700, "Dispatcher blocked for " + gap + "ms");
            if (mode == "slow" || timeout) Assert(ticks > 10, "Dispatcher timer did not keep ticking");
            Console.WriteLine("PASS weather " + mode + " elapsed=" + clock.ElapsedMilliseconds + "ms max-gap=" + gap + "ms ticks=" + ticks);
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL weather " + mode + ": " + ex.Message); }
        finally
        {
            heartbeat.Stop(); release.Set(); listener.Stop();
            Assert(server.Join(4000), "Server did not stop");
            release.Dispose();
            if (serverError != null) throw serverError;
            if (mode == "parse-delay") Assert(Payload.ParseFinished.WaitOne(4000), "Late parser did not finish");
        }
    }
    [STAThread] public static int Main(string[] args)
    {
        DataRoot = args[1];
        if (args[0] == "mutex-check")
        {
            id = args[2];
            string name = "Global\\AppShelf.Test." + Path.GetFileName(DataRoot) + ".";
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) name += identity.User.Value;
            if (id == "identity-error") Program.Main();
            else
            {
                MutexSecurity security = new MutexSecurity();
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    security.AddAccessRule(new MutexAccessRule(identity.User, MutexRights.FullControl, AccessControlType.Allow));
                if (id == "access-error")
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        security.AddAccessRule(new MutexAccessRule(identity.User, MutexRights.FullControl, AccessControlType.Deny));
                }
                bool created;
                using (Mutex observer = new Mutex(false, name, out created, security))
                {
                    try { Program.Main(); }
                    catch (InvalidOperationException ex)
                    {
                        if (id != "release-error" || ex.Message != "Injected release failure") throw;
                    }
                    if (id == "release-error") Assert(releasedHandle != null && releasedHandle.IsClosed, "Mutex handle leaked after a release exception");
                    if (id == "startup-error")
                    {
                        Assert(observer.WaitOne(0), "Startup exception did not release ownership");
                        observer.ReleaseMutex();
                    }
                }
            }
            Assert(File.Exists(Path.Combine(DataRoot, id + ".notice")), "Failure did not produce a notification");
            string notice = File.ReadAllText(Path.Combine(DataRoot, id + ".notice"));
            if (id == "startup-error" || id == "release-error") Assert(notice.Contains("Injected startup failure"), "Startup exception path was not exercised");
            if (id == "identity-error") Assert(notice.Contains("Injected identity failure"), "Identity exception path was not exercised");
            Assert(!File.Exists(Path.Combine(DataRoot, id + ".ready")), "Failure entered the working application");
            Console.WriteLine("PASS mutex " + id);
            return 0;
        }
        if (args[0] == "instance")
        {
            id = args[2]; hidden = args[3] == "hidden";
            Program.Main(); return 0;
        }
        foreach (string mode in new string[] { "valid", "slow", "stall", "headers-stall", "trickle", "limit", "oversize", "unknown-size", "malformed", "disconnect", "http-error" }) Weather(mode);
        if (args.Length > 2) { Weather("parse-delay"); Weather("tls-retry"); }
        return failures == 0 ? 0 : 1;
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = Join-Path $temp 'Probe.exe'
    $options.CompilerOptions = '/main:Probe'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll',
        'System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xml.dll','System.Xaml.dll',
        'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    function Start-Probe($id, $visibility) {
        $p = Start-Process $options.OutputAssembly -ArgumentList @('instance', ('"' + $temp + '"'), $id, $visibility) -PassThru
        $script:children += $p
        return $p
    }
    function Wait-Ready($id) {
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        while (!(Test-Path (Join-Path $temp "$id.ready")) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 30 }
        if (!(Test-Path (Join-Path $temp "$id.ready"))) { throw "No startup: $id" }
    }
    function Data-Snapshot {
        return ((Get-ChildItem (Join-Path $temp 'AppShelf') -File -Recurse | Sort-Object FullName | ForEach-Object {
            # Avoid depending on PowerShell module discovery in child CI processes.
            $sha256 = [Security.Cryptography.SHA256]::Create()
            $stream = $null
            try {
                $stream = [IO.File]::OpenRead($_.FullName)
                $hash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
                $_.FullName + ':' + $_.LastWriteTimeUtc.Ticks + ':' + $hash
            } finally {
                if ($stream) { $stream.Dispose() }
                $sha256.Dispose()
            }
        }) -join "`n")
    }
    $failed = 0
    foreach ($visibility in @('open','hidden')) {
        $firstId = $visibility + '1'
        $first = Start-Probe $firstId $visibility
        Wait-Ready $firstId
        $expectedVisible = if ($visibility -eq 'open') { 'True' } else { 'False' }
        if ([IO.File]::ReadAllText((Join-Path $temp "$firstId.ready")) -ne $expectedVisible) { throw 'Incorrect window state.' }
        $writes = Join-Path $temp 'AppShelf\writes'
        $before = [IO.File]::ReadAllText($writes)
        $snapshot = Data-Snapshot
        $secondId = $visibility + '2'
        $second = Start-Probe $secondId $visibility
        $exited = $second.WaitForExit(2500)
        if (!$exited -or !(Test-Path (Join-Path $temp "$secondId.notice")) -or [IO.File]::ReadAllText($writes) -ne $before -or (Data-Snapshot) -ne $snapshot) {
            $failed++; Write-Output "FAIL second instance/${visibility}: not rejected or wrote data"
            if (!$second.HasExited) { $second.Kill(); $second.WaitForExit() }
        } else { Write-Output "PASS second instance/${visibility}: rejected, no writes" }
        [IO.File]::WriteAllText((Join-Path $temp "$firstId.stop"), '')
        if (!$first.WaitForExit(4000)) { throw 'Graceful exit failed.' }
        $thirdId = $visibility + '3'
        $third = Start-Probe $thirdId $visibility
        Wait-Ready $thirdId
        Write-Output "PASS graceful lock release/$visibility"
        # Keep the kernel object alive so recovery exercises AbandonedMutexException,
        # not just recreation after the last handle disappears.
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $mutexName = 'Global\AppShelf.Test.' + [IO.Path]::GetFileName($temp) + '.' + $sid
        $observer = New-Object Threading.Mutex($false, $mutexName)
        try {
            $third.Kill(); $third.WaitForExit()
            $fourthId = $visibility + '4'
            $fourth = Start-Probe $fourthId $visibility
            Wait-Ready $fourthId
        } finally { $observer.Dispose() }
        Write-Output "PASS abandoned lock recovery/$visibility"
        [IO.File]::WriteAllText((Join-Path $temp "$fourthId.stop"), '')
        if (!$fourth.WaitForExit(4000)) { throw 'Recovered process did not exit.' }
    }
    $race = @()
    1..6 | ForEach-Object { $race += Start-Probe "race$_" hidden }
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $ready = @(Get-ChildItem $temp -Filter 'race*.ready')
        $notices = @(Get-ChildItem $temp -Filter 'race*.notice')
        if ($ready.Count + $notices.Count -eq 6) { break }
        Start-Sleep -Milliseconds 30
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($ready.Count -ne 1 -or $notices.Count -ne 5) {
        $failed++; Write-Output 'FAIL competing startup: expected one owner and five rejections'
    } else { Write-Output 'PASS competing startup: one owner, five rejections' }
    foreach ($p in $race) { if (!$p.HasExited) { $p.Kill(); $p.WaitForExit() } }
    if (!$Baseline) {
        foreach ($fault in @('access-error','identity-error','startup-error','release-error')) {
            $snapshot = Data-Snapshot
            & $options.OutputAssembly mutex-check $temp $fault
            if ($LASTEXITCODE -ne 0 -or (Data-Snapshot) -ne $snapshot) { throw "Mutex fault check failed: $fault" }
        }
    }
    $weatherArguments = @('weather', $temp)
    if (!$Baseline) { $weatherArguments += 'concluding-checks' }
    & $options.OutputAssembly @weatherArguments
    if ($LASTEXITCODE -ne 0) { $failed++ }
    if ($failed) { throw "Startup/weather failures: $failed. Data: $temp" }
    Write-Output "PASS startup/weather tests. Isolated data: $temp"
}
finally {
    foreach ($p in $children) { if (!$p.HasExited) { $p.Kill(); $p.WaitForExit() }; $p.Dispose() }
    $provider.Dispose()
}
