using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;

public static class Ui218Probe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed, failed, unhandled;
    public static int SaveErrors;
    public static ContextMenu Tools;
    private static Action<Window> modal;
    private static readonly string[] Categories = { "network", "weather", "time" };
    private static string SizeFile { get { return Path.Combine(ConfigStore.DirectoryPath, "window-sizes.json"); } }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double actual, double expected, string message) { Assert(Math.Abs(actual - expected) < 1.1, message + ": " + actual + " != " + expected); }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static object Call(MainWindow w, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static AppConfig Live(MainWindow w) { return (AppConfig)Get(w, "_config"); }
    private static AppConfig Load()
    {
        string warning; bool safe; AppConfig c = ConfigStore.Load(out warning, out safe);
        Assert(safe && warning == null, "Config failed: " + warning); return c;
    }
    private static void Seed()
    {
        ConfigStore.Save(new AppConfig { ShowNetwork = false, ShowWeather = false, ShowPomodoro = true,
            ShowCountdownTimer = true, RdpKeepAwake = false, MinimizeToTray = false,
            BottomPanelMode = "categories", MainBackgroundPreset = "classic", SettingsLastSection = "appearance" });
    }
    private static MainWindow Open()
    {
        MainWindow w = new MainWindow();
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        w.ShowInTaskbar = false; w.Show(); Drain(); return w;
    }
    public static bool? Modal(Window dialog)
    {
        Exception error = null;
        dialog.ShowInTaskbar = false;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
            try { Assert(modal != null, "Unexpected modal " + dialog.Title); modal(dialog); }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        bool? result = dialog.ShowDialog();
        if (error != null) throw error;
        return result;
    }
    public static System.Windows.Forms.DialogResult Modal(System.Windows.Forms.FolderBrowserDialog dialog)
    {
        throw new Exception("Unexpected folder picker");
    }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> list = new List<T>(); if (root is T) list.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject) list.AddRange(Controls<T>((DependencyObject)child));
        return list;
    }
    private static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain(); }
    private static void Click(Window w, string caption)
    {
        foreach (Button b in Controls<Button>(w)) if (Convert.ToString(b.Content) == caption) { Click(b); return; }
        throw new Exception("Button missing: " + caption);
    }
    private static CheckBox Category(Window w, string key)
    {
        foreach (CheckBox c in Controls<CheckBox>(w)) if (Object.Equals(c.Tag, key)) return c;
        throw new Exception("Category missing: " + key);
    }
    private static ComboBox Presets(Window w)
    {
        foreach (ComboBox c in Controls<ComboBox>(w))
            foreach (object entry in c.Items)
                if (entry is ComboBoxItem && Object.Equals(((ComboBoxItem)entry).Tag, "blue-arcs")) return c;
        throw new Exception("Preset selector missing");
    }
    private static List<Button> Choices(Window w)
    {
        List<Button> result = new List<Button>();
        foreach (Button b in Controls<Button>(w)) if (b.Content is StackPanel && b.Width == 210) result.Add(b);
        return result;
    }
    private static void Layout(MainWindow w, int count)
    {
        Drain(); UniformGrid tabs = (UniformGrid)Get(w, "_bottomCategoryTabs");
        Assert(tabs.IsVisible && tabs.Columns == count && tabs.Children.Count == count, "Wrong category count");
        double sum = 0;
        foreach (Button b in tabs.Children) {
            double occupied = b.ActualWidth + b.Margin.Left + b.Margin.Right;
            Near(occupied, tabs.ActualWidth / count, "Unequal category width"); sum += occupied;
        }
        Near(sum, tabs.ActualWidth, "Unused category width");
    }
    private static void Case(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void Restart(params string[] args)
    {
        RestartAt(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), args);
    }
    private static void RestartAt(string data, params string[] args)
    {
        ProcessStartInfo info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, String.Join(" ", args));
        info.UseShellExecute = false;
        info.EnvironmentVariables["APPSHELF_TEST_DATA"] = data;
        using (Process child = Process.Start(info)) {
            if (!child.WaitForExit(30000)) { child.Kill(); child.WaitForExit(); throw new Exception("Restart timeout"); }
            Assert(child.ExitCode == 0, "Restart verification failed: " + String.Join(" ", args));
        }
    }
    private static void Settings(MainWindow w, Action<Window> inspect)
    {
        modal = inspect; Call(w, "ShowSettings");
    }
    private static Window Factory(MainWindow w, string kind)
    {
        if (kind == "weather") return new WeatherForecastWindow(delegate {}, delegate {});
        if (kind == "collections") return new CollectionsWindow(w);
        if (kind == "diagnostics") return new HappDiagnosticsWindow(new AppConfig());
        if (kind == "countdown" || kind == "pomodoro") {
            Call(w, kind == "countdown" ? "ShowCountdownWindow" : "ShowPomodoroWindow");
            return (Window)Get(w, kind == "countdown" ? "_countdownWindow" : "_pomodoroWindow");
        }
        if (kind == "reminder-list") {
            Call(w, "ShowRemindersWindow"); return (Window)Get(w, "_remindersWindow");
        }
        if (kind == "reminder-alert") {
            return (Window)typeof(MainWindow).GetMethod("NewReminderWindow", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { "Напоминания", 470.0, 325.0 });
        }
        if (kind == "fields-a" || kind == "fields-b") {
            return (Window)typeof(CollectionsWindow).GetMethod("Dialog", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { w, "Поля — " + kind, 650.0, 530.0 });
        }
        throw new Exception("Unknown window " + kind);
    }
    private static void WithWindow(MainWindow w, string kind, Action<Window> inspect)
    {
        if (kind == "settings") { Settings(w, inspect); return; }
        Window d = Factory(w, kind);
        try { if (!d.IsVisible) { d.ShowInTaskbar = false; d.Show(); } Drain(); inspect(d); }
        finally { d.Close(); Drain(); }
    }
    private static string Image(string name, bool jpeg)
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), name);
        byte[] pixels = new byte[80 * 50 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 35; pixels[i+1] = 90; pixels[i+2] = 210; pixels[i+3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(80, 50, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
        BitmapEncoder encoder = jpeg ? (BitmapEncoder)new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (Stream output = File.Create(path)) encoder.Save(output);
        return path;
    }
    private static void Gallery(MainWindow w, Action<Window> inspect)
    {
        modal = inspect;
        foreach (object entry in Tools.Items) {
            MenuItem item = entry as MenuItem;
            if (item != null && Convert.ToString(item.Header) == "Выбрать фон…") {
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Drain(); return;
            }
        }
        throw new Exception("Gallery not in overflow menu");
    }
    [STAThread] public static int Main(string[] args)
    {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Isolation failed");
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; FluentUi.Install(app);
        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
            unhandled++; Console.WriteLine("UNHANDLED " + e.Exception); e.Handled = true;
        };
        if (args.Length > 0) {
            try {
                if (args[0] == "damaged") {
                    Seed(); string before = File.ReadAllText(SizeFile);
                    MainWindow owner = Open();
                    try { WithWindow(owner, "weather", delegate(Window d) { d.Width += 25; Drain(); }); }
                    finally { owner.Close(); }
                    Assert(File.ReadAllText(SizeFile) == before, "Damaged sizes overwritten");
                    app.Shutdown(); return unhandled == 0 ? 0 : 1;
                }
                MainWindow w = Open();
                try {
                    if (args[0] == "categories") Layout(w, Int32.Parse(args[1]));
                    else if (args[0] == "background") {
                        Assert(Live(w).MainBackgroundPreset == args[1], "Background lost on restart");
                        Assert(((DockPanel)Get(w, "_mainBackgroundHost")).Background is ImageBrush, "Custom background not restored");
                    } else WithWindow(w, args[0], delegate(Window d) {
                        Near(d.Width, Double.Parse(args[1], CultureInfo.InvariantCulture), "Restart width");
                        Near(d.Height, Double.Parse(args[2], CultureInfo.InvariantCulture), "Restart height");
                    });
                } finally { w.Close(); }
                app.Shutdown(); return unhandled == 0 ? 0 : 1;
            } catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        Case("old JSON defaults to three categories without rewriting", delegate {
            Seed(); string json = "{\"version\":5,\"items\":[],\"groups\":[],\"bottomPanelMode\":\"categories\"}";
            File.WriteAllText(ConfigStore.FilePath, json);
            MainWindow w = Open(); try { Layout(w, 3); Assert(File.ReadAllText(ConfigStore.FilePath) == json, "Load rewrote JSON"); } finally { w.Close(); }
        });
        foreach (int count in new int[] { 1, 2, 3 }) { int wanted = count;
            Case("category settings, equal widths, reopen and restart count=" + count, delegate {
                Seed(); MainWindow w = Open();
                try {
                    Settings(w, delegate(Window d) {
                        for (int i = 0; i < 3; i++) Category(d, Categories[i]).IsChecked = i < wanted;
                        if (wanted == 1) Assert(!Category(d, "network").IsEnabled, "Last category can be disabled");
                        Assert(Category(d, "weather").IsEnabled, "Other categories cannot be re-enabled");
                        Click(d, "Сохранить");
                    });
                    Layout(w, wanted);
                    Assert(Live(w).ShowPomodoro && Live(w).ShowCountdownTimer != false, "Hiding time disabled modules");
                    Assert(Load().BottomPanelCategories.Count == wanted, "Categories not saved");
                    Settings(w, delegate(Window d) {
                        for (int i = 0; i < 3; i++) Assert(Category(d, Categories[i]).IsChecked == (i < wanted), "Choice lost");
                        Click(d, "Отмена");
                    });
                    foreach (string mode in new string[] { "compact", "detailed", "categories" }) {
                        Call(w, "SetBottomPanelMode", mode);
                        Assert(((UniformGrid)Get(w, "_bottomCategoryTabs")).IsVisible == (mode == "categories"), "Other mode broken");
                    }
                    Layout(w, wanted);
                } finally { w.Close(); }
                Restart("categories", wanted.ToString());
            });
        }
        Case("all category subsets retain active modules and close hidden detail", delegate {
            Seed(); MainWindow w = Open();
            try {
                Live(w).ShowNetwork = true; Live(w).ShowWeather = true;
                for (int mask = 1; mask < 8; mask++) {
                    Live(w).BottomPanelCategories = new List<string>();
                    for (int i = 0; i < 3; i++) if ((mask & (1 << i)) != 0) Live(w).BottomPanelCategories.Add(Categories[i]);
                    typeof(MainWindow).GetField("_bottomOpenCategory", Private).SetValue(w, "time");
                    Call(w, "ApplyBottomPanelMode"); Layout(w, Live(w).BottomPanelCategories.Count);
                    if ((mask & 4) == 0) Assert(Get(w, "_bottomOpenCategory") == null, "Hidden details remain open");
                    Assert(Live(w).ShowNetwork && Live(w).ShowWeather && Live(w).ShowPomodoro, "Module flag changed");
                }
                Live(w).ShowNetwork = false; Live(w).ShowWeather = false;
            } finally { w.Close(); }
        });
        Case("category cancel and failed save retain the current panel and persisted choice", delegate {
            Seed(); MainWindow w = Open();
            try {
                Settings(w, delegate(Window d) {
                    Category(d, "weather").IsChecked = false; Category(d, "time").IsChecked = false;
                    Click(d, "Отмена");
                });
                Layout(w, 3); Assert(Load().BottomPanelCategories == null, "Cancelled categories saved");
                int errors = SaveErrors;
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Settings(w, delegate(Window d) {
                        Category(d, "weather").IsChecked = false; Category(d, "time").IsChecked = false;
                        Click(d, "Сохранить");
                    });
                Assert(SaveErrors == errors + 1, "Save failure not reported");
                Layout(w, 3); Assert(Load().BottomPanelCategories == null, "Rejected categories saved");
                foreach (List<string> invalid in new List<string>[] { new List<string>(), new List<string> { "unknown" } }) {
                    Live(w).BottomPanelCategories = invalid; Call(w, "ApplyBottomPanelMode"); Layout(w, 3);
                }
            } finally { w.Close(); }
        });
        foreach (string kind in new string[] { "settings", "reminder-list", "reminder-alert", "collections", "weather", "countdown", "pomodoro", "diagnostics" }) {
            string k = kind;
            Case("independent window size reopen and process restart " + k, delegate {
                Seed(); MainWindow w = Open(); double width = 0, height = 0;
                try {
                    WithWindow(w, k, delegate(Window d) {
                        d.Width = Math.Min(SystemParameters.WorkArea.Width - 24, d.Width + 37);
                        d.Height = Math.Min(SystemParameters.WorkArea.Height - 24, d.Height + 29);
                        Drain(); width = d.ActualWidth; height = d.ActualHeight;
                    });
                    WithWindow(w, k, delegate(Window d) { Near(d.Width, width, "Reopen width"); Near(d.Height, height, "Reopen height"); });
                } finally { w.Close(); }
                Restart(k, width.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.InvariantCulture));
            });
        }
        Case("same-title reminders are independent and collection field keys ignore names", delegate {
            Seed(); MainWindow w = Open();
            try {
                WithWindow(w, "reminder-list", delegate(Window d) { d.Width = 810; d.Height = 650; Drain(); });
                WithWindow(w, "reminder-alert", delegate(Window d) { d.Width = 550; d.Height = 410; Drain(); });
                WithWindow(w, "reminder-list", delegate(Window d) { Near(d.Width, 810, "Reminder title collision"); Near(d.Height, 650, "Reminder height collision"); });
                WithWindow(w, "reminder-alert", delegate(Window d) { Near(d.Width, 550, "Alert title collision"); });
                WithWindow(w, "fields-a", delegate(Window d) { d.Width = 690; d.Height = 570; Drain(); });
                WithWindow(w, "fields-b", delegate(Window d) { Near(d.Width, 690, "Collection name created new key"); });
                string json = File.ReadAllText(SizeFile);
                Assert(!json.Contains("fields-a") && !json.Contains("fields-b"), "Dynamic collection names persisted");
                Assert(!json.Contains("\"left\"") && !json.Contains("\"top\""), "Window positions persisted");
            } finally { w.Close(); }
        });
        Case("size restore clamps to minimum, maximum and available screen; position is not restored", delegate {
            Window d = new Window { Title = "AppShelf — bounds test", Width = 500, Height = 400, MinWidth = 390, MinHeight = 240 };
            FluentUi.PrepareWindow(d); d.ShowInTaskbar = false; d.Show(); d.Width = 4000; d.Height = 3000; Drain(); d.Close();
            d = new Window { Title = "AppShelf — bounds test", Width = 500, Height = 400, MinWidth = 600, MinHeight = 450, MaxWidth = 800, MaxHeight = 600, Left = 17, Top = 19 };
            FluentUi.PrepareWindow(d);
            Assert(d.Width >= 600 && d.Width <= Math.Min(800, Math.Max(600, SystemParameters.WorkArea.Width - 24)), "Width not clamped");
            Assert(d.Height >= 450 && d.Height <= Math.Min(600, Math.Max(450, SystemParameters.WorkArea.Height - 24)), "Height not clamped");
            Assert(d.Left == 17 && d.Top == 19, "Position restored"); d.Close();
        });
        Case("main window placement remains separate from secondary sizes", delegate {
            Seed(); MainWindow w = Open();
            try { w.Width = 910; w.Height = 710; Drain(); Call(w, "SaveWindowPlacement"); }
            finally { w.Close(); }
            w = Open(); try { Near(w.Width, 910, "Main width"); Near(w.Height, 710, "Main height"); } finally { w.Close(); }
            Assert(!File.ReadAllText(SizeFile).Contains("AppShelf.MainWindow"), "Main uses secondary store");
        });
        foreach (WindowState state in new WindowState[] { WindowState.Maximized, WindowState.Minimized }) {
        WindowState closingState = state;
        Case("secondary window retains normal restore size when closed " + state, delegate {
            Seed(); MainWindow w = Open();
            try {
                WithWindow(w, "countdown", delegate(Window d) {
                    d.Width = 560; d.Height = 440; Drain();
                    d.WindowState = closingState; Drain();
                });
                WithWindow(w, "countdown", delegate(Window d) {
                    Near(d.Width, 560, "Maximized width persisted instead of restore width");
                    Near(d.Height, 440, "Maximized height persisted instead of restore height");
                    Assert(d.WindowState == WindowState.Normal, "Window state persisted");
                });
            } finally { w.Close(); }
        });
        }
        Case("cancelled close and unchanged reopen do not write window sizes", delegate {
            string before = File.ReadAllText(SizeFile);
            Window d = new Window { Title = "AppShelf — cancelled close", Width = 500, Height = 400 };
            FluentUi.PrepareWindow(d); d.ShowInTaskbar = false; d.Show(); d.Width = 550; Drain();
            bool cancel = true;
            d.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = cancel; };
            d.Close(); Assert(d.IsVisible && File.ReadAllText(SizeFile) == before, "Cancelled close saved sizes");
            cancel = false; d.Close();
            Assert(File.ReadAllText(SizeFile) != before, "Successful close did not save sizes");
            before = File.ReadAllText(SizeFile);
            d = new Window { Title = "AppShelf — cancelled close", Width = 500, Height = 400 };
            FluentUi.PrepareWindow(d); d.ShowInTaskbar = false; d.Show(); Drain(); d.Close();
            Assert(File.ReadAllText(SizeFile) == before, "Unchanged reopen rewrote sizes");
        });
        Case("damaged window sizes do not crash or overwrite the damaged file", delegate {
            string root = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "damaged");
            Directory.CreateDirectory(Path.Combine(root, "AppShelf"));
            File.WriteAllText(Path.Combine(root, "AppShelf", "window-sizes.json"), "not JSON");
            RestartAt(root, "damaged");
        });
        Case("gallery JPG/PNG thumbnails, instant apply, settings sync, save failure and restart", delegate {
            Seed(); string png = Image("Оригинал.png", false), jpg = Image("Original.jpg", true);
            string pngBefore = Convert.ToBase64String(File.ReadAllBytes(png)), jpgBefore = Convert.ToBase64String(File.ReadAllBytes(jpg));
            string p = CustomBackgroundStore.Import(png), j = CustomBackgroundStore.Import(jpg);
            MainWindow w = Open();
            try {
                Gallery(w, delegate(Window d) {
                    List<Button> choices = Choices(d); Assert(choices.Count == 6, "Library incomplete");
                    foreach (Button b in choices) {
                        Border picture = Controls<Border>(b)[0]; Assert(picture.Background != null && b.IsEnabled, "Missing thumbnail");
                        if (Convert.ToString(b.ToolTip).EndsWith(".png") || Convert.ToString(b.ToolTip).EndsWith(".jpg")) {
                            ImageBrush image = picture.Background as ImageBrush;
                            Assert(image != null && image.IsFrozen && ((BitmapSource)image.ImageSource).PixelWidth <= 320, "Raster thumbnail not decoded");
                        }
                    }
                    Button custom = choices.Find(b => Convert.ToString(b.ToolTip) == "Оригинал.png");
                    Click(custom); Assert(Live(w).MainBackgroundPreset == p && Load().MainBackgroundPreset == p, "Selection not persisted");
                    Assert(((DockPanel)Get(w, "_mainBackgroundHost")).Background is ImageBrush, "Selection not applied live");
                    int errors = SaveErrors;
                    using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        Click(choices.Find(b => Convert.ToString(b.ToolTip) == "Original.jpg"));
                    Assert(SaveErrors == errors + 1 && Live(w).MainBackgroundPreset == p, "Failed save changed wallpaper");
                });
                Settings(w, delegate(Window d) {
                    Assert(Object.Equals(((ComboBoxItem)Presets(d).SelectedItem).Tag, p), "Settings disagree with gallery");
                    foreach (ComboBoxItem item in Presets(d).Items) if (Object.Equals(item.Tag, j)) { Presets(d).SelectedItem = item; break; }
                    Click(d, "Сохранить");
                });
                Gallery(w, delegate(Window d) {
                    Button selected = Choices(d).Find(b => Convert.ToString(b.ToolTip) == "Original.jpg");
                    Assert(Object.ReferenceEquals(selected.BorderBrush, FluentUi.Accent), "Gallery does not reflect settings");
                });
            } finally { w.Close(); }
            Restart("background", j);
            Assert(Convert.ToBase64String(File.ReadAllBytes(png)) == pngBefore && Convert.ToBase64String(File.ReadAllBytes(jpg)) == jpgBefore, "Original modified");
        });
        Case("large gallery scrolls and damaged or missing images are safe", delegate {
            Seed(); string original = Image("many.png", false);
            Directory.CreateDirectory(CustomBackgroundStore.DirectoryPath);
            for (int i = 0; i < 180; i++)
                File.Copy(original, Path.Combine(CustomBackgroundStore.DirectoryPath, Guid.NewGuid().ToString("N") + "_sample-" + i + ".png"));
            string bad = Path.Combine(CustomBackgroundStore.DirectoryPath, Guid.NewGuid().ToString("N") + "_broken.png");
            File.WriteAllText(bad, "not an image");
            string missing = "custom:" + Guid.NewGuid().ToString("N") + "_missing.png";
            MethodInfo thumbnail = typeof(MainWindow).GetMethod("BackgroundGalleryThumbnail", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(thumbnail.Invoke(null, new object[] { missing }) == null, "Missing thumbnail not safe");
            MainWindow w = Open();
            try {
                Stopwatch clock = Stopwatch.StartNew();
                Gallery(w, delegate(Window d) {
                    List<Button> choices = Choices(d);
                    Assert(choices.Count == 4 + CustomBackgroundStore.ListPresets().Count, "Large library omitted items");
                    Assert(!choices.Find(b => Convert.ToString(b.ToolTip) == "broken.png").IsEnabled, "Broken image selectable");
                    ScrollViewer scroll = Controls<ScrollViewer>(d)[0];
                    Assert(scroll.ScrollableHeight > 0, "Large gallery not scrollable");
                    scroll.ScrollToEnd(); Drain(); Assert(scroll.VerticalOffset > 0, "Gallery cannot scroll");
                    string preset = CustomBackgroundStore.ListPresets().Find(x => x.EndsWith("_sample-0.png"));
                    File.Delete(CustomBackgroundStore.FilePath(preset));
                    Assert(thumbnail.Invoke(null, new object[] { preset }) == null, "Removed file thumbnail not safe");
                });
                Console.WriteLine("Gallery 180 files open/scroll: " + clock.ElapsedMilliseconds + " ms");
            } finally { w.Close(); }
        });
        Case("portrait wallpapers use bounded readable thumbnails", delegate {
            string path = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "portrait.png");
            BitmapSource source = BitmapSource.Create(1, 2160, 96, 96, PixelFormats.Bgra32, null, new byte[2160 * 4], 4);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using (Stream output = File.Create(path)) encoder.Save(output);
            string preset = CustomBackgroundStore.Import(path);
            ImageBrush brush = (ImageBrush)typeof(MainWindow).GetMethod("BackgroundGalleryThumbnail", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { preset });
            Assert(brush != null, "Valid portrait thumbnail unavailable");
            BitmapSource image = (BitmapSource)brush.ImageSource;
            Assert(image.PixelWidth <= 320 && image.PixelHeight <= 320, "Portrait thumbnail is oversized: " + image.PixelWidth + "x" + image.PixelHeight);
        });
        app.Shutdown();
        Console.WriteLine("UI218 passed=" + passed + "; failed=" + failed + "; unhandled=" + unhandled);
        return failed == 0 && unhandled == 0 ? 0 : 1;
    }
}
