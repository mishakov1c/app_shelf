# Windows PowerShell 5.1 -STA. Real Program.Main in independent processes.
# Only storage and mutex are isolated; the reminder timer/alerts are not replaced.
# Native visibility flags and WPF renders do not prove a human saw the notification.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Use Windows PowerShell 5.1 -STA.'
}
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-DeliveryProcess-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous = $env:APPSHELF_DELIVERY_DATA
$env:APPSHELF_DELIVERY_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Isolation anchor missing' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_DELIVERY_DATA"), "AppShelf"')
            $mutex = '"Global\\AppShelf.Profile." + sid'
            if ($text.Split(@($mutex), [StringSplitOptions]::None).Length -ne 2) { throw 'Mutex isolation anchor missing' }
            $text = $text.Replace($mutex, '"Local\\AppShelf.DeliveryCheck." + sid + "." + new DirectoryInfo(Environment.GetEnvironmentVariable("APPSHELF_DELIVERY_DATA")).Name')
            if ($text.Contains('"Global\\AppShelf.Profile."')) { throw 'Mutex isolation failed' }
        }
        $text
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AppShelf;

public static class DeliveryProcessCheck {
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static string stage;
    static DateTime began;
    static DispatcherTimer timer;
    static int result = 1;
    static bool handled;
    static DateTime firstVisible;
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    static void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
    static ReminderDocument Load() {
        string warning; bool safe;
        ReminderDocument doc = ReminderStore.Load(out warning, out safe);
        Assert(safe && warning == null, "Reminder load failed: " + warning);
        return doc;
    }
    static object Get(MainWindow window, string name) {
        return typeof(MainWindow).GetField(name, Private).GetValue(window);
    }
    static List<Button> Buttons(DependencyObject root) {
        List<Button> found = new List<Button>();
        if (root is Button) found.Add((Button)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject) found.AddRange(Buttons((DependencyObject)child));
        return found;
    }
    static void Capture(Window alert) {
        IntPtr hwnd = new WindowInteropHelper(alert).Handle;
        Assert(hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd),
            "Notification does not have a visible non-minimized native window");
        Assert(alert.Topmost && alert.ShowInTaskbar && alert.IsVisible, "Notification is not user-visible");
        System.Windows.Media.Imaging.RenderTargetBitmap rendered =
            new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(alert.ActualWidth),
                (int)Math.Ceiling(alert.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        System.Windows.Media.DrawingVisual visual = new System.Windows.Media.DrawingVisual();
        using (System.Windows.Media.DrawingContext context = visual.RenderOpen())
            context.DrawRectangle(new System.Windows.Media.VisualBrush(alert), null,
                new Rect(0, 0, alert.ActualWidth, alert.ActualHeight));
        rendered.Render(visual);
        System.Windows.Media.Imaging.PngBitmapEncoder encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
        using (FileStream output = File.Create(Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_DELIVERY_DATA"),
            stage + "-render.png"))) encoder.Save(output);
        Point origin = alert.PointToScreen(new Point(0, 0));
        System.Windows.Media.Matrix dpi = PresentationSource.FromVisual(alert).CompositionTarget.TransformToDevice;
        int width = (int)Math.Ceiling(alert.ActualWidth * dpi.M11);
        int height = (int)Math.Ceiling(alert.ActualHeight * dpi.M22);
        // Screen pixels are diagnostic only: locked/headless Windows sessions may
        // reject capture or return a blank desktop even when the HWND is visible.
        try {
            using (System.Drawing.Bitmap image = new System.Drawing.Bitmap(width, height))
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(image)) {
                graphics.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(width, height));
                image.Save(Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_DELIVERY_DATA"), stage + "-screen.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            }
        } catch (System.ComponentModel.Win32Exception error) {
            Console.WriteLine("Screen capture unavailable: " + error.Message);
        }
    }
    static void Tick(object sender, EventArgs e) {
        if (handled) return;
        MainWindow w = Application.Current == null ? null : Application.Current.MainWindow as MainWindow;
        if (w == null) return;
        try {
            Window alert = (Window)Get(w, "_reminderAlertWindow");
            if (stage == "after-ack" || stage == "after-snooze") {
                if ((DateTime.Now - began).TotalSeconds < 2) return;
                Assert(alert == null, "Acknowledged/snoozed notification reappeared");
                Assert(Load().Reminders.Count == 2, "Restart duplicated history");
            } else {
                if (alert == null) {
                    Assert((DateTime.Now - began).TotalSeconds < 22, "Expected actual notification did not open");
                    return;
                }
                if (firstVisible == DateTime.MinValue) { firstVisible = DateTime.Now; return; }
                if ((DateTime.Now - firstVisible).TotalSeconds < 1) return;
                // Let the native window paint before capturing its screen pixels.
                if (!alert.IsActive) alert.Activate();
                alert.UpdateLayout();
                Capture(alert);
                ReminderDocument doc = Load();
                Assert(doc.Reminders.Count == 2 && doc.Reminders[1].PendingAlert, "Pending occurrence not durable");
                DateTime next, due;
                Assert(ReminderStore.TryParse(doc.Reminders[0].DueLocal, out next) &&
                    ReminderStore.TryParse(doc.Reminders[1].DueLocal, out due) &&
                    next > DateTime.Now && next.TimeOfDay == due.TimeOfDay, "Daily schedule shifted");
                if (stage == "late-tray" || stage == "recover-ack")
                    Assert(!w.IsVisible, "Tray-start main window unexpectedly visible");
                if (stage == "recover-ack") {
                    Button done = Buttons(alert).Find(b => Convert.ToString(b.Content) == "\u0413\u043e\u0442\u043e\u0432\u043e");
                    Assert(done != null, "Done button missing");
                    done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(!Load().Reminders[1].PendingAlert, "Done did not acknowledge on disk");
                } else if (stage == "snooze") {
                    Button button = Buttons(alert).Find(b => b.ToolTip != null);
                    Assert(button != null, "Quick snooze missing");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    doc = Load();
                    DateTime snoozed;
                    Assert(doc.Reminders.Count == 2 && !doc.Reminders[1].PendingAlert &&
                        doc.Reminders[1].Active && ReminderStore.TryParse(doc.Reminders[1].DueLocal, out snoozed) &&
                        snoozed > DateTime.Now && doc.Reminders[0].DueLocal == ReminderStore.ToValue(next),
                        "Snooze changed parent, lost state or duplicated history");
                }
            }
            handled = true; timer.Stop();
            // A normal application shutdown must not acknowledge an open alert.
            Application.Current.Shutdown();
            if (stage == "late-tray" || stage == "on-time")
                Assert(Load().Reminders[1].PendingAlert, "Shutdown acknowledged unseen alert");
            Console.WriteLine("PASS process/native-window delivery: " + stage);
            result = 0;
        } catch (Exception error) {
            handled = true; timer.Stop(); Console.Error.WriteLine(error);
            Application.Current.Shutdown(); result = 1;
        }
    }
    [STAThread] public static int Main(string[] args) {
        stage = args[0];
        string expected = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_DELIVERY_DATA"), "AppShelf");
        Assert(ConfigStore.DirectoryPath == expected, "Storage isolation failed");
        if (stage == "late-tray" || stage == "on-time" || stage == "snooze") {
            ConfigStore.Save(new AppConfig { ShowNetwork = false, ShowWeather = false, ShowPomodoro = false,
                ShowCountdownTimer = false, RdpKeepAwake = false, MinimizeToTray = true,
                SoundsEnabled = false });
            ReminderDocument doc = new ReminderDocument();
            doc.Reminders.Add(new ReminderItem { Id = "daily-process", Title = "Isolated daily delivery",
                Active = true, Repeat = "daily", DueLocal = ReminderStore.ToValue(
                    stage == "on-time" ? DateTime.Now.AddSeconds(2) : DateTime.Now.AddMinutes(-40)) });
            ReminderStore.Save(doc);
        }
        began = DateTime.Now;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += Tick; timer.Start();
        Program.Main();
        return result;
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = Join-Path $temp 'DeliveryProcess.exe'
    $options.CompilerOptions = '/main:DeliveryProcessCheck'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $compiled = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($compiled.Errors.Count) { throw ($compiled.Errors | Out-String) }
    foreach ($stage in @('late-tray','recover-ack','after-ack','on-time','snooze','after-snooze')) {
        $out = Join-Path $temp ($stage + '.out')
        $err = Join-Path $temp ($stage + '.err')
        $arguments = @($stage)
        if ($stage -ne 'on-time' -and $stage -ne 'snooze') { $arguments += '--tray' }
        $process = Start-Process -FilePath $options.OutputAssembly -ArgumentList $arguments -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
        $handle = $process.Handle
        try {
            if (-not $process.WaitForExit(30000)) { $process.Kill(); throw "Timeout: $stage" }
            Get-Content -LiteralPath $out
            if ($process.ExitCode -ne 0) { throw (Get-Content -LiteralPath $err -Raw) }
        } finally { $process.Dispose() }
    }
    Write-Output "DELIVERY_ARTIFACTS=$temp"
} finally { $provider.Dispose(); $env:APPSHELF_DELIVERY_DATA = $previous }
