using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;

public static class Ui219Probe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed, failed, unhandled;
    private static Action<Window> settings;
    private static string Root { get { return Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"); } }
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static object Call(MainWindow w, string name, params object[] args) {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static void Wait(int milliseconds) {
        DispatcherFrame frame = new DispatcherFrame();
        DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); Drain();
    }
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
            ShowPomodoro = false, RdpKeepAwake = false, MinimizeToTray = false,
            SettingsLastSection = "appearance", BottomPanelMode = "compact", BottomPanelOpacity = 100,
            GroupingEnabled = true };
        Directory.CreateDirectory(ConfigStore.DirectoryPath);
        string shortcut = Path.Combine(ConfigStore.DirectoryPath, "fixture.cmd");
        File.WriteAllText(shortcut, "@echo off\r\n");
        c.Groups.Add("Программы");
        c.Items.Add(new AppItem { Name = "Тестовое приложение", Path = shortcut, Group = "Программы" });
        ConfigStore.Save(c);
    }
    private static MainWindow Open() {
        MainWindow w = new MainWindow();
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        w.ShowInTaskbar = false; w.Show(); Drain();
        Assert(PresentationSource.FromVisual(w) != null, "No native WPF source"); return w;
    }
    private static Border Footer(MainWindow w) { return (Border)Get(w, "_footerBorder"); }
    private static void CheckOpacity(MainWindow w, int value) {
        Border footer = Footer(w);
        Assert(Math.Abs(footer.Background.Opacity - value / 100.0) < 0.001, "Wrong backdrop opacity");
        Assert(footer.Opacity == 1 && ((FrameworkElement)footer.Child).Opacity == 1, "Children became translucent");
        Assert(((TextBlock)Get(w, "_status")).Opacity == 1, "Status became translucent");
    }
    private static void Picture(FrameworkElement view, string name) {
        view.UpdateLayout();
        RenderTargetBitmap image = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),
            (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        image.Render(visual);
        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (FileStream stream = File.Create(Path.Combine(Root, name + ".png"))) encoder.Save(stream);
    }
    private static void Click(Window d, string text) {
        foreach (Button b in Controls<Button>(d)) if (Convert.ToString(b.Content) == text) {
            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return;
        }
        throw new Exception("Button missing: " + text);
    }
    private static Slider Opacity(Window d) {
        foreach (Slider s in Controls<Slider>(d)) if (s.Minimum == 0 && s.Maximum == 100) return s;
        throw new Exception("Footer opacity slider missing");
    }
    public static bool? Settings(Window d) {
        Exception error = null; d.ShowInTaskbar = false;
        d.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
            try { settings(d); } catch (Exception ex) { error = ex; }
            finally { if (d.IsVisible) d.Close(); }
        }));
        bool? result = d.ShowDialog(); if (error != null) throw error; return result;
    }
    private static void Restart(int opacity) {
        ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--reopen " + opacity);
        start.UseShellExecute = false;
        using (Process p = Process.Start(start)) {
            if (!p.WaitForExit(20000)) { p.Kill(); p.WaitForExit(); throw new Exception("Restart timed out"); }
            Assert(p.ExitCode == 0, "Restart failed");
        }
    }
    [STAThread] public static int Main(string[] args) {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Root, "AppShelf"), "Storage is not isolated");
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; FluentUi.Install(app);
        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
            unhandled++; Console.Error.WriteLine(e.Exception); e.Handled = true;
        };
        if (args.Length == 2 && args[0] == "--reopen") {
            try {
                int opacity = Int32.Parse(args[1]); Assert(Load().BottomPanelOpacity == opacity, "Saved value lost");
                MainWindow w = Open(); try { CheckOpacity(w, opacity); } finally { w.Close(); }
                app.Shutdown(); return unhandled == 0 ? 0 : 1;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        Case("legacy and invalid opacity load without rewriting JSON", delegate {
            Seed("blue-arcs");
            foreach (string field in new string[] { "", ",\"bottomPanelOpacity\":null", ",\"bottomPanelOpacity\":-1", ",\"bottomPanelOpacity\":101" }) {
                string json = "{\"version\":5,\"items\":[],\"groups\":[]" + field + "}";
                File.WriteAllText(ConfigStore.FilePath, json);
                Assert(Load().BottomPanelOpacity == 100, "Legacy default differs from 100");
                Assert(File.ReadAllText(ConfigStore.FilePath) == json, "Load rewrote legacy data");
            }
        });
        List<string> presets = new List<string> { "blue-arcs", "midnight", "graphite", "classic" };
        string photo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows\img0.jpg");
        if (File.Exists(photo)) presets.Add(CustomBackgroundStore.Import(photo));
        else Console.WriteLine("SKIP optional Windows wallpaper fixture unavailable: " + photo);
        // A white imported image is a worst-case backdrop for status contrast.
        string white = Path.Combine(Root, "white.png");
        using (System.Drawing.Bitmap image = new System.Drawing.Bitmap(800, 600)) {
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(image)) g.Clear(System.Drawing.Color.White);
            image.Save(white, System.Drawing.Imaging.ImageFormat.Png);
        }
        presets.Add(CustomBackgroundStore.Import(white));
        // Optional local photographic fixture; do not distribute vendor wallpaper in the repository.
        string vendorPhoto = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"Web\Wallpaper\HP Backgrounds\backgroundDefault.jpg");
        if (File.Exists(vendorPhoto)) presets.Add(CustomBackgroundStore.Import(vendorPhoto));
        for (int index = 0; index < presets.Count; index++) {
            string preset = presets[index]; int fixture = index;
            Case("continuous background, live opacity, cancel, save and restart " + preset, delegate {
                Seed(preset); MainWindow w = Open();
                try {
                    Grid content = (Grid)Get(w, "_mainContentGrid");
                    DockPanel host = (DockPanel)Get(w, "_mainBackgroundHost");
                    Assert(Object.ReferenceEquals(content.Parent, host) && Object.ReferenceEquals(Footer(w).Parent, host),
                        "Tiles/footer do not share a single backdrop");
                    Assert(Object.ReferenceEquals(content.Background, Brushes.Transparent), "Tiles hide the backdrop");
                    settings = delegate(Window d) {
                        foreach (int value in new int[] { 0, 25, 50, 75, 100 }) {
                            Opacity(d).Value = value; Drain(); CheckOpacity(w, value);
                            Picture(w, "background-" + fixture + "-" + value);
                        }
                        Opacity(d).Value = 0; Click(d, "Отмена");
                    };
                    Call(w, "ShowSettings"); CheckOpacity(w, 100);
                    Assert(Load().BottomPanelOpacity == 100, "Cancel persisted opacity");
                    settings = delegate(Window d) { Opacity(d).Value = 37; Click(d, "Сохранить"); };
                    Call(w, "ShowSettings"); CheckOpacity(w, 37);
                    Assert(Load().BottomPanelOpacity == 37, "Save did not persist opacity");
                    TextBlock status = (TextBlock)Get(w, "_status");
                    SolidColorBrush backdrop = (SolidColorBrush)((Border)status.Parent).Background;
                    Assert(backdrop.Color.A >= 190 && backdrop.Color.R <= 32 && status.FontSize >= 11.5,
                        "Status contrast backdrop missing");
                } finally { w.Close(); }
                Restart(37);
            });
        }
        Case("five-second status lifetime, search/repaint protection and timer restart", delegate {
            Seed("classic"); MainWindow w = Open();
            try {
                TextBlock status = (TextBlock)Get(w, "_status");
                Call(w, "ShowStatusMessage", "first", false);
                Wait(2800);
                ((TextBox)Get(w, "_search")).Text = "no matches"; Call(w, "RenderTiles"); Drain();
                Assert(status.Text == "first", "Search/repaint erased temporary message");
                Call(w, "ShowStatusMessage", "second", false);
                Wait(2800); Assert(status.Text == "second", "Old deadline erased the new message");
                Wait(2500); Assert(status.Text == (string)Get(w, "_defaultStatusText"), "Message did not expire");
                Call(w, "ShowStatusMessage", "persistent", true);
                Wait(5300); Assert(status.Text == "persistent", "Persistent warning expired");
            } finally { w.Close(); }
        });
        Case("all panel modes, long compact cards, expand/collapse and resize", delegate {
            Seed("blue-arcs"); MainWindow w = Open();
            try {
                AppConfig config = (AppConfig)Get(w, "_config");
                config.ShowWeather = true; config.ShowNetwork = true;
                config.BottomPanelFavorites = new List<string> { "weather", "reminders", "route" };
                ((TextBlock)Get(w, "_weather")).Text = "Очень длинный прогноз погоды: облачно с прояснениями, сильный ветер и продолжительные осадки";
                ((TextBlock)Get(w, "_remindersChipText")).Text = "Длинное напоминание: подготовить документы, оплатить счета и сохранить подтверждения";
                foreach (string mode in new string[] { "compact", "categories", "detailed" }) {
                    config.BottomPanelMode = mode; Call(w, "UpdateMonitorVisibility"); Drain();
                    Assert(((FrameworkElement)Get(w, mode == "compact" ? "_bottomCompactRow" :
                        mode == "categories" ? "_bottomCategoryTabs" : "_bottomFullMonitors")).IsVisible, "Panel mode missing");
                    foreach (double width in new double[] { 510, 700, 1000, 600 }) {
                        w.Width = Math.Max(w.MinWidth, width); w.Height = 800; Drain();
                        if (mode == "compact") {
                            Button expand = (Button)Get(w, "_bottomExpandButton");
                            foreach (bool expanded in new bool[] { false, true, false }) {
                                if ((bool)Get(w, "_bottomCompactExpanded") != expanded)
                                    expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                Drain();
                                var compactItems = (WrapPanel)Get(w, "_bottomCompactItems");
                                var detailItems = (WrapPanel)Get(w, "_bottomFullMonitors");
                                Assert(compactItems.IsVisible == !expanded && detailItems.IsVisible == expanded,
                                    "Expanded details did not replace compact indicators");
                                Assert(expanded ? compactItems.Children.Count == 0 : compactItems.Children.Count > 0,
                                    "Compact indicators duplicated or did not return after collapse");
                                double tallest = 0;
                                foreach (FrameworkElement item in (expanded ? detailItems : compactItems).Children)
                                    if (item.IsVisible) tallest = Math.Max(tallest, item.ActualHeight);
                                Assert(Math.Abs(expand.ActualHeight - Math.Max(tallest, expand.MinHeight)) < 0.6,
                                    "Expand height differs from tallest card");
                                Picture(w, "compact-" + width + "-" + expanded);
                            }
                        } else Picture(w, mode + "-" + width);
                    }
                }
            } finally { w.Close(); }
        });
        app.Shutdown();
        Console.WriteLine("UI 2.19 passed=" + passed + "; failed=" + failed + "; unhandled=" + unhandled);
        return failed == 0 && unhandled == 0 ? 0 : 1;
    }
}
