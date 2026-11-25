# Windows PowerShell 5.1 -STA. Actual countdown methods/WPF, isolated data/registry, no audio.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$id = [Guid]::NewGuid().ToString('N')
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-Countdown-' + $id)
$data = Join-Path $temp 'TestData'
[void][IO.Directory]::CreateDirectory($data)
$key = 'Software\AppShelf.Tests\Countdown-' + $id
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $data
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs','PluginWidgetView.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Storage isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $text = $text.Replace('DateTime.UtcNow', 'CountdownTests.UtcNow')
            $pattern = 'MessageBox.Show\(_countdownWindow, "[^"\r\n]*",\s*"AppShelf · Таймер", MessageBoxButton.OK, MessageBoxImage.Warning\);'
            if ([regex]::Matches($text, $pattern).Count -ne 1) { throw 'Countdown validation boundary missing.' }
            $text = [regex]::Replace($text, $pattern, 'CountdownTests.Warnings++;')
            $pattern = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $pattern).Count -ne 1) { throw 'Error boundary missing.' }
            $text = [regex]::Replace($text, $pattern, 'CountdownTests.Errors++;')
            $anchor = 'bool? savedSettings = dialog.ShowDialog();'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Settings boundary missing.' }
            $text = $text.Replace($anchor, 'bool? savedSettings = CountdownTests.Settings(dialog);')
        }
        if ($file -eq 'WindowsIntegration.cs') {
            $anchor = 'Software\Microsoft\Windows\CurrentVersion\Run'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Registry isolation anchor missing.' }
            $text = $text.Replace($anchor, $key)
            $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', 'CountdownTests.Sounds++;').Replace('System.Media.SystemSounds.Exclamation.Play();', 'CountdownTests.Sounds++;')
        }
        $text = [regex]::Replace($text, '_trayIcon.ShowBalloonTip\([^;]+\);', 'CountdownTests.Balloons++;')
        $sources += $text.Replace('SiteLinks.FetchFavicon(site)', 'false')
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;

