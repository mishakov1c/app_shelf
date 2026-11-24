# Windows PowerShell 5.1 -STA. Real WPF/isolated HKCU key; no sounds or production Run writes.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$id = [Guid]::NewGuid().ToString('N')
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-StartupSound-' + $id)
$data = Join-Path $temp 'TestData'
[void][IO.Directory]::CreateDirectory($data)
$testKey = 'Software\AppShelf.Tests\Package8-' + $id
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $data
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Storage isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $errorDialog = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $errorDialog).Count -ne 1) { throw 'Error boundary missing.' }
            $text = [regex]::Replace($text, $errorDialog, 'StartupSoundTests.Errors++;')
            $anchor = 'bool? savedSettings = dialog.ShowDialog();'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Settings boundary missing.' }
            $text = $text.Replace($anchor, 'bool? savedSettings = StartupSoundTests.Settings(dialog);')
            $text = $text.Replace('new Forms.NotifyIcon()', 'StartupSoundTests.NewTray()')
            $text = $text.Replace('_trayIcon = StartupSoundTests.NewTray();', '_trayIcon = StartupSoundTests.NewTray(); StartupSoundTests.AfterTrayCreated();')
            $text = $text.Replace('Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName)', 'StartupSoundTests.TrayImage()')
            # Replace the invocation only: retain the real WPF handler and its guard in the assembly.
            $anchor = '                RunApplication();'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Entry-point boundary missing.' }
            $text = $text.Replace($anchor, '                StartupSoundTests.ChildRun();')
            $index = $text.IndexOf('    public static class Program')
            $text = $text.Substring(0, $index) + $text.Substring($index).Replace('MessageBox.Show(', 'StartupSoundTests.Notice(')
            $text = $text.Replace('AppShelf.Profile.', ('AppShelf.Package8.' + $id + '.'))
        }
        if ($file -eq 'WindowsIntegration.cs') {
            $anchor = 'Software\Microsoft\Windows\CurrentVersion\Run'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Registry isolation anchor missing.' }
            $text = $text.Replace($anchor, $testKey)
            $text = $text.Replace('Process.GetCurrentProcess().MainModule.FileName', 'StartupSoundTests.Executable')
            $text = $text.Replace('            string value = null;', '            StartupSoundTests.BeforeRead(); string value = null;')
            $text = $text.Replace('public static void WriteRaw(string command)' + "`r`n" + '        {', 'public static void WriteRaw(string command)' + "`r`n" + '        { StartupSoundTests.BeforeWrite();')
            # Also supports LF sources.
            $text = $text.Replace('public static void WriteRaw(string command)' + "`n" + '        {', 'public static void WriteRaw(string command)' + "`n" + '        { StartupSoundTests.BeforeWrite();')
            if (-not $text.Contains('{ StartupSoundTests.BeforeWrite();')) { throw 'Registry fault boundary missing.' }
            $text = $text.Replace('key.SetValue(AppValueName, command, RegistryValueKind.String);', 'key.SetValue(AppValueName, command, RegistryValueKind.String); StartupSoundTests.AfterWrite();')
            $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', 'StartupSoundTests.PomodoroSounds++;')
            $text = $text.Replace('System.Media.SystemSounds.Exclamation.Play();', 'StartupSoundTests.ReminderSounds++;')
        }
        $text = [regex]::Replace($text, '_trayIcon.ShowBalloonTip\([^;]+\);', 'StartupSoundTests.Balloons++;')
        $sources += $text.Replace('SiteLinks.FetchFavicon(site)', 'false')
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;
using Forms = System.Windows.Forms;

