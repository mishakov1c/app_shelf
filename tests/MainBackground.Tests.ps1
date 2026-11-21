# Windows PowerShell 5.1 -STA. Real WPF windows; isolated data and no monitor/network startup.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-Background-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Data isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $anchor = 'bool? savedSettings = dialog.ShowDialog();'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Settings boundary missing.' }
            $text = $text.Replace($anchor, 'bool? savedSettings = BackgroundTests.Settings(dialog);')
            $errorDialog = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $errorDialog).Count -ne 1) { throw 'Error dialog boundary missing.' }
            $text = [regex]::Replace($text, $errorDialog, 'BackgroundTests.SaveErrors++;')
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $sources += $text.Replace('SiteLinks.FetchFavicon(site)', 'false')
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;

public static class BackgroundTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed, failed, unhandled;
    public static int SaveErrors;
    private static Action<Window> settings;
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static void Set(MainWindow w, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(w, value); }
    private static object Call(MainWindow w, string name) {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, null); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static void Case(string name, Action test) {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject {
        List<T> result = new List<T>(); if (root is T) result.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject) result.AddRange(Controls<T>((DependencyObject)child));
        return result;
    }
    private static AppConfig Load() {
        string warning; bool safe; AppConfig c = ConfigStore.Load(out warning, out safe);
        Assert(safe && warning == null, "Config load failed: " + warning); return c;
    }
    private static void Seed(string preset) {
        AppConfig c = new AppConfig { MainBackgroundPreset = preset, ShowNetwork = false, ShowWeather = false,
            ShowPomodoro = false, RdpKeepAwake = false, MinimizeToTray = false, GroupingEnabled = true,
            SettingsLastSection = "appearance", TileDisplayMode = "normal", BottomPanelMode = "categories" };
        c.Groups.Add("Тестовая группа");
        Directory.CreateDirectory(ConfigStore.DirectoryPath);
        string path = Path.Combine(ConfigStore.DirectoryPath, "fixture.cmd");
        File.WriteAllText(path, "@echo off\r\n");
        c.Items.Add(new AppItem { Name = "Тестовое приложение", Path = path, Group = "Тестовая группа" });
        ConfigStore.Save(c);
    }
    private static MainWindow Open() {
        MainWindow w = new MainWindow();
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        Set(w, "_networkTimer", new DispatcherTimer());
        w.ShowInTaskbar = false; w.Show(); Drain();
        Assert(PresentationSource.FromVisual(w) != null, "No real WPF source");
        return w;
    }
    private static DockPanel Content(MainWindow w) { return (DockPanel)Get(w, "_mainBackgroundHost"); }
    private static void Background(MainWindow w, string preset) {
        DockPanel grid = Content(w); Brush brush = grid.Background;
        Assert(brush != null && brush.IsFrozen, "Background is missing or mutable");
        if (preset == "classic") Assert(Object.ReferenceEquals(brush, FluentUi.Background), "Classic background changed");
        else {
            DrawingBrush drawing = brush as DrawingBrush; Assert(drawing != null, "Drawing background missing");
            if (preset == "blue-arcs")
                Assert(Math.Abs(drawing.Viewbox.Width - grid.ActualWidth) < 0.01 &&
                    Math.Abs(drawing.Viewbox.Height - grid.ActualHeight) < 0.01, "Arcs use stale viewport dimensions");
        }
        List<Expander> groups = Controls<Expander>((DependencyObject)Get(w, "_tileHost"));
        Assert(groups.Count >= 1, "Grouped tiles disappeared");
        Border card = (Border)groups[0].Parent;
        Assert(card.Background.IsFrozen && card.BorderBrush.IsFrozen && card.BorderThickness.Left == 1,
            "Group surface/frame missing");
        if (preset == "classic")
            Assert(Object.ReferenceEquals(card.Background, FluentUi.Surface) &&
                Object.ReferenceEquals(card.BorderBrush, FluentUi.BorderColor), "Classic group styling changed");
        else {
            LinearGradientBrush fill = card.Background as LinearGradientBrush;
            Assert(fill != null && fill.GradientStops[0].Color.A < 255, "Translucent group gradient missing");
            Assert(((SolidColorBrush)card.BorderBrush).Color == ((SolidColorBrush)FluentUi.MainGroupBorder(preset)).Color,
                "Group border does not match preset");
        }
        Assert(Controls<Button>((DependencyObject)Get(w, "_tileHost")).Exists(b => b.Tag is AppItem),
            "Tiles disappeared");
    }
    private static void Picture(FrameworkElement view, string name) {
        view.UpdateLayout();
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),
            (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), name + ".png")))
            encoder.Save(stream);
    }
    private static void Click(Window dialog, string text) {
        foreach (Button b in Controls<Button>(dialog)) if (Convert.ToString(b.Content) == text) {
            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return;
        }
        throw new Exception("Button missing: " + text);
    }
    private static ComboBox Presets(Window dialog) {
        foreach (ComboBox combo in Controls<ComboBox>(dialog))
            foreach (object entry in combo.Items) {
                ComboBoxItem item = entry as ComboBoxItem;
                if (item != null && Object.Equals(item.Tag, "blue-arcs")) return combo;
            }
        throw new Exception("Background selector missing");
    }
    private static Slider Opacity(Window dialog) {
        foreach (Slider control in Controls<Slider>(dialog))
            if (control.Minimum == 50 && control.Maximum == 100) return control;
        throw new Exception("Group opacity slider missing");
    }
    private static Slider TileSize(Window dialog) {
        foreach (Slider control in Controls<Slider>(dialog))
            if (control.Minimum == 75 && control.Maximum == 125) return control;
        throw new Exception("Tile-size slider missing");
    }
    public static bool? Settings(Window dialog) {
        Exception error = null; dialog.ShowInTaskbar = false;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
            try { settings(dialog); } catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        bool? result = dialog.ShowDialog(); if (error != null) throw error; return result;
    }
    private static void ReopenProcess(string preset) {
        ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--reopen " + preset);
        start.UseShellExecute = false;
        using (Process child = Process.Start(start)) {
            if (!child.WaitForExit(20000)) { child.Kill(); child.WaitForExit(); throw new Exception("Reopen process timed out"); }
            Assert(child.ExitCode == 0, "Preset did not survive process restart");
        }
    }
    [STAThread] public static int Main(string[] args) {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"),
            "Data isolation failed");
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; FluentUi.Install(app);
        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
            unhandled++; Console.Error.WriteLine("UNHANDLED dispatcher: " + e.Exception); e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
            Console.Error.WriteLine("UNHANDLED process: " + e.ExceptionObject);
        };
        if (args.Length == 2 && args[0] == "--reopen") {
            try {
                Assert(Load().MainBackgroundPreset == args[1], "Persisted preset mismatch");
                MainWindow w = Open(); try { Background(w, args[1]); } finally { w.Close(); }
                app.Shutdown(); return unhandled == 0 ? 0 : 1;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        Case("all presets and invalid dimensions create frozen brushes without Transform exceptions", delegate {
            foreach (string preset in new string[] { "blue-arcs", "midnight", "graphite", "classic", null, "invalid" })
                foreach (double size in new double[] { 0, -1, Double.NaN, Double.PositiveInfinity, 1, 510, 850, 3840 })
                    Assert(FluentUi.MainWindowBackground(preset, size, size).IsFrozen, "Unfrozen brush");
        });
        string[] presets = new string[] { "blue-arcs", "midnight", "graphite", "classic" };
        for (int index = 0; index < presets.Length; index++) { string preset = presets[index]; int selected = index;
            Case("native window, resize, groups, save and process restart " + preset, delegate {
                Seed("classic"); MainWindow w = Open(); try {
                    settings = delegate(Window d) {
                        ComboBox combo = Presets(d); Assert(combo.Items.Count == 4, "Preset choices missing");
                        combo.SelectedIndex = selected; Click(d, "Сохранить");
                    };
                    Call(w, "ShowSettings");
                    Assert(Load().MainBackgroundPreset == preset, "Preset not saved");
                    Background(w, preset); Picture(w, preset + "-normal");
                    w.Width = w.MinWidth; w.Height = w.MinHeight; Drain();
                    Background(w, preset); Picture(w, preset + "-minimum");
                    w.WindowState = WindowState.Maximized; Drain();
                    Background(w, preset); Picture(w, preset + "-maximized");
                    w.WindowState = WindowState.Normal; Drain();
                    for (int i = 0; i < 30; i++) {
                        w.Width = w.MinWidth + i * 13; w.Height = w.MinHeight + (i % 7) * 23; Drain(); Background(w, preset);
                    }
                    TextBox search = (TextBox)Get(w, "_search");
                    search.Text = "нет совпадений"; Drain(); Assert(((TextBlock)Get(w, "_empty")).IsVisible, "Search empty state broken");
                    search.Text = ""; Drain(); Background(w, preset);
                    settings = delegate(Window d) {
                        Assert(Presets(d).SelectedIndex == selected, "Settings reopened with wrong selection");
                        Presets(d).SelectedIndex = (selected + 1) % 4; Click(d, "Отмена");
                    };
                    Call(w, "ShowSettings"); Assert(Load().MainBackgroundPreset == preset, "Cancelled preset saved"); Background(w, preset);
                    settings = delegate(Window d) { Presets(d).SelectedIndex = (selected + 1) % 4; Click(d, "Сохранить"); };
                    int errors = SaveErrors;
                    using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        Call(w, "ShowSettings");
                    Assert(SaveErrors == errors + 1 && Load().MainBackgroundPreset == preset, "Failed save changed preset");
                    Background(w, preset);
                } finally { w.Close(); }
                ReopenProcess(preset);
            });
        }
        Case("legacy JSON without preset and unknown preset remain readable without load-time writes", delegate {
            Seed("classic");
            foreach (string extra in new string[] { "", ",\"mainBackgroundPreset\":\"unknown\"", ",\"mainBackgroundPreset\":null" }) {
                string json = "{\"version\":5,\"items\":[],\"groups\":[]" + extra + "}";
                File.WriteAllText(ConfigStore.FilePath, json);
                Assert(Load().MainBackgroundPreset == "blue-arcs", "Missing/unknown preset did not fall back");
                Assert(File.ReadAllText(ConfigStore.FilePath) == json, "Loading rewrote legacy JSON");
            }
        });
        Case("group opacity alpha, normalization, and unchanged legacy default", delegate {
            Assert(FluentUi.NormalizeGroupOpacity(null) == 88 &&
                FluentUi.NormalizeGroupOpacity(-1) == 88 &&
                FluentUi.NormalizeGroupOpacity(105) == 88, "Invalid opacity default");
            foreach (string preset in new string[] { "blue-arcs", "midnight", "graphite" }) {
                Brush standard = FluentUi.MainGroupBackground(preset);
                LinearGradientBrush defaultBrush = (LinearGradientBrush)standard;
                LinearGradientBrush restored = (LinearGradientBrush)FluentUi.MainGroupBackground(preset, 88);
                LinearGradientBrush light = (LinearGradientBrush)FluentUi.MainGroupBackground(preset, 50);
                LinearGradientBrush solid = (LinearGradientBrush)FluentUi.MainGroupBackground(preset, 100);
                Assert(defaultBrush.IsFrozen && light.IsFrozen && solid.IsFrozen,
                    "Group brush is mutable");
                Assert(defaultBrush.GradientStops[0].Color == restored.GradientStops[0].Color &&
                    defaultBrush.GradientStops[1].Color == restored.GradientStops[1].Color,
                    "Default group appearance changed");
                Assert(light.GradientStops[0].Color.A < restored.GradientStops[0].Color.A &&
                    solid.GradientStops[0].Color.A == 255 && solid.GradientStops[1].Color.A == 255,
                    "Opacity is not applied to group background");
            }
            Seed("blue-arcs");
            foreach (string extra in new string[] { "", ",\"groupOpacity\":0", ",\"groupOpacity\":120" }) {
                string json = "{\"version\":5,\"items\":[],\"groups\":[]" + extra + "}";
                File.WriteAllText(ConfigStore.FilePath, json);
                Assert(FluentUi.NormalizeGroupOpacity(Load().GroupOpacity) == 88,
                    "Old or invalid opacity was not normalized");
                Assert(File.ReadAllText(ConfigStore.FilePath) == json,
                    "Loading opacity unexpectedly rewrote JSON");
            }
        });
        Case("group opacity live preview, save, and cancel", delegate {
            Seed("blue-arcs");
            MainWindow w = Open(); try {
                StackPanel groups = (StackPanel)Get(w, "_tileHost");
                Border group = null;
                foreach (UIElement item in groups.Children) {
                    Border candidate = item as Border;
                    if (candidate != null && Object.Equals(candidate.Tag, "AppShelfMainGroupCard"))
                        group = candidate;
                }
                Assert(group != null, "No real group card to preview");
                Brush before = group.Background;
                settings = delegate(Window d) {
                    Slider slider = Opacity(d);
                    Assert(Math.Abs(slider.Value - 88) < 0.01, "Wrong default slider value");
                    slider.ApplyTemplate(); Drain();
                    System.Windows.Controls.Primitives.Track track =
                        (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
                    Assert(track != null && track.Thumb != null &&
                        track.Thumb.Width == 16 && track.Orientation == Orientation.Horizontal,
                        "Custom rounded opacity slider is missing");
                    slider.Value = 50; Drain();
                    bool percentage = false;
                    foreach (TextBlock t in Controls<TextBlock>(d))
                        if (t.Text == "50%") percentage = true;
                    Assert(percentage, "Opacity percentage did not refresh");
                    Border sampleBackdrop = null, sampleGroup = null;
                    foreach (Border b in Controls<Border>(d)) {
                        if (Object.Equals(b.Tag, "GroupOpacityPreviewBackdrop")) sampleBackdrop = b;
                        if (Object.Equals(b.Tag, "GroupOpacityPreviewCard")) sampleGroup = b;
                    }
                    Assert(sampleBackdrop != null && sampleGroup != null &&
                        sampleBackdrop.Height == 104 && sampleGroup.IsVisible,
                        "Compact in-dialog group preview is missing");
                    LinearGradientBrush sampleFill = sampleGroup.Background as LinearGradientBrush;
                    LinearGradientBrush sampleExpected =
                        (LinearGradientBrush)FluentUi.MainGroupBackground("blue-arcs", 50);
                    Assert(sampleFill != null &&
                        sampleFill.GradientStops[0].Color == sampleExpected.GradientStops[0].Color,
                        "In-dialog preview did not update opacity");
                    // Bring the in-dialog preview into the viewport before rendering
                    // the actual window: the settings dialog is intentionally scrollable.
                    sampleBackdrop.BringIntoView();
                    Drain();
                    Picture(d, "opacity-inline-50");
                    LinearGradientBrush preview = group.Background as LinearGradientBrush;
                    LinearGradientBrush expected = (LinearGradientBrush)FluentUi.MainGroupBackground("blue-arcs", 50);
                    Assert(preview != null &&
                        preview.GradientStops[0].Color == expected.GradientStops[0].Color,
                        "Real group surface did not update");
                    // The background selection is also preview-only until Save.
                    Presets(d).SelectedIndex = 1; Drain();
                    preview = (LinearGradientBrush)group.Background;
                    expected = (LinearGradientBrush)FluentUi.MainGroupBackground("midnight", 50);
                    Assert(preview.GradientStops[0].Color == expected.GradientStops[0].Color,
                        "Preset change did not refresh real group preview");
                    Click(d, "Отмена");
                };
                Call(w, "ShowSettings");
                Assert(FluentUi.NormalizeGroupOpacity(Load().GroupOpacity) == 88 &&
                    Load().MainBackgroundPreset == "blue-arcs",
                    "Cancel persisted preview-only values");
                LinearGradientBrush restored = (LinearGradientBrush)group.Background;
                LinearGradientBrush initial = (LinearGradientBrush)before;
                Assert(restored.GradientStops[0].Color == initial.GradientStops[0].Color &&
                    restored.GradientStops[1].Color == initial.GradientStops[1].Color,
                    "Cancel did not restore the original group colors");
                settings = delegate(Window d) {
                    Opacity(d).Value = 100;
                    TileSize(d).Value = 125;
                    Click(d, "Сохранить");
                };
                Call(w, "ShowSettings");
                Assert(FluentUi.NormalizeGroupOpacity(Load().GroupOpacity) == 100,
                    "Saved opacity not persisted");
                Assert(Load().TileScalePercent == 125,
                    "Saved tile size not persisted");
                Button scaledTile = null;
                foreach (Button b in Controls<Button>(w))
                    if (b.Tag is AppItem) { scaledTile = b; break; }
                Assert(scaledTile != null && Math.Abs(scaledTile.Width - 125) < 0.01,
                    "Tile size not applied to existing normal-mode tile");
                settings = delegate(Window d) {
                    Assert(Math.Abs(Opacity(d).Value - 100) < 0.01 &&
                        Math.Abs(TileSize(d).Value - 125) < 0.01,
                        "Opacity or tile size not restored when reopening Settings");
                    TileSize(d).Value = 75;
                    Click(d, "Отмена");
                };
                Call(w, "ShowSettings");
                Assert(Load().TileScalePercent == 125,
                    "Cancelled tile size changed persisted value");
            } finally { w.Close(); }
        });
        Case("clean arc silhouettes retain internal gradients and corner anchoring", delegate {
            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) {
                DrawingBrush brush = (DrawingBrush)FluentUi.MainWindowBackground("blue-arcs", 510 + i * 17, 390 + i * 5);
                DrawingGroup drawing = (DrawingGroup)brush.Drawing;
                Assert(drawing.Children.Count == 4 && drawing.OpacityMask == null,
                    "Arc silhouettes were replaced with blurred layers");
                foreach (Drawing child in drawing.Children) Assert(child is GeometryDrawing, "Raster background introduced");
                for (int corner = 2; corner < 4; corner++) {
                    GeometryDrawing arc = (GeometryDrawing)drawing.Children[corner];
                    GradientBrush paint = arc.Brush as GradientBrush;
                    Assert(paint != null && paint.Opacity == 1 && paint.GradientStops.Count == 3,
                        "Internal arc gradient missing");
                    foreach (GradientStop stop in paint.GradientStops)
                        Assert(stop.Color.A == 255, "Arc edge is being faded through transparency");
                    Matrix transform = arc.Geometry.Transform.Value;
                    Assert(Math.Abs(transform.M11 - transform.M22) < 0.00001 &&
                        transform.M12 == 0 && transform.M21 == 0, "Arc proportions stretched");
                }
                Rect top = ((GeometryDrawing)drawing.Children[2]).Geometry.Bounds;
                Rect lower = ((GeometryDrawing)drawing.Children[3]).Geometry.Bounds;
                Assert(Math.Abs(top.Left) < 0.01 && Math.Abs(top.Top) < 0.01 &&
                    Math.Abs(lower.Right - brush.Viewbox.Width) < 0.01 &&
                    Math.Abs(lower.Bottom - brush.Viewbox.Height) < 0.01, "Arcs detached from corners");
            }
            Console.WriteLine("100 vector arc backgrounds: " + clock.ElapsedMilliseconds + "ms");
        });
        app.Shutdown();
        Console.WriteLine("Background cases passed=" + passed + "; failed=" + failed + "; unhandled=" + unhandled);
        return failed == 0 && unhandled == 0 ? 0 : 1;
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = Join-Path $temp 'BackgroundProbe.exe'
    $options.CompilerOptions = '/main:BackgroundTests'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    & $options.OutputAssembly
    if ($LASTEXITCODE -ne 0) { throw "Background checks failed. Isolated data: $temp" }
    Write-Output "PASS background tests. Isolated data and WPF images: $temp"
} finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $previous }