public static class CountdownTests
{
    public static DateTime Now = new DateTime(2031, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public static bool RealTime;
    public static DateTime UtcNow { get { return RealTime ? DateTime.UtcNow : Now; } }
    public static int Sounds, Balloons, Warnings, Errors;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed, failed;
    private static Action<Window> settings;
    private static bool saveSettings;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static void Set(MainWindow w, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(w, value); }
    private static object Call(MainWindow w, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, args); } catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static AppConfig Live(MainWindow w) { return (AppConfig)Get(w, "_config"); }
    private static CountdownTimerEngine Engine(MainWindow w) { return (CountdownTimerEngine)Get(w, "_countdownEngine"); }
    private static Window Dialog(MainWindow w) { return (Window)Get(w, "_countdownWindow"); }
    private static AppConfig Load() { string warning; bool safe; AppConfig c = ConfigStore.Load(out warning, out safe); Assert(safe && warning == null, "Config rejected: " + warning); return c; }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> result = new List<T>(); if (root is T) result.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject) result.AddRange(Controls<T>((DependencyObject)child)); return result;
    }
    private static Button ButtonNamed(Window d, string text) { foreach (Button b in Controls<Button>(d)) if (Convert.ToString(b.Content) == text) return b; throw new Exception("Missing button: " + text); }
    private static CheckBox Check(Window d, string text) { foreach (CheckBox c in Controls<CheckBox>(d)) if (Convert.ToString(c.Content) == text) return c; throw new Exception("Missing checkbox: " + text); }
    private static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain(); }
    public static bool Settings(Window d)
    {
        Exception error = null; d.ShowInTaskbar = false;
        d.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
            try { settings(d); if (d.IsVisible) Click(ButtonNamed(d, saveSettings ? "Сохранить" : "Отмена")); }
            catch (Exception ex) { error = ex; d.Close(); }
        }));
        bool saved = d.ShowDialog() == true; Drain(); if (error != null) throw error; return saved;
    }
    private static void EditSettings(MainWindow w, bool save, Action<Window> action) { settings = action; saveSettings = save; Call(w, "ShowSettings"); }
    private static MainWindow Open(bool hidden)
    {
        ConfigStore.Save(new AppConfig { ShowWeather = false, ShowNetwork = false, RdpKeepAwake = false, CountdownDurationSeconds = 300, MinimizeToTray = false });
        ReminderStore.Save(new ReminderDocument()); MainWindow w = new MainWindow(hidden);
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w, typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        w.ShowInTaskbar = false; w.Show(); Drain(); return w;
    }
    private static void Fill(MainWindow w, string h, string m, string s)
    {
        ((TextBox)Get(w, "_countdownHours")).Text = h; ((TextBox)Get(w, "_countdownMinutes")).Text = m; ((TextBox)Get(w, "_countdownSeconds")).Text = s;
    }
    private static void Show(MainWindow w) { Call(w, "ShowCountdownWindow"); Drain(); }
    private static void Picture(FrameworkElement view, string name)
    {
        view.UpdateLayout(); RenderTargetBitmap image = new RenderTargetBitmap((int)Math.Ceiling(view.RenderSize.Width), (int)Math.Ceiling(view.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new DrawingVisual(); using (DrawingContext dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(view) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(view.RenderSize) }, null, new Rect(view.RenderSize));
        image.Render(visual); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (FileStream stream = File.Create(Path.Combine(new DirectoryInfo(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA")).Parent.FullName, name + ".png"))) encoder.Save(stream);
    }
    private static void InsideClient(FrameworkElement control, Window window)
    {
        NativeRect native; Assert(GetClientRect(new WindowInteropHelper(window).Handle, out native), "No native client bounds");
        Point size = PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice.Transform(new Point(native.Right, native.Bottom));
        Thickness margin = ((FrameworkElement)window.Content).Margin;
        Rect area = new Rect(margin.Left, margin.Top, size.X - margin.Left - margin.Right, size.Y - margin.Top - margin.Bottom);
        Rect bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
        Assert(bounds.Left >= area.Left - 0.5 && bounds.Top >= area.Top - 0.5 && bounds.Right <= area.Right + 0.5 && bounds.Bottom <= area.Bottom + 0.5, "Control clipped by native client/margins: " + bounds + " in " + area);
    }
    private static void Case(string name, Action test)
    {
        DateTime previous = Now;
        try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } finally { Now = previous; }
    }
    public static void Run()
    {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Isolation failed");
        Application app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; FluentUi.Install(app);
        foreach (int duration in new int[] { 1, 60, 3600, 86399 }) { int value = duration; Case("engine duration and completion " + value, delegate {
            CountdownTimerEngine e = new CountdownTimerEngine(value); e.Start(Now); Assert(e.Running && e.RemainingAt(Now).TotalSeconds == value, "Start failed");
            Assert(!e.Advance(Now.AddSeconds(value).AddTicks(-1)), "Completed before deadline"); Assert(e.Advance(Now.AddSeconds(value)) && e.Completed && !e.Running && e.RemainingAt(Now.AddDays(1)) == TimeSpan.Zero, "Completion failed"); Assert(!e.Advance(Now.AddDays(1)), "Duplicate completion");
            e.Start(Now.AddDays(1)); Assert(e.Running && !e.Completed && e.RemainingAt(Now.AddDays(1)).TotalSeconds == value, "Restart after completion failed");
        }); }
        foreach (int duration in new int[] { -1, 0, 86400, Int32.MaxValue }) { int value = duration; Case("engine rejects invalid duration " + value, delegate {
            bool rejected = false; try { new CountdownTimerEngine(value); } catch (ArgumentOutOfRangeException) { rejected = true; } Assert(rejected, "Invalid duration accepted");
        }); }
        Case("no accumulated drift after irregular dispatcher delays", delegate {
            CountdownTimerEngine e = new CountdownTimerEngine(3600); e.Start(Now);
            foreach (double seconds in new double[] { 0.25, 1.8, 40, 121.31, 3599.9 }) { Assert(!e.Advance(Now.AddSeconds(seconds)), "Early completion"); Assert(Math.Abs(e.RemainingAt(Now.AddSeconds(seconds)).TotalSeconds - (3600 - seconds)) < 0.001, "Accumulated tick drift"); }
            Assert(e.Advance(Now.AddHours(2)), "Delayed UI did not complete");
        });
        Case("pause and resume retain fractional time", delegate {
            CountdownTimerEngine e = new CountdownTimerEngine(30); e.Start(Now); e.Pause(Now.AddMilliseconds(1250)); Assert(e.Paused && !e.Running && e.RemainingAt(Now.AddDays(1)).TotalSeconds == 28.75, "Pause did not freeze time");
            e.Start(Now.AddDays(1)); Assert(!e.Paused && e.Running && !e.Advance(Now.AddDays(1).AddSeconds(28)), "Resume changed remaining time"); Assert(e.Advance(Now.AddDays(1).AddMilliseconds(28750)), "Resume deadline wrong");
        });
        Case("running start is idempotent, reset cancels pending completion", delegate {
            CountdownTimerEngine e = new CountdownTimerEngine(10); e.Start(Now); e.Start(Now.AddSeconds(8)); Assert(e.RemainingAt(Now.AddSeconds(8)).TotalSeconds == 2, "Second start extended deadline");
            e.Reset(20); Assert(!e.Running && !e.Paused && !e.Completed && e.RemainingAt(Now).TotalSeconds == 20 && !e.Advance(Now.AddDays(1)), "Reset did not cancel old deadline");
        });
        foreach (string input in new string[] { "0|0|0", "24|0|0", "0|60|0", "0|0|60", "-1|0|1", "x|0|1", "|0|1", "0|0|1.5" }) { string invalid = input; Case("UI invalid input preserved " + invalid, delegate {
            MainWindow w = Open(false); try { Show(w); string[] fields = invalid.Split('|'); Fill(w, fields[0], fields[1], fields[2]); int warnings = Warnings; Click(ButtonNamed(Dialog(w), "Старт")); Assert(Warnings == warnings + 1 && !Engine(w).Running && ((TextBox)Get(w, "_countdownHours")).Text == fields[0] && ((TextBox)Get(w, "_countdownSeconds")).Text == fields[2], "Invalid input started/was lost"); } finally { w.Close(); }
        }); }
        Case("UI start, pause, continue, reset and locked duration fields", delegate {
            MainWindow w = Open(false); try { Show(w); Fill(w, "1", "2", "3"); Click(ButtonNamed(Dialog(w), "Старт")); Assert(Engine(w).DurationSeconds == 3723 && Load().CountdownDurationSeconds == 3723, "Hours/minutes/seconds or persistence incorrect");
                foreach (TextBox box in new TextBox[] { (TextBox)Get(w, "_countdownHours"), (TextBox)Get(w, "_countdownMinutes"), (TextBox)Get(w, "_countdownSeconds") }) Assert(!box.IsEnabled, "Can edit running duration");
                Now = Now.AddSeconds(2.5); Click(ButtonNamed(Dialog(w), "Пауза")); Assert(Engine(w).Paused && !((TextBox)Get(w, "_countdownHours")).IsEnabled && !((TextBox)Get(w, "_countdownMinutes")).IsEnabled && !((TextBox)Get(w, "_countdownSeconds")).IsEnabled, "Pause failed or allows duration edits"); double frozen = Engine(w).RemainingAt(Now).TotalSeconds; Now = Now.AddHours(1); Call(w, "TickCountdown"); Assert(Engine(w).RemainingAt(Now).TotalSeconds == frozen, "Paused timer advanced");
                Click(ButtonNamed(Dialog(w), "Продолжить")); Assert(Engine(w).Running, "Continue failed"); Click(ButtonNamed(Dialog(w), "Сброс")); Assert(!Engine(w).Running && !Engine(w).Paused && ((TextBox)Get(w, "_countdownHours")).IsEnabled && ((TextBlock)Get(w, "_countdownClock")).Text == "01:02:03", "Reset failed");
            } finally { w.Close(); }
        });
        foreach (bool viaButton in new bool[] { false, true }) { bool button = viaButton; Case("single completion after blocked UI, via button=" + button, delegate {
            MainWindow w = Open(false); try { Show(w); Fill(w, "0", "0", "1"); Click(ButtonNamed(Dialog(w), "Старт")); int sounds = Sounds, balloons = Balloons; Now = Now.AddSeconds(20);
                if (button) Click(ButtonNamed(Dialog(w), "Пауза")); else Call(w, "TickCountdown");
                Call(w, "TickCountdown"); Call(w, "TickCountdown"); Assert(Engine(w).Completed && Sounds == sounds + 1 && Balloons == balloons + 1 && ((TextBlock)Get(w, "_countdownStage")).Text == "Время вышло", "Completion duplicated/missing");
                Click(ButtonNamed(Dialog(w), "Старт")); Assert(Engine(w).Running && !Engine(w).Completed, "Restart failed");
            } finally { w.Close(); }
        }); }
        foreach (bool master in new bool[] { false, true }) foreach (bool countdown in new bool[] { false, true }) { bool m = master, c = countdown; Case("countdown audio master=" + m + " module=" + c, delegate {
            MainWindow w = Open(false); try { Live(w).SoundsEnabled = m; Live(w).CountdownSoundEnabled = c; Show(w); Fill(w, "0", "0", "1"); Click(ButtonNamed(Dialog(w), "Старт")); int sounds = Sounds, balloons = Balloons; Now = Now.AddSeconds(2); Call(w, "TickCountdown"); Assert(Sounds - sounds == (m && c ? 1 : 0) && Balloons == balloons + 1 && ((TextBlock)Get(w, "_countdownClock")).Text == "00:00:00", "Audio gating or visual notification failed"); } finally { w.Close(); }
        }); }
        Case("maximum UI duration and reopen do not duplicate window or dispatcher", delegate {
            MainWindow w = Open(false); DispatcherTimer timer = null; try { timer = (DispatcherTimer)Get(w, "_countdownTimer"); Show(w); Window original = Dialog(w); Show(w); Assert(Object.ReferenceEquals(original, Dialog(w)), "Duplicate timer window");
                Fill(w, "23", "59", "59"); Click(ButtonNamed(Dialog(w), "Старт")); Assert(Engine(w).DurationSeconds == 86399, "Maximum rejected"); original.Close(); Now = Now.AddSeconds(3); Show(w); Assert(Engine(w).Running && ((TextBlock)Get(w, "_countdownClock")).Text == "23:59:56" && Object.ReferenceEquals(timer, Get(w, "_countdownTimer")), "Reopening stopped timer or created dispatcher");
                MainWindow reopened; w.Close(); Assert(!timer.IsEnabled, "Dispatcher survived owner close"); reopened = new MainWindow(); try { Assert(((CountdownTimerEngine)Get(reopened, "_countdownEngine")).DurationSeconds == 86399 && !Engine(reopened).Running, "Duration not restored or runtime resumed unexpectedly"); } finally { reopened.Close(); }
            } finally { w.Close(); }
        });
        Case("hidden tray countdown completes independently of Pomodoro", delegate {
            MainWindow w = Open(true); try { Assert(!w.IsVisible, "Fixture is not hidden"); Engine(w).Reset(10); Engine(w).Start(Now); Set(w, "_pomodoroRunning", true); Set(w, "_pomodoroRemaining", TimeSpan.FromMinutes(17)); Set(w, "_pomodoroDeadlineUtc", Now.AddHours(1)); int phase = (int)Get(w, "_pomodoroPhase"); Now = Now.AddSeconds(11); Call(w, "TickCountdown");
                Assert(Engine(w).Completed && (bool)Get(w, "_pomodoroRunning") && (int)Get(w, "_pomodoroPhase") == phase && (TimeSpan)Get(w, "_pomodoroRemaining") == TimeSpan.FromMinutes(17), "Timer stopped in tray or changed Pomodoro");
                Call(w, "ShowPanel"); Show(w); Assert(((TextBlock)Get(w, "_countdownStage")).Text == "Время вышло", "Completion lost after opening from tray");
            } finally { w.Close(); }
        });
        Case("real UTC and dispatcher finish once after an actual UI-thread stall", delegate {
            RealTime = true; MainWindow w = null;
            try { w = Open(false); Show(w); Fill(w, "0", "0", "1"); Click(ButtonNamed(Dialog(w), "Старт")); int sounds = Sounds, balloons = Balloons;
                Thread.Sleep(1200); Drain(); Stopwatch watch = Stopwatch.StartNew();
                while (Engine(w).Running && watch.ElapsedMilliseconds < 2500) { Drain(); Thread.Sleep(15); }
                Assert(Engine(w).Completed && Sounds == sounds + 1 && Balloons == balloons + 1 && ((TextBlock)Get(w, "_countdownClock")).Text == "00:00:00", "Real dispatcher/UTC completion failed");
                Thread.Sleep(350); Drain(); Assert(Sounds == sounds + 1 && Balloons == balloons + 1, "Real dispatcher repeated completion"); Picture(Dialog(w), "countdown-completed");
            } finally { if (w != null) w.Close(); RealTime = false; }
        });
        Case("Pomodoro completion leaves running countdown unchanged", delegate {
            MainWindow w = Open(false); try { Show(w); Fill(w, "0", "1", "0"); Click(ButtonNamed(Dialog(w), "Старт")); CountdownTimerEngine engine = Engine(w);
                Set(w, "_pomodoroRunning", true); Set(w, "_pomodoroDeadlineUtc", Now.AddSeconds(-1)); Call(w, "TickPomodoro");
                Assert(Object.ReferenceEquals(engine, Engine(w)) && engine.Running && engine.RemainingAt(Now).TotalSeconds == 60, "Pomodoro completion changed countdown");
            } finally { w.Close(); }
        });
        Case("disable, reenable, cancel and settings sound toggle", delegate {
            MainWindow w = Open(false); try { Show(w); Fill(w, "0", "1", "0"); Click(ButtonNamed(Dialog(w), "Старт")); DispatcherTimer old = (DispatcherTimer)Get(w, "_countdownTimer");
                EditSettings(w, false, delegate(Window d) { Check(d, "Показывать обычный таймер").IsChecked = false; }); Assert(Engine(w).Running && old.IsEnabled, "Cancel disabled timer");
                EditSettings(w, true, delegate(Window d) { Check(d, "Показывать обычный таймер").IsChecked = false; }); Assert(!old.IsEnabled && Get(w, "_countdownTimer") == null && Dialog(w) == null && !Engine(w).Running && ((FrameworkElement)Get(w, "_countdownChip")).Visibility == Visibility.Collapsed, "Disable did not stop/close/hide module");
                EditSettings(w, true, delegate(Window d) { Check(d, "Показывать обычный таймер").IsChecked = true; Check(d, "Звук завершения обычного таймера").IsChecked = false; });
                DispatcherTimer current = (DispatcherTimer)Get(w, "_countdownTimer"); Assert(current != null && current.IsEnabled && !Object.ReferenceEquals(current, old) && Load().CountdownSoundEnabled == false, "Reenable/sound save failed");
                EditSettings(w, true, delegate(Window d) {}); Assert(Object.ReferenceEquals(current, Get(w, "_countdownTimer")), "Unrelated settings created another dispatcher");
            } finally { w.Close(); }
        });
        Case("failed settings save does not disable live countdown", delegate {
            MainWindow w = Open(false); try { Show(w); Fill(w, "0", "1", "0"); Click(ButtonNamed(Dialog(w), "Старт")); DispatcherTimer timer = (DispatcherTimer)Get(w, "_countdownTimer"); int errors = Errors;
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) EditSettings(w, true, delegate(Window d) { Check(d, "Показывать обычный таймер").IsChecked = false; Check(d, "Звук завершения обычного таймера").IsChecked = false; });
                Assert(Errors == errors + 1 && Engine(w).Running && timer.IsEnabled && Live(w).ShowCountdownTimer == true && Live(w).CountdownSoundEnabled == true, "Failed save applied module settings");
            } finally { w.Close(); }
        });
        Case("2.8.0 JSON defaults, unchanged source and ZIP roundtrip", delegate {
            Directory.CreateDirectory(ConfigStore.DirectoryPath); string old = "{\"version\":5,\"items\":[],\"groups\":[],\"soundsEnabled\":false}"; File.WriteAllText(ConfigStore.FilePath, old);
            AppConfig c = Load(); Assert(c.ShowCountdownTimer == true && c.CountdownSoundEnabled == true && c.CountdownDurationSeconds == 300 && c.SoundsEnabled == false && File.ReadAllText(ConfigStore.FilePath) == old, "Old config defaults/read-only load failed");
            c.ShowCountdownTimer = false; c.CountdownSoundEnabled = false; c.CountdownDurationSeconds = 86399; ConfigStore.Save(c); c = Load(); Assert(c.ShowCountdownTimer == false && c.CountdownSoundEnabled == false && c.CountdownDurationSeconds == 86399 && c.Version == 5, "New config roundtrip failed");
            string zip = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "countdown.zip"); BackupStore.ExportAll(zip, c, new ReminderDocument()); ConfigStore.Save(new AppConfig()); BackupStore.RestoreAll(zip); c = Load(); Assert(c.ShowCountdownTimer == false && c.CountdownSoundEnabled == false && c.CountdownDurationSeconds == 86399, "ZIP lost countdown settings");
        });
        foreach (bool minimum in new bool[] { false, true }) foreach (double scale in new double[] { 1, 1.25, 1.5 }) { bool min = minimum; double s = scale; Case("real WPF layout scale=" + s + " minimum=" + min, delegate {
            MainWindow w = Open(false); try { Show(w); Window d = Dialog(w); d.Width = (min ? d.MinWidth : d.Width) * s; d.Height = (min ? d.MinHeight : d.Height) * s; ((FrameworkElement)d.Content).LayoutTransform = new ScaleTransform(s, s); Drain();
                Picture(d, "countdown-" + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + (min ? "-min" : ""));
                foreach (TextBox box in Controls<TextBox>(d)) InsideClient(box, d);
                TextBlock hint = Controls<TextBlock>(d)[Controls<TextBlock>(d).Count - 1]; Rect h = hint.TransformToAncestor(d).TransformBounds(new Rect(hint.RenderSize));
                Assert(h.Bottom <= d.RenderSize.Height - 10, "Sound hint clipped: " + h + " in " + d.RenderSize); InsideClient(hint, d);
                foreach (Button b in Controls<Button>(d)) { Rect r = b.TransformToAncestor(d).TransformBounds(new Rect(b.RenderSize)); Assert(b.IsVisible && r.Bottom <= d.RenderSize.Height - 10, "Buttons clipped"); }
            } finally { w.Close(); }
        }); }
        Console.WriteLine("Countdown scenarios passed=" + passed + "; failed=" + failed); app.Shutdown(); if (failed != 0) throw new Exception("Countdown failures: " + failed);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $true
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) { [void]$options.ReferencedAssemblies.Add((Join-Path $framework $ref)) }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('CountdownTests').GetMethod('Run').Invoke($null, @())
    "PASS isolated countdown tests; renders: $temp"
} finally {
    $provider.Dispose()
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($key, $false)
    $env:APPSHELF_TEST_DATA = $previous
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
}