public static class StartupSoundTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static string Executable = @"C:\Test Applications\App Shelf.exe";
    public static string TestExecutable;
    public static int Errors, Writes, PomodoroSounds, ReminderSounds, Balloons;
    public static bool FailWrite, FailAfterWrite, FailRead, FailTray, NullIcon, FailTrayAfterCreate;
    private static int passed, failed;
    private static Action<Window> settings;
    private static bool saveSettings;
    public static void BeforeRead() { if (FailRead) throw new UnauthorizedAccessException("Injected registry read failure"); }
    public static void BeforeWrite() { Writes++; if (FailWrite) { FailWrite = false; throw new UnauthorizedAccessException("Injected registry write failure"); } }
    public static void AfterWrite() { if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("Injected partial registry write"); } }
    public static Forms.NotifyIcon NewTray() { if (FailTray) throw new InvalidOperationException("Injected unavailable tray"); return new Forms.NotifyIcon(); }
    public static void AfterTrayCreated() { if (FailTrayAfterCreate) throw new InvalidOperationException("Injected partial tray initialization failure"); }
    public static System.Drawing.Icon TrayImage() { return NullIcon ? null : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone(); }
    public static MessageBoxResult Notice(string text, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "notice.txt"), text); return MessageBoxResult.OK;
    }
    public static void ChildRun()
    {
        string folder = Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA");
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--render-failure") >= 0)
        {
            RenderFailureProbe(folder);
            return;
        }
        File.WriteAllText(Path.Combine(folder, "started.txt"), WindowsStartup.IsTrayLaunch().ToString());
        if (Environment.GetEnvironmentVariable("APPSHELF_HOLD_TEST") == "1")
            using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\AppShelf.Package8." + new DirectoryInfo(folder).Name)) stop.WaitOne(20000);
    }
    private static void RenderFailureProbe(string folder)
    {
        // Invoke the compiled production callback in a disposable process, not the user's app.
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exits = 0, exitCode = 0;
        DispatcherFrame shutdownFrame = new DispatcherFrame();
        app.Exit += delegate(object sender, ExitEventArgs e)
        {
            exits++; exitCode = e.ApplicationExitCode; shutdownFrame.Continue = false;
        };
        MethodInfo callback = null;
        foreach (Type type in typeof(Program).Assembly.GetTypes())
        {
            if (!type.FullName.StartsWith("AppShelf.Program", StringComparison.Ordinal)) continue;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[1].ParameterType == typeof(DispatcherUnhandledExceptionEventArgs))
                    callback = method;
            }
        }
        Assert(callback != null, "Production render-failure callback missing");
        object target = callback.IsStatic ? null : Activator.CreateInstance(callback.DeclaringType, true);
        if (target != null)
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.FieldType == typeof(Application)) field.SetValue(target, app);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Exception error = new System.Runtime.InteropServices.COMException("UCEERR_RENDERTHREADFAILURE-test",
                unchecked((int)0x88980406));
            object args = Activator.CreateInstance(typeof(DispatcherUnhandledExceptionEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { Dispatcher.CurrentDispatcher }, null);
            FieldInfo exceptionField = typeof(DispatcherUnhandledExceptionEventArgs).GetField("_exception",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(exceptionField != null, "Runtime dispatcher exception boundary missing");
            exceptionField.SetValue(args, error);
            callback.Invoke(target, new object[] { app, args });
            Assert(((DispatcherUnhandledExceptionEventArgs)args).Handled, "Render failure was not handled");
        }
        // Shutdown is dispatched asynchronously; let the real queued shutdown callback run.
        DispatcherTimer deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        deadline.Tick += delegate { shutdownFrame.Continue = false; };
        deadline.Start();
        if (exits == 0) Dispatcher.PushFrame(shutdownFrame);
        deadline.Stop();
        Assert(exits == 1 && exitCode == -1, "Render failure did not shut down once with failure exit code");
        string log = File.ReadAllText(ConfigStore.LogPath);
        Assert(log.Split(new string[] { "UCEERR_RENDERTHREADFAILURE-test" }, StringSplitOptions.None).Length == 2,
            "Repeated render failure was logged more than once");
        string notice = File.ReadAllText(Path.Combine(folder, "notice.txt"));
        Assert(notice.Split(new string[] { "WPF" }, StringSplitOptions.None).Length == 2,
            "Repeated render failure showed more than one notice");
        File.WriteAllText(Path.Combine(folder, "wpf-pass.txt"), "PASS");
    }
    public static bool Settings(Window d)
    {
        Exception error = null;
        d.ShowInTaskbar = false;
        d.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
            try { settings(d); if (d.IsVisible) Click(ButtonNamed(d, saveSettings ? "Сохранить" : "Отмена")); }
            catch (Exception ex) { error = ex; d.Close(); }
        }));
        bool result = d.ShowDialog() == true; Drain(); if (error != null) throw error; return result;
    }
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static void Set(MainWindow w, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(w, value); }
    private static object Call(MainWindow w, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, args); } catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static AppConfig Live(MainWindow w) { return (AppConfig)Get(w, "_config"); }
    private static AppConfig Load() { string warning; bool safe; AppConfig c = ConfigStore.Load(out warning, out safe); Assert(safe && warning == null, "Config load failed"); return c; }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> result = new List<T>(); if (root is T) result.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject) result.AddRange(Controls<T>((DependencyObject)child)); return result;
    }
    private static Button ButtonNamed(Window d, string text) { foreach (Button b in Controls<Button>(d)) if (Convert.ToString(b.Content) == text) return b; throw new Exception("Button missing: " + text); }
    private static CheckBox Check(Window d, string text) { foreach (CheckBox c in Controls<CheckBox>(d)) if (Convert.ToString(c.Content) == text) return c; throw new Exception("Checkbox missing: " + text); }
    private static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain(); }
    private static void Seed(bool maximized)
    {
        FailWrite = FailAfterWrite = FailRead = FailTray = NullIcon = FailTrayAfterCreate = false;
        ConfigStore.Save(new AppConfig { ShowNetwork = false, ShowWeather = false, RdpKeepAwake = false,
            WindowPlacementSaved = true, WindowLeft = 80, WindowTop = 90, WindowWidth = 720, WindowHeight = 510, WindowMaximized = maximized, MinimizeToTray = true, CloseToTray = false });
        ReminderStore.Save(new ReminderDocument()); WindowsStartup.WriteRaw(null); Writes = 0;
    }
    private static MainWindow Open(bool tray, bool maximized)
    {
        MainWindow w = new MainWindow(tray);
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w, typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        w.Show(); Drain(); return w;
    }
    private static void Edit(MainWindow w, bool save, Action<Window> action) { saveSettings = save; settings = action; Call(w, "ShowSettings"); }
    private static void ForceExit(MainWindow w) { Set(w, "_explicitExitRequested", true); w.Close(); }
    private static void Startup(Window d, bool enabled, bool tray)
    {
        Check(d, "Запускать AppShelf при входе в Windows").IsChecked = enabled;
        Check(d, "При автозапуске сразу скрывать в трей").IsChecked = tray;
    }
    private static void Picture(FrameworkElement view, string name)
    {
        view.UpdateLayout(); RenderTargetBitmap image = new RenderTargetBitmap((int)Math.Ceiling(view.RenderSize.Width), (int)Math.Ceiling(view.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new DrawingVisual(); using (DrawingContext dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(view) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(view.RenderSize) }, null, new Rect(view.RenderSize));
        image.Render(visual); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (FileStream s = File.Create(Path.Combine(new DirectoryInfo(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA")).Parent.FullName, name + ".png"))) encoder.Save(s);
    }
    private static void Case(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static Process Child(string folder, string args, bool hold)
    {
        Directory.CreateDirectory(folder); ProcessStartInfo info = new ProcessStartInfo(TestExecutable, args) { UseShellExecute = false, CreateNoWindow = true };
        info.EnvironmentVariables["APPSHELF_TEST_DATA"] = folder; info.EnvironmentVariables["APPSHELF_HOLD_TEST"] = hold ? "1" : "0"; return Process.Start(info);
    }
    private static void WaitStarted(Process p, string path)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!File.Exists(path) && !p.HasExited && watch.ElapsedMilliseconds < 5000) Thread.Sleep(20); Assert(File.Exists(path), "Child entry point did not start");
    }
    public static void Run()
    {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Data isolation failed");
        Application app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; FluentUi.Install(app);
        Case("enable/disable quoted startup command and tray argument", delegate {
            Seed(false); WindowsStartup.Apply(true, false); WindowsStartup.State state = WindowsStartup.Read(); Assert(state.RawValue == "\"" + Executable + "\"" && state.PointsToCurrentExe && !state.InTray, "Normal command incorrect");
            WindowsStartup.Apply(true, true); state = WindowsStartup.Read(); Assert(state.RawValue == "\"" + Executable + "\" --tray" && state.InTray && state.PointsToCurrentExe, "Tray command incorrect"); WindowsStartup.Apply(false, true); Assert(!WindowsStartup.Read().Enabled, "Disable failed");
        });
        foreach (bool hidden in new bool[] { false, true }) { bool h = hidden; Case("settings save startup hidden=" + h, delegate {
            Seed(false); MainWindow w = Open(false, false); try { Edit(w, true, delegate(Window d) { Startup(d, true, h); }); Assert(WindowsStartup.Read().Enabled && WindowsStartup.Read().InTray == h && Writes == 1, "Settings startup not applied once"); Edit(w, true, delegate(Window d) { Startup(d, false, h); }); Assert(!WindowsStartup.Read().Enabled, "Settings disable failed"); } finally { w.Close(); }
        }); }
        Case("unrelated settings do not rewrite registry, moved EXE updates it", delegate {
            Seed(false); WindowsStartup.Apply(true, true); Writes = 0; MainWindow w = Open(false, false); string original = Executable;
            try { Edit(w, true, delegate(Window d) { Check(d, "Включить звуки AppShelf").IsChecked = false; }); Assert(Writes == 0, "Unrelated save rewrote Run value"); Executable = @"C:\Moved Application\AppShelf.exe"; Edit(w, true, delegate(Window d) {}); Assert(Writes == 1 && WindowsStartup.Read().PointsToCurrentExe, "Moved EXE path not updated"); } finally { Executable = original; w.Close(); }
        });
        Case("cancel does not apply registry or audio config", delegate {
            Seed(false); MainWindow w = Open(false, false); try { Edit(w, false, delegate(Window d) { Startup(d, true, true); Check(d, "Включить звуки AppShelf").IsChecked = false; }); Assert(Writes == 0 && !WindowsStartup.Read().Enabled && Live(w).SoundsEnabled == true && Load().SoundsEnabled == true, "Cancel applied changes"); } finally { w.Close(); }
        });
        foreach (string failure in new string[] { "registry", "partial", "config" }) { string f = failure; Case("atomic settings failure " + f, delegate {
            Seed(false); string old = "\"C:\\Old App\\AppShelf.exe\" --tray"; WindowsStartup.WriteRaw(old); MainWindow w = Open(false, false);
            try { string before = File.ReadAllText(ConfigStore.FilePath); int errors = Errors;
                Action change = delegate { Edit(w, true, delegate(Window d) { Startup(d, true, false); Check(d, "Включить звуки AppShelf").IsChecked = false; }); };
                if (f == "config") using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) change();
                else { FailWrite = f == "registry"; FailAfterWrite = f == "partial"; change(); }
                Assert(Errors > errors && WindowsStartup.Read().RawValue == old && Live(w).SoundsEnabled == true && File.ReadAllText(ConfigStore.FilePath) == before, "Failure changed old Run/config");
            } finally { w.Close(); }
        }); }
        foreach (bool maximized in new bool[] { false, true }) { bool max = maximized; Case("hidden launch, tray/hotkey reopen and geometry maximized=" + max, delegate {
            Seed(max); MainWindow w = new MainWindow(true); bool flashed = false; w.IsVisibleChanged += delegate { if (w.IsVisible && w.WindowState != WindowState.Minimized) flashed = true; };
            w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w, typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
            try { w.Show(); Drain(); Assert(!w.IsVisible && !w.ShowInTaskbar && !flashed, "Hidden startup exposed a window/taskbar button");
                Assert(((DispatcherTimer)Get(w, "_pomodoroTimer")).IsEnabled && ((DispatcherTimer)Get(w, "_remindersTimer")).IsEnabled, "Hidden timers did not start");
                Call(w, "SaveWindowPlacement"); Assert(Load().WindowMaximized == max && Math.Abs(Load().WindowWidth - 720) < 3 && Math.Abs(Load().WindowHeight - 510) < 3, "Hidden save lost normal/maximized geometry");
                Forms.NotifyIcon tray = (Forms.NotifyIcon)Get(w, "_trayIcon"); ((Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[0]).PerformClick(); Drain(); Assert(w.IsVisible && w.ShowInTaskbar && w.WindowState == (max ? WindowState.Maximized : WindowState.Normal), "Tray reopen lost state");
                Call(w, "SaveWindowPlacement"); Assert(Load().WindowMaximized == max && Math.Abs(Load().WindowWidth - 720) < 3 && Math.Abs(Load().WindowHeight - 510) < 3, "Geometry lost after hidden startup");
                w.Hide(); object[] args = new object[] { IntPtr.Zero, 0x0312, new IntPtr(0x4711), IntPtr.Zero, false }; Call(w, "GlobalHotKeyMessage", args); Drain(); Assert(w.IsVisible && (bool)args[4], "Hotkey message did not reopen window");
            } finally { w.Close(); }
        }); }
        Case("legacy close-to-tray fallback and new preference roundtrip", delegate {
            Seed(false);
            AppConfig c = Load(); c.CloseToTray = null; c.MinimizeToTray = true; ConfigStore.Save(c);
            Assert(Load().CloseToTrayEnabled && !Load().CloseToTray.HasValue,
                "Legacy enabled tray preference was not honored");
            c.MinimizeToTray = false; ConfigStore.Save(c);
            Assert(!Load().CloseToTrayEnabled, "Legacy disabled preference was not honored");
            c.CloseToTray = true; ConfigStore.Save(c);
            Assert(Load().CloseToTrayEnabled && Load().CloseToTray == true,
                "Explicit new preference did not override legacy false");
            c.CloseToTray = false; c.MinimizeToTray = true; ConfigStore.Save(c);
            Assert(!Load().CloseToTrayEnabled && Load().CloseToTray == false,
                "Explicit new false preference did not survive serialization");
        });
        Case("minimize always stays in taskbar even when close-to-tray enabled", delegate {
            Seed(false); AppConfig c = Load(); c.CloseToTray = true; ConfigStore.Save(c);
            MainWindow w = Open(false, false); try {
                w.WindowState = WindowState.Minimized; Drain();
                Assert(w.IsVisible && w.ShowInTaskbar &&
                    w.WindowState == WindowState.Minimized,
                    "Minimize incorrectly hid the window in the tray");
                Call(w, "ShowPanel"); Drain();
                Assert(w.IsVisible && w.WindowState == WindowState.Normal,
                    "Restoring from the taskbar lost the normal window");
            } finally { ForceExit(w); }
        });
        Case("close button hides into tray and tray Exit always terminates", delegate {
            Seed(false); AppConfig c = Load(); c.CloseToTray = true; ConfigStore.Save(c);
            MainWindow w = Open(false, false); int closed = 0;
            w.Closed += delegate { closed++; };
            try {
                Forms.NotifyIcon tray = (Forms.NotifyIcon)Get(w, "_trayIcon");
                Assert(tray != null && tray.Visible && tray.Icon != null,
                    "Tray was not available for the close interception test");
                w.Close(); Drain();
                Assert(!w.IsVisible && closed == 0 && tray.Visible,
                    "Close did not leave the application alive in tray");
                ((Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[0]).PerformClick(); Drain();
                Assert(w.IsVisible, "Tray Open did not restore a hidden window");
                w.Close(); Drain();
                Assert(!w.IsVisible && closed == 0, "Second close unexpectedly exited");
                ((Forms.ToolStripMenuItem)tray.ContextMenuStrip.Items[2]).PerformClick(); Drain();
                Assert(closed == 1, "Tray Exit was intercepted instead of closing the application");
            } finally { if (closed == 0) ForceExit(w); }
        });
        Case("close preference off exits, and shutdown bypasses close-to-tray", delegate {
            Seed(false); AppConfig c = Load(); c.CloseToTray = false; ConfigStore.Save(c);
            MainWindow w = Open(false, false); int closed = 0;
            w.Closed += delegate { closed++; };
            try { w.Close(); Drain(); Assert(closed == 1, "Disabled preference did not close the application"); }
            finally { if (closed == 0) ForceExit(w); }
            c.CloseToTray = true; ConfigStore.Save(c);
            MainWindow shutdown = Open(false, false); int ended = 0;
            shutdown.Closed += delegate { ended++; };
            try {
                Set(shutdown, "_sessionEnding", true);
                shutdown.Close(); Drain();
                Assert(ended == 1, "Windows session ending was intercepted by tray hiding");
            } finally { if (ended == 0) ForceExit(shutdown); }
        });
        Case("close preference cannot hide the app when the tray is unavailable", delegate {
            Seed(false); AppConfig c = Load(); c.CloseToTray = true; ConfigStore.Save(c);
            FailTray = true; MainWindow w = Open(false, false); int closed = 0;
            w.Closed += delegate { closed++; };
            try {
                w.Close(); Drain();
                Assert(closed == 1, "Close left an inaccessible background application");
            } finally { FailTray = false; if (closed == 0) ForceExit(w); }
        });
        Case("settings save changes close-to-tray without changing the legacy field", delegate {
            Seed(false); AppConfig c = Load(); c.MinimizeToTray = false;
            c.CloseToTray = null; ConfigStore.Save(c);
            MainWindow w = Open(false, false); try {
                Edit(w, true, delegate(Window d) {
                    CheckBox close = Check(d, "При закрытии окна сворачивать в трей");
                    Assert(close.IsChecked == false, "Previous disabled preference was not shown");
                    close.IsChecked = true;
                });
                c = Load();
                Assert(c.CloseToTray == true && c.MinimizeToTray == false,
                    "New preference did not persist independently from legacy minimize flag");
                Edit(w, false, delegate(Window d) {
                    Check(d, "При закрытии окна сворачивать в трей").IsChecked = false;
                });
                Assert(Load().CloseToTray == true,
                    "Cancelled tray preference change was written to disk");
            } finally { ForceExit(w); }
        });
        Case("ordinary launch is visible without tray argument", delegate { Seed(false); MainWindow w = Open(false, false); try { Assert(w.IsVisible && w.ShowInTaskbar && w.WindowState == WindowState.Normal, "Normal launch hidden"); } finally { w.Close(); } });
        foreach (bool nullIcon in new bool[] { false, true }) { bool n = nullIcon; Case("tray unavailable fallback null icon=" + n, delegate {
            Seed(false); FailTray = !n; NullIcon = n; MainWindow w = Open(true, false); try {
                Forms.NotifyIcon tray = (Forms.NotifyIcon)Get(w, "_trayIcon");
                Assert((w.IsVisible && w.ShowInTaskbar && w.WindowState != WindowState.Minimized) ||
                    (n && tray != null && tray.Visible && tray.Icon != null), "Unavailable tray stranded application");
            } finally { w.Close(); FailTray = NullIcon = false; }
        }); }
        Case("old JSON audio defaults and roundtrip/ZIP", delegate {
            Seed(false); File.WriteAllText(ConfigStore.FilePath, "{\"version\":5,\"items\":[],\"groups\":[]}"); AppConfig c = Load(); Assert(c.SoundsEnabled == true && c.PomodoroSoundEnabled == true && c.RemindersSoundEnabled == false && !NotificationAudio.ShouldPlayReminder(c), "Legacy defaults changed");
            c.SoundsEnabled = false; c.PomodoroSoundEnabled = false; c.RemindersSoundEnabled = true; ConfigStore.Save(c); c = Load(); Assert(c.SoundsEnabled == false && c.PomodoroSoundEnabled == false && c.RemindersSoundEnabled == true, "Audio fields lost on load");
            string zip = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "audio.zip"); BackupStore.ExportAll(zip, c, new ReminderDocument()); ConfigStore.Save(new AppConfig()); BackupStore.RestoreAll(zip); c = Load(); Assert(c.SoundsEnabled == false && c.PomodoroSoundEnabled == false && c.RemindersSoundEnabled == true, "ZIP lost audio fields");
        });
        Case("partial tray failure keeps taskbar access after minimizing", delegate {
            Seed(false); FailTrayAfterCreate = true; MainWindow w = Open(true, false); try {
                Assert(w.IsVisible && w.ShowInTaskbar, "Partial tray failure did not expose window");
                w.WindowState = WindowState.Minimized; Drain(); Assert(w.IsVisible && w.ShowInTaskbar, "Partial tray failure hid the only access point on minimize");
            } finally { FailTrayAfterCreate = false; w.Close(); }
        });
        foreach (bool master in new bool[] { false, true }) foreach (bool pomo in new bool[] { false, true }) foreach (bool reminder in new bool[] { false, true }) { bool m = master, p = pomo, r = reminder; Case("audio conditions master=" + m + " pomo=" + p + " reminders=" + r, delegate {
            AppConfig c = new AppConfig { SoundsEnabled = m, PomodoroSoundEnabled = p, RemindersSoundEnabled = r }; int pc = PomodoroSounds, rc = ReminderSounds;
            NotificationAudio.PlayPomodoro(c); NotificationAudio.PlayReminder(c); Assert(PomodoroSounds - pc == (m && p ? 1 : 0) && ReminderSounds - rc == (m && r ? 1 : 0), "Audio gating incorrect");
            Seed(false); MainWindow w = Open(false, false); try {
                Edit(w, true, delegate(Window d) { Check(d, "Включить звуки AppShelf").IsChecked = m; Check(d, "Звук окончания этапа Помодоро").IsChecked = p; Check(d, "Звук напоминаний").IsChecked = r; });
                c = Load(); Assert(c.SoundsEnabled == m && c.PomodoroSoundEnabled == p && c.RemindersSoundEnabled == r && Live(w).SoundsEnabled == m && Live(w).PomodoroSoundEnabled == p && Live(w).RemindersSoundEnabled == r, "Settings lost independent audio switches");
            } finally { w.Close(); }
        }); }
        Case("sound-only failed save leaves all switches and Run unchanged", delegate {
            Seed(false); MainWindow w = Open(false, false); try {
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    Edit(w, true, delegate(Window d) { Check(d, "Включить звуки AppShelf").IsChecked = false; Check(d, "Звук окончания этапа Помодоро").IsChecked = false; Check(d, "Звук напоминаний").IsChecked = true; });
                }
                Assert(Writes == 0 && Live(w).SoundsEnabled == true && Live(w).PomodoroSoundEnabled == true && Live(w).RemindersSoundEnabled == false && Load().SoundsEnabled == true && Load().RemindersSoundEnabled == false, "Failed audio save changed settings");
            } finally { w.Close(); }
        });
        Case("null audio config and nullable legacy values do not add reminder sound", delegate {
            Assert(!NotificationAudio.ShouldPlayPomodoro(null) && !NotificationAudio.ShouldPlayReminder(null), "Null config played audio");
            AppConfig c = new AppConfig { SoundsEnabled = null, PomodoroSoundEnabled = null, RemindersSoundEnabled = null };
            Assert(NotificationAudio.ShouldPlayPomodoro(c) && !NotificationAudio.ShouldPlayReminder(c), "Nullable defaults changed");
        });
        Case("silent Pomodoro/reminder keep visual notifications while hidden", delegate {
            Seed(false); MainWindow w = Open(true, false); try { Live(w).SoundsEnabled = false; int p = PomodoroSounds, r = ReminderSounds, b = Balloons;
                Set(w, "_pomodoroRunning", true); Set(w, "_pomodoroDeadlineUtc", DateTime.UtcNow.AddSeconds(-1)); Call(w, "TickPomodoro"); Assert(PomodoroSounds == p && Balloons == b + 1 && ((TextBlock)Get(w, "_status")).Text.Contains("Следующий этап"), "Silent Pomodoro lost notification");
                ReminderDocument doc = (ReminderDocument)Get(w, "_reminderDocument"); doc.Reminders.Add(new ReminderItem { Id = "test", Title = "Silent reminder", DueLocal = ReminderStore.ToValue(DateTime.Now.AddSeconds(-10)), Repeat = "once", Active = true }); ReminderStore.Save(doc);
                Call(w, "CheckDueReminders"); Drain(); Window alert = (Window)Get(w, "_reminderAlertWindow"); Assert(ReminderSounds == r && alert != null && alert.IsVisible && !doc.Reminders[0].Active, "Silent reminder lost visual/delivery"); alert.Close();
            } finally { w.Close(); }
        });
        Case("enabled sounds are requested once by real notification handlers", delegate {
            Seed(false); MainWindow w = Open(true, false); try {
                Live(w).SoundsEnabled = true; Live(w).PomodoroSoundEnabled = true; Live(w).RemindersSoundEnabled = true;
                int p = PomodoroSounds, r = ReminderSounds;
                Set(w, "_pomodoroRunning", true); Set(w, "_pomodoroDeadlineUtc", DateTime.UtcNow.AddSeconds(-1)); Call(w, "TickPomodoro"); Call(w, "TickPomodoro"); Assert(PomodoroSounds == p + 1, "Pomodoro handler omitted/duplicated audio");
                ReminderDocument doc = (ReminderDocument)Get(w, "_reminderDocument"); doc.Reminders.Add(new ReminderItem { Id = "audible", Title = "Enabled audio probe", DueLocal = ReminderStore.ToValue(DateTime.Now.AddSeconds(-10)), Repeat = "once", Active = true }); ReminderStore.Save(doc);
                Call(w, "CheckDueReminders"); Call(w, "CheckDueReminders"); Drain(); Assert(ReminderSounds == r + 1 && ((Window)Get(w, "_reminderAlertWindow")).IsVisible, "Reminder handler omitted/duplicated audio or visual alert");
                ((Window)Get(w, "_reminderAlertWindow")).Close();
            } finally { w.Close(); }
        });
        Case("registry read failure does not apply config", delegate {
            Seed(false); MainWindow w = Open(false, false); try {
                string before = File.ReadAllText(ConfigStore.FilePath); int errors = Errors; FailRead = true; Call(w, "ShowSettings"); FailRead = false;
                Assert(Errors == errors + 1 && Writes == 0 && File.ReadAllText(ConfigStore.FilePath) == before, "Registry read failure changed settings");
            } finally { FailRead = false; w.Close(); }
        });
        foreach (double scale in new double[] { 1, 1.25, 1.5 }) { double s = scale; Case("settings controls, scrolling, cancel scale=" + s, delegate {
            Seed(false); MainWindow w = Open(false, false); try { Edit(w, false, delegate(Window d) {
                d.Width *= s; d.Height *= s; ((FrameworkElement)d.Content).LayoutTransform = new ScaleTransform(s, s); Drain();
                CheckBox startup = Check(d, "Запускать AppShelf при входе в Windows"), hidden = Check(d, "При автозапуске сразу скрывать в трей"); Assert(!hidden.IsEnabled, "Hidden option enabled without startup"); startup.IsChecked = true; Assert(hidden.IsEnabled, "Hidden option did not enable");
                CheckBox master = Check(d, "Включить звуки AppShelf"), pomo = Check(d, "Звук окончания этапа Помодоро"), reminder = Check(d, "Звук напоминаний"); Assert(!reminder.IsChecked.Value, "Reminder sound enabled by default"); master.IsChecked = false; Assert(!pomo.IsEnabled && !reminder.IsEnabled, "Master does not disable child options"); master.IsChecked = true; Assert(pomo.IsEnabled && reminder.IsEnabled, "Child options did not reenable");
                ScrollViewer scroll = Controls<ScrollViewer>(d)[0]; scroll.ScrollToBottom(); Drain(); Assert(scroll.ScrollableHeight > 0 && scroll.VerticalOffset > 0, "No settings scroll");
                Picture(d, "settings-" + s.ToString(System.Globalization.CultureInfo.InvariantCulture)); Button save = ButtonNamed(d, "Сохранить"), cancel = ButtonNamed(d, "Отмена"); FrameworkElement content = (FrameworkElement)d.Content;
                foreach (Button button in new Button[] { save, cancel }) { Rect rect = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize)); Assert(button.IsVisible && rect.Bottom <= content.RenderSize.Height + 0.5, "Settings buttons clipped"); }
            }); Assert(Writes == 0 && !WindowsStartup.Read().Enabled, "Cancel wrote registry"); } finally { w.Close(); }
        }); }
        Case("entry-point tray argument and duplicate process protection", delegate {
            string folder = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "Child-" + Guid.NewGuid().ToString("N"));
            using (Process owner = Child(folder, "--tray", true)) try {
                WaitStarted(owner, Path.Combine(folder, "started.txt")); Assert(File.ReadAllText(Path.Combine(folder, "started.txt")) == "True", "Tray argument not parsed");
                using (Process second = Child(folder, "--tray", false)) { Assert(second.WaitForExit(5000) && !File.Exists(Path.Combine(folder, "notice.txt")), "Duplicate tray launch showed dialog/hung"); }
                using (Process second = Child(folder, "", false)) { Assert(second.WaitForExit(5000) && File.Exists(Path.Combine(folder, "notice.txt")), "Duplicate normal launch was not reported"); }
            } finally { using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\AppShelf.Package8." + new DirectoryInfo(folder).Name)) stop.Set(); Assert(owner.WaitForExit(5000), "Test owner did not exit"); }
            folder = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "Child-" + Guid.NewGuid().ToString("N")); using (Process normal = Child(folder, "", false)) { Assert(normal.WaitForExit(5000), "Normal child hung"); Assert(File.ReadAllText(Path.Combine(folder, "started.txt")) == "False", "Ordinary argument parsed as tray"); }
        });
        Case("production WPF render-failure handler logs once and shuts down without dialog flood", delegate {
            string folder = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "Render-" + Guid.NewGuid().ToString("N"));
            using (Process child = Child(folder, "--render-failure", false))
            {
                Assert(child.WaitForExit(10000), "Render-failure probe did not exit");
                string notice = Path.Combine(folder, "notice.txt");
                Assert(File.Exists(Path.Combine(folder, "wpf-pass.txt")), "Production WPF handler regression: " +
                    (File.Exists(notice) ? File.ReadAllText(notice) : "no child notice"));
            }
        });
        Console.WriteLine("Startup/sound scenarios passed=" + passed + "; failed=" + failed); app.Shutdown(); if (failed != 0) throw new Exception("Startup/sound failures: " + failed);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.GenerateInMemory = $true
    $options.MainClass = 'AppShelf.Program'
    $options.OutputAssembly = Join-Path $data 'Startup Sound Tests.exe'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) { [void]$options.ReferencedAssemblies.Add((Join-Path $framework $ref)) }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('StartupSoundTests').GetField('TestExecutable').SetValue($null, $options.OutputAssembly)
    $result.CompiledAssembly.GetType('StartupSoundTests').GetMethod('Run').Invoke($null, @())
    "PASS isolated startup/sound tests; renders: $temp"
} finally {
    $provider.Dispose()
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKey, $false)
    $env:APPSHELF_TEST_DATA = $previous
    # This unique directory was created above; keep only the non-personal UI renders outside it.
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
}
