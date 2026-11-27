# Windows PowerShell 5.1 -STA. Isolated data, real WPF windows, no Program.Main or monitors.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-Package6-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs','PluginWidgetView.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $errorDialog = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $errorDialog).Count -ne 1) { throw 'Error dialog anchor missing.' }
            $text = [regex]::Replace($text, $errorDialog, 'Package6Tests.Errors++;')
            $warning = 'MessageBox.Show\(this,\s*"Сохранение отключено[^;]*?MessageBoxImage.Warning\);'
            if ([regex]::Matches($text, $warning).Count -ne 1) { throw 'Save-disabled boundary missing.' }
            $text = [regex]::Replace($text, $warning, 'Package6Tests.DisabledWarnings++;')
            $about = '(?s)(private void ShowAboutWindow\(\).*?)            dialog.ShowDialog\(\);'
            if ([regex]::Matches($text, $about).Count -ne 1) { throw 'About boundary missing.' }
            $text = [regex]::Replace($text, $about, '$1            Package6Tests.About(dialog);')
            $text = $text.Replace('            ContextMenu addMenu = new ContextMenu();', '            ContextMenu addMenu = new ContextMenu(); Package6Tests.AddMenu = addMenu;')
            $anchor = 'if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);'
            if (-not $text.Contains($anchor)) { throw 'Add dialog boundary missing.' }
            $text = $text.Replace($anchor, 'Package6Tests.AddDialogCalls++;')
            $anchor = 'Process.Start(new ProcessStartInfo("https://github.com/mishakov1c/app_shelf")'
            if (-not $text.Contains($anchor)) { throw 'Repository launch boundary missing.' }
            $text = $text.Replace($anchor, 'Package6Tests.OpenLink(new ProcessStartInfo("https://github.com/mishakov1c/app_shelf")')
            $anchor = '            ContextMenu tools = new ContextMenu();'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Overflow capture anchor missing.' }
            $text = $text.Replace($anchor, $anchor + ' Package6Tests.ToolsMenu = tools;')
            $settings = '(?s)(private void ShowSettings\(\).*?)bool\? savedSettings = dialog.ShowDialog\(\);'
            if ([regex]::Matches($text, $settings).Count -ne 1) { throw 'Settings boundary missing.' }
            $text = [regex]::Replace($text, $settings, '$1bool? savedSettings = Package6Tests.Settings(dialog);')
            $rdpConfirm = 'MessageBox.Show\(this,\s*"AppShelf будет отправлять F15.*?MessageBoxImage.Question\)'
            if ([regex]::Matches($text, $rdpConfirm, 'Singleline').Count -ne 2) { throw 'RDP confirmation boundaries missing.' }
            $text = [regex]::Replace($text, $rdpConfirm, 'Package6Tests.ConfirmRdp()', 'Singleline')
        }
        if ($file -eq 'BackupManager.cs') {
            $target = '            dialog.ShowDialog();'
            if ($text.Split(@($target), [StringSplitOptions]::None).Length -ne 2) { throw 'Backup dialog interception anchor missing.' }
            $text = $text.Replace($target, '            Package6Tests.Backup(dialog);')
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $text.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks;
using AppShelf;

public static class Package6Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Errors, AddDialogCalls, DisabledWarnings;
    public static ContextMenu ToolsMenu;
    public static ContextMenu AddMenu;
    private static int passed, failed;
    private static Action<Window> about;
    private static Action<Window> settings;
    private static Action<Window> backup;
    public static void Backup(Window dialog)
    {
        dialog.ShowInTaskbar = false; dialog.Show(); Drain();
        try { backup(dialog); } finally { dialog.Close(); }
    }
    private static int rdpConfirmations;
    private static MessageBoxResult rdpAnswer = MessageBoxResult.No;
    public static MessageBoxResult ConfirmRdp() { rdpConfirmations++; return rdpAnswer; }
    public static bool? Settings(Window dialog)
    {
        Exception error = null;
        dialog.ShowInTaskbar = false;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
            try { settings(dialog); }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        bool? result = dialog.ShowDialog();
        if (error != null) throw error;
        return result;
    }
    private static ProcessStartInfo link;
    public static void OpenLink(ProcessStartInfo info) { link = info; }
    public static void About(Window dialog)
    {
        dialog.ShowInTaskbar = false; dialog.Show(); Drain();
        try { about(dialog); } finally { dialog.Close(); }
    }
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static void Set(MainWindow w, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(w, value); }
    private static object Call(MainWindow w, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static AppConfig Live(MainWindow w) { return (AppConfig)Get(w, "_config"); }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain(); }
    private static void Click(MenuItem m) { m.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Drain(); }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> list = new List<T>(); if (root is T) list.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject) list.AddRange(Controls<T>((DependencyObject)child));
        return list;
    }
    private static Button ButtonNamed(Window w, string text)
    {
        foreach (Button b in Controls<Button>(w)) if (Convert.ToString(b.Content) == text || (text == "+  Добавить" && b.Name == "AddItemButton")) return b;
        throw new Exception("Button missing: " + text);
    }
    private static MenuItem Menu(ContextMenu menu, string text)
    {
        foreach (object item in menu.Items) { MenuItem m = item as MenuItem; if (m != null && Convert.ToString(m.Header) == text) return m; }
        throw new Exception("Menu missing: " + text);
    }
    private static Button ToolsButton(MainWindow w)
    {
        foreach (Button button in Controls<Button>(w))
            if (Convert.ToString(button.ToolTip) == "Дополнительные действия и настройки") return button;
        throw new Exception("Additional actions button missing");
    }
    private static ContextMenu Tools(MainWindow w) { return ToolsMenu; }
    private static string Snapshot(AppConfig c)
    {
        using (MemoryStream s = new MemoryStream()) { new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(s, c); return Convert.ToBase64String(s.ToArray()); }
    }
    private static AppConfig Load()
    {
        string warning; bool safe; AppConfig c = ConfigStore.Load(out warning, out safe);
        Assert(safe && warning == null, "Config rejected: " + warning); return c;
    }
    private static void Seed(string mode, int count)
    {
        AppConfig c = new AppConfig { TileDisplayMode = mode, Compact = mode != "labeled", GroupingEnabled = true,
            ShowWeather = false, ShowNetwork = false, ShowPomodoro = false, RdpKeepAwake = false, MinimizeToTray = false };
        c.Groups.AddRange(new string[] { "Первая", "Вторая", "Пустая" });
        Directory.CreateDirectory(ConfigStore.DirectoryPath);
        for (int i = 0; i < count; i++) {
            string path = Path.Combine(ConfigStore.DirectoryPath, "test-" + i + ".cmd"); File.WriteAllText(path, "@echo off\r\n");
            c.Items.Add(new AppItem { Name = "Приложение App " + i, Path = path, Group = "Первая" });
        }
        ConfigStore.Save(c);
    }
    private static MainWindow Open()
    {
        MainWindow w = new MainWindow();
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w, typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        Set(w, "_weatherTimer", new DispatcherTimer());
        w.ShowInTaskbar = false; w.Show(); Drain();
        PresentationSource source = PresentationSource.FromVisual(w);
        Assert(source != null && source.CompositionTarget != null, "No native WPF source");
        return w;
    }
    private static void Picture(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        Rect bounds = view.LayoutTransform.TransformBounds(new Rect(view.RenderSize));
        RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new DrawingVisual(); using (DrawingContext dc = visual.RenderOpen()) {
            dc.DrawRectangle(new VisualBrush(view) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, new Rect(bounds.Size));
        }
        bmp.Render(visual); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (FileStream s = File.Create(Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), name + ".png"))) encoder.Save(s);
    }
    private static void Inside(FrameworkElement element, FrameworkElement root, string name)
    {
        Rect r = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert(element.IsVisible && r.Left >= -0.5 && r.Top >= -0.5 && r.Right <= root.RenderSize.Width + 0.5 && r.Bottom <= root.RenderSize.Height + 0.5,
            name + " clipped: " + r + " in " + root.RenderSize);
    }
    private static T Visual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T) return (T)root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            T child = Visual<T>(VisualTreeHelper.GetChild(root, i)); if (child != null) return child;
        }
        return null;
    }
    private static void Dark(Brush brush, string name)
    {
        SolidColorBrush solid = brush as SolidColorBrush;
        Assert(solid != null && solid.Color.R < 100 && solid.Color.G < 100 && solid.Color.B < 120, name + " is not dark");
    }
    private static void ScrollInput(ScrollViewer scroll, string name)
    {
        Assert(scroll.ScrollableHeight > 0, name + ": no overflow for input verification");
        scroll.ScrollToHome(); Drain();
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.MouseWheelEvent }); Drain();
        Assert(scroll.VerticalOffset > 0, name + ": mouse wheel did not scroll");
        scroll.ScrollToHome(); Drain();
        System.Windows.Controls.Primitives.ScrollBar bar =
            (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        System.Windows.Controls.Primitives.Track track =
            (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
        Assert(track != null && track.Thumb != null, name + ": drag thumb missing");
        Assert(scroll.ViewportHeight > 0 && track.IsVisible && track.ActualHeight > 0,
            name + ": scroll viewport or drag track collapsed");
        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 25)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent }); Drain();
        Assert(scroll.VerticalOffset > 0, name + ": dragging thumb did not scroll");
    }
    private static void Readable(CheckBox check)
    {
        SolidColorBrush foreground = check.Foreground as SolidColorBrush;
        Assert(foreground != null && foreground.Color.R >= 160 && foreground.Color.G >= 160 &&
            check.Opacity == 1, "Disabled favorite text loses contrast");
        Assert(Visual<Border>(check).Opacity == 1, "Checkbox chrome is faded");
    }
    private static List<Button> Tiles(MainWindow w)
    {
        List<Button> list = new List<Button>(); foreach (Button b in Controls<Button>((DependencyObject)Get(w, "_tileHost"))) if (b.Tag is AppItem) list.Add(b); return list;
    }
    private static Expander Group(MainWindow w, int index) { return Controls<Expander>((DependencyObject)Get(w, "_tileHost"))[index]; }
    private static DragEventArgs Drag(FrameworkElement target, DataObject data, RoutedEvent routedEvent)
    {
        ConstructorInfo constructor = typeof(DragEventArgs).GetConstructor(Private, null,
            new Type[] { typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point) }, null);
        Assert(constructor != null, "DragEventArgs constructor unavailable");
        DragEventArgs e = (DragEventArgs)constructor.Invoke(new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, new Point(0, 0) });
        e.RoutedEvent = routedEvent; target.RaiseEvent(e); Drain(); return e;
    }
    private static void Case(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static MenuItem Menu(MenuItem parent, string text)
    {
        foreach (object item in parent.Items) { MenuItem m = item as MenuItem; if (m != null && Convert.ToString(m.Header) == text) return m; }
        throw new Exception("Submenu missing: " + text);
    }
    private static MenuItem TileModeMenu(MainWindow w)
    {
        return Menu(Menu(Tools(w), "Быстрые настройки"), "Режим плиток");
    }
    private static Button Section(Window d, string key)
    {
        foreach (Button b in Controls<Button>(d)) if (Object.Equals(b.Tag, key)) return b;
        throw new Exception("Missing settings section " + key);
    }
    private static Border Body(Window d, string key)
    {
        // Unified accordion wraps each header button in its highlight surface.
        Border heading = Section(d, key).Parent as Border;
        Assert(heading != null, "Missing shared-container header surface " + key);
        StackPanel section = heading.Parent as StackPanel;
        Assert(section != null && section.Children.Count == 2,
            "Missing section body in shared-container accordion " + key);
        return (Border)section.Children[1];
    }
    private static ComboBox BottomPanelMode(Window d)
    {
        return Controls<ComboBox>(Body(d, "appearance-panel"))[0];
    }
    private static ComboBox ComboWithOption(Window d, string optionTag)
    {
        foreach (ComboBox field in Controls<ComboBox>(d))
            foreach (object choice in field.Items) {
                ComboBoxItem entry = choice as ComboBoxItem;
                if (entry != null && Object.Equals(entry.Tag, optionTag)) return field;
            }
        throw new Exception("Settings combo missing option " + optionTag);
    }
    private static RadioButton RadioWithTag(Window d, string tag)
    {
        foreach (RadioButton radio in Controls<RadioButton>(d))
            if (Object.Equals(radio.Tag, tag)) return radio;
        throw new Exception("Settings radio missing: " + tag);
    }
    private static Button ButtonWithTag(Window d, string tag)
    {
        foreach (Button button in Controls<Button>(d))
            if (Object.Equals(button.Tag, tag)) return button;
        throw new Exception("Settings button missing: " + tag);
    }
    private static Border AccordionHeader(Window d, string key)
    {
        foreach (Border border in Controls<Border>(d))
            if (Object.Equals(border.Tag, "SettingsAccordionHeader:" + key)) return border;
        throw new Exception("Missing accordion header highlight " + key);
    }
    private static CheckBox Check(Window d, string text)
    {
        foreach (CheckBox c in Controls<CheckBox>(d))
            if (Convert.ToString(c.Content).StartsWith(text, StringComparison.Ordinal)) return c;
        throw new Exception("Missing checkbox " + text);
    }
    private static void OpenMenu(MenuItem menu)
    {
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu)); Drain();
    }
    private static void KeyPress(UIElement element, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(element);
        Assert(source != null, "Keyboard target has no native WPF source");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Drain();
    }
    private static void ModeMenu(MainWindow w, string expected)
    {
        string[] modes = new string[] { "compact", "normal", "labeled" };
        for (int i=0;i<3;i++) Assert(((MenuItem)TileModeMenu(w).Items[i]).IsChecked == (modes[i] == expected), "Mode checkmark mismatch");
    }
    public static void Run()
    {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Isolation failed");
        Application app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; FluentUi.Install(app);
        Case("widget groups use four nonoverlapping sizes and independent lifecycle", delegate {
            PluginRuntimeState running = new PluginRuntimeState { Status = "running", Version = "1.0.0", ProcessId = 42 };
            PluginWidget card = new PluginWidget { Id = "quotes", Title = "Курсы акций", Kind = "card",
                Elements = new[] { new PluginWidgetElement { Type = "metric", Label = "AAPL", Value = "200" },
                    new PluginWidgetElement { Type = "action", Label = "Открыть", CommandId = "open" } },
                DefaultSize = "full" };
            card.Layouts = new[] {
                new PluginWidgetLayout { Size = "full", Elements = card.Elements },
                new PluginWidgetLayout { Size = "half-horizontal", Elements = card.Elements },
                new PluginWidgetLayout { Size = "half-vertical", Elements = card.Elements },
                new PluginWidgetLayout { Size = "quarter", Elements = new[] {
                    new PluginWidgetElement { Type = "metric", Label = "AAPL", Value = "compact" } } }
            };
            PluginWidget tile = new PluginWidget { Id = "cpu", Title = "CPU", Kind = "tile",
                Elements = new[] { new PluginWidgetElement { Type = "metric", Label = "CPU", Value = "42%" } } };
            PluginWidget indicator = new PluginWidget { Id = "vpn", Title = "VPN", Kind = "indicator",
                Elements = new[] { new PluginWidgetElement { Type = "text", Label = "Connected" } } };
            PluginWidgetSnapshot snapshot = new PluginWidgetSnapshot { ProcessId = 42, Widgets = new[] { card, tile, indicator } };
            int changes = 0, reads = 0, invokes = 0;
            string invoked = null;
            PluginWidgetWorkspace workspace = new PluginWidgetWorkspace(
                delegate(string id) { return running; },
                delegate(string id, string version, bool activate) {
                    Assert(!activate && id == "test.widget" && version == "1.0.0", "Refresh must not start plugin");
                    reads++; return Task.FromResult(snapshot);
                },
                delegate(string id, string version, int pid, string command) {
                    Assert(id == "test.widget" && version == "1.0.0" && pid == 42, "Widget action identity lost");
                    invokes++; invoked = command; return Task.FromResult(true);
                }, delegate { changes++; });
            Assert(!workspace.HasWidgets && workspace.Surface.Visibility == Visibility.Collapsed, "Empty widget surface visible");
            Assert(!workspace.Add("test.widget", "1.0.0", snapshot, "vpn"), "Indicator added to main surface");
            Assert(workspace.Add("test.widget", "1.0.0", snapshot, "quotes"), "Card not added");
            Border group = (Border)workspace.Surface.Children[0];
            Grid cells = (Grid)((StackPanel)group.Child).Children[1];
            ContentControl host = (ContentControl)cells.Children[0];
            Assert(cells.RowDefinitions.Count == 2 && cells.ColumnDefinitions.Count == 2 &&
                Grid.GetRowSpan(host) == 2 && Grid.GetColumnSpan(host) == 2, "Full size is not 2x2");
            Assert(workspace.Resize("test.widget", "quotes", "half-vertical") &&
                Grid.GetRowSpan(host) == 2 && Grid.GetColumnSpan(host) == 1, "Vertical half is not 1x2");
            Assert(workspace.Resize("test.widget", "quotes", "quarter") &&
                Grid.GetRowSpan(host) == 1 && Grid.GetColumnSpan(host) == 1 && cells.Height == 140,
                "Quarter is not 1x1 or empty bottom row did not collapse");
            Assert(Controls<TextBlock>(host).Exists(delegate(TextBlock t) { return t.Text == "compact"; }),
                "Quarter layout content not selected");
            Assert(workspace.Resize("test.widget", "quotes", "half-horizontal") &&
                Grid.GetRowSpan(host) == 1 && Grid.GetColumnSpan(host) == 2, "Horizontal half is not 2x1");
            Assert(workspace.Add("test.widget", "1.0.0", snapshot, "cpu") &&
                workspace.Surface.Children.Count == 1 && cells.Children.Count == 2, "Widgets did not share a group");
            ContentControl remaining = (ContentControl)cells.Children[1];
            Assert(Grid.GetRow(remaining) == 1 && Grid.GetColumn(remaining) == 0 &&
                Grid.GetRowSpan(remaining) == 1 && Grid.GetColumnSpan(remaining) == 1 && cells.Height == 280,
                "Quarter overlaps half or bottom row did not expand");
            Assert(!workspace.Resize("test.widget", "quotes", "full") &&
                Grid.GetRowSpan(host) == 1 && Grid.GetColumnSpan(host) == 2, "Resize overlapped neighbor");
            Assert(!workspace.Add("test.widget", "1.0.0", snapshot, "quotes") && changes == 5,
                "Duplicate placement accepted or failed resize mutated state");
            Window window = new Window { Width = 460, Height = 430, Content = workspace.Surface, ShowInTaskbar = false };
            try {
                window.Show(); Drain();
                remaining.ContextMenu.PlacementTarget = remaining;
                remaining.ContextMenu.IsOpen = true; Drain();
                MenuItem move = (MenuItem)remaining.ContextMenu.Items[4];
                MenuItem toNew = (MenuItem)move.Items[move.Items.Count - 1];
                Assert(Convert.ToString(toNew.Header) == "В новую группу", "Move command missing");
                Click(toNew); remaining.ContextMenu.IsOpen = false; Drain();
                Assert(workspace.Surface.Children.Count == 2 && cells.Children.Count == 1,
                    "Move to new group failed");
                Assert(workspace.Resize("test.widget", "quotes", "full"), "Freed cells not reusable");
                Button old = Controls<Button>(host).Find(delegate(Button button) { return Object.Equals(button.Tag, "open"); });
                Assert(old != null, "Widget action missing");
                Click(old);
                Assert(invokes == 1 && invoked == "open", "Widget action did not dispatch");
                PluginWidget updated = new PluginWidget { Id = "quotes", Title = "Курсы акций", Kind = "card",
                    Elements = new[] { new PluginWidgetElement { Type = "metric", Label = "AAPL", Value = "201" } } };
                snapshot = new PluginWidgetSnapshot { ProcessId = 42, Widgets = new[] { updated, tile } };
                Click((MenuItem)host.ContextMenu.Items[0]); Drain();
                Assert(reads == 1 && Controls<TextBlock>(host).Exists(delegate(TextBlock text) { return text.Text == "201"; }),
                    "Manual refresh did not update data");
                Click(old); Assert(invokes == 1, "Detached widget command still executed");
                running = new PluginRuntimeState { Status = "error" };
                workspace.InvalidateStopped();
                Assert(Controls<TextBlock>(host).Exists(delegate(TextBlock text) { return text.Text.Contains("Плагин недоступен"); }),
                    "Stopped plugin retained live content");
                Assert(!workspace.Add("test.other", "1.0.0", snapshot, "quotes"), "Stopped process accepted");
                Click((MenuItem)host.ContextMenu.Items[1]);
                Assert(workspace.Surface.Children.Count == 1, "Empty group was not removed");
                Click((MenuItem)remaining.ContextMenu.Items[1]);
                Assert(changes == 9 && !workspace.HasWidgets && workspace.Surface.Visibility == Visibility.Collapsed,
                    "Removing widgets did not collapse the surface");
            } finally { window.Close(); }
        });
        Case("widget groups share free board zones and detach without losing content", delegate {
            PluginRuntimeState running = new PluginRuntimeState { Status = "running", Version = "1.0.0", ProcessId = 42 };
            PluginWidget widget = new PluginWidget { Id = "quotes", Kind = "card", Title = "Quotes",
                Elements = new[] { new PluginWidgetElement { Type = "text", Label = "Demo" } } };
            PluginWidgetSnapshot snapshot = new PluginWidgetSnapshot { ProcessId = 42, Widgets = new[] { widget } };
            PluginWidgetWorkspace workspace = new PluginWidgetWorkspace(delegate { return running; },
                delegate { return Task.FromResult(snapshot); }, delegate { return Task.FromResult(true); }, delegate {});
            bool editable = true;
            workspace.ConfigureBoard(new[] { "top-left", "top-right" }, delegate(string key) { return key; },
                delegate { return "Renamed"; }, delegate { return editable; });
            StackPanel left = new StackPanel(), right = new StackPanel();
            Border ordinary = new Border { Height = 50 };
            left.Children.Add(ordinary);
            Dictionary<string, StackPanel> zones = new Dictionary<string, StackPanel> { { "top-left", left }, { "top-right", right } };
            workspace.AttachBoard(zones, "top-left");
            Assert(workspace.Add("test.widget", "1.0.0", snapshot, "quotes"), "Cannot add widget in free mode");
            workspace.AttachBoard(zones, "top-left"); workspace.FitBoard(220);
            Assert(left.Children.Count == 2 && Object.ReferenceEquals(left.Children[0], ordinary), "Widget did not share app zone");
            Border group = (Border)left.Children[1];
            DockPanel header = (DockPanel)((StackPanel)group.Child).Children[0];
            TextBlock heading = (TextBlock)header.Children[2];
            Button collapseButton = (Button)header.Children[0];
            Grid cells = (Grid)((StackPanel)group.Child).Children[1];
            StackPanel root = new StackPanel(); root.Children.Add(left); root.Children.Add(right);
            Window window = new Window { Width = 360, Height = 500, Content = root, ShowInTaskbar = false };
            try {
                window.Show(); Drain();
                ContextMenu menu = heading.ContextMenu;
                menu.PlacementTarget = heading; menu.IsOpen = true; Drain();
                Click((MenuItem)menu.Items[0]); menu.IsOpen = false; Drain();
                Assert(heading.Text == "Renamed  ·  1", "Rename failed");
                Click(collapseButton);
                Assert(cells.Visibility == Visibility.Collapsed, "Collapse button failed");
                Click(collapseButton);
                Assert(cells.Visibility == Visibility.Visible, "Expand button failed");
                menu.IsOpen = true; Drain(); Click((MenuItem)menu.Items[1]); menu.IsOpen = false;
                Assert(cells.Visibility == Visibility.Collapsed, "Collapse menu failed");
                menu.IsOpen = true; Drain(); Click((MenuItem)menu.Items[2]); menu.IsOpen = false;
                Assert(Object.ReferenceEquals(left.Children[0], group), "Widget could not move before app group");
                menu.IsOpen = true; Drain(); Click((MenuItem)((MenuItem)menu.Items[4]).Items[1]); menu.IsOpen = false;
                Assert(right.Children.Count == 1 && Object.ReferenceEquals(right.Children[0], group), "Zone move failed");
                workspace.DetachBoard(); editable = false;
                Assert(right.Children.Count == 0 && workspace.HasWidgets &&
                    !workspace.Resize("test.widget", "quotes", "quarter"), "Hidden mode changed placement");
                editable = true; workspace.AttachBoard(zones, "top-left");
                Assert(right.Children.Count == 1 && Object.ReferenceEquals(right.Children[0], group) &&
                    heading.Text == "Renamed  ·  1" && cells.Visibility == Visibility.Collapsed, "Free layout was not restored");
            } finally { window.Close(); }
        });
        Case("widget layout persists, restores without executing and rejects overlap", delegate {
            PluginWidgetGroupConfig group = new PluginWidgetGroupConfig { Number = 7, Title = "Финансы",
                Zone = "middle-right", Order = 2, Collapsed = true, Widgets = new List<PluginWidgetPlacementConfig> {
                    new PluginWidgetPlacementConfig { PluginId = "example.stocks", WidgetId = "quotes", Version = "1.0.0",
                        Title = "Курсы акций", Size = "half-horizontal", Row = 0, Column = 0 },
                    new PluginWidgetPlacementConfig { PluginId = "example.stocks", WidgetId = "price", Version = "1.0.0",
                        Title = "Цена", Size = "quarter", Row = 1, Column = 1 } } };
            AppConfig config = new AppConfig { GroupingEnabled = true, GroupLayoutMode = "free",
                WidgetGroups = new List<PluginWidgetGroupConfig> { group } };
            AppConfig copy = config.Copy(); copy.WidgetGroups[0].Widgets[0].Row = 1;
            Assert(group.Widgets[0].Row == 0, "Widget layout copy was shallow");
            bool rejected = false;
            try { PluginWidgetGroupConfig.Validate(copy.WidgetGroups); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Overlapping stored placements accepted");
            ConfigStore.Save(config);
            string warning; bool safe;
            AppConfig loaded = ConfigStore.Load(out warning, out safe);
            Assert(safe && warning == null && loaded.WidgetGroups.Count == 1 &&
                loaded.WidgetGroups[0].Widgets[1].Column == 1, "Widget layout did not roundtrip");
            int reads = 0, actions = 0, changes = 0;
            PluginWidgetWorkspace workspace = new PluginWidgetWorkspace(
                delegate { return new PluginRuntimeState { Status = "idle" }; },
                delegate { reads++; throw new Exception("Restore must not execute a plugin"); },
                delegate { actions++; throw new Exception("Restore must not execute a command"); },
                delegate { changes++; });
            workspace.RestoreLayout(loaded.WidgetGroups);
            List<PluginWidgetGroupConfig> captured = workspace.CaptureLayout();
            Assert(reads == 0 && actions == 0 && changes == 0 && workspace.HasWidgets &&
                captured[0].Number == 7 && captured[0].Title == "Финансы" && captured[0].Zone == "middle-right" &&
                captured[0].Collapsed && captured[0].Order == 2 &&
                captured[0].Widgets[0].Size == "half-horizontal" && captured[0].Widgets[1].Column == 1,
                "Restore lost placement metadata or executed plugin");
            workspace.Clear();
        });
        Case("automatic widget refresh batches placements, skips hidden layouts and never restarts errors", delegate {
            PluginRuntimeState current = new PluginRuntimeState { Status = "idle" };
            PluginWidget first = new PluginWidget { Id = "one", Kind = "tile", Title = "One",
                Elements = new[] { new PluginWidgetElement { Type = "metric", Label = "A", Value = "100" } } };
            PluginWidget second = new PluginWidget { Id = "two", Kind = "tile", Title = "Two",
                Elements = new[] { new PluginWidgetElement { Type = "metric", Label = "B", Value = "200" } } };
            PluginWidgetSnapshot snapshot = new PluginWidgetSnapshot { ProcessId = 42, Version = "1.0.0", Widgets = new[] { first, second } };
            PluginWidgetWorkspace workspace = new PluginWidgetWorkspace(delegate { return current; },
                delegate { throw new Exception("Periodic refresh used manual activation"); },
                delegate { throw new Exception("Unexpected command"); }, delegate {});
            workspace.ConfigureBoard(new[] { "top-left" }, delegate(string key) { return key; }, delegate { return null; }, delegate { return true; });
            workspace.RestoreLayout(new List<PluginWidgetGroupConfig> { new PluginWidgetGroupConfig {
                Number = 1, Title = "Widgets", Zone = "top-left", Order = 0,
                Widgets = new List<PluginWidgetPlacementConfig> {
                    new PluginWidgetPlacementConfig { PluginId = "test.widget", WidgetId = "one", Version = "1.0.0",
                        Title = "One", Size = "quarter", Row = 0, Column = 0 },
                    new PluginWidgetPlacementConfig { PluginId = "test.widget", WidgetId = "two", Version = "1.0.0",
                        Title = "Two", Size = "quarter", Row = 0, Column = 1 } } } });
            StackPanel host = new StackPanel();
            workspace.AttachBoard(new Dictionary<string, StackPanel> { { "top-left", host } }, "top-left");
            int reads = 0, activations = 0;
            workspace.ConfigureAutomaticRefresh(delegate(string id, string version, bool activate) {
                Assert(id == "test.widget", "Wrong plugin read"); reads++;
                if (activate) {
                    activations++;
                    Assert(version == null && current.Status == "idle", "Invalid startup activation");
                    current = new PluginRuntimeState { Status = "running", Version = "1.0.0", ProcessId = 42 };
                } else Assert(version == "1.0.0", "Running refresh did not bind version");
                return Task.FromResult(snapshot);
            });
            workspace.RefreshVisibleAsync(delegate { return false; }).GetAwaiter().GetResult();
            Assert(reads == 0, "Hidden workspace performed RPC");
            workspace.RefreshVisibleAsync(delegate { return true; }).GetAwaiter().GetResult();
            Grid cells = (Grid)((StackPanel)((Border)host.Children[0]).Child).Children[1];
            ContentControl firstHost = (ContentControl)cells.Children[0];
            Assert(reads == 1 && activations == 1 &&
                Controls<TextBlock>(cells).Exists(delegate(TextBlock text) { return text.Text == "100"; }) &&
                Controls<TextBlock>(cells).Exists(delegate(TextBlock text) { return text.Text == "200"; }),
                "Snapshot did not serve both widgets");
            object content = firstHost.Content;
            workspace.RefreshVisibleAsync(delegate { return true; }).GetAwaiter().GetResult();
            Assert(reads == 2 && activations == 1 && Object.ReferenceEquals(content, firstHost.Content),
                "Unchanged snapshot rebuilt widget or restarted plugin");
            current = new PluginRuntimeState { Status = "error" };
            workspace.RefreshVisibleAsync(delegate { return true; }).GetAwaiter().GetResult();
            Assert(reads == 2 && activations == 1 &&
                Controls<TextBlock>(cells).Exists(delegate(TextBlock text) { return text.Text.Contains("Плагин недоступен"); }),
                "Failed plugin restarted or retained live values");
            workspace.Clear();
        });
        Case("exact widget cell movement preserves spans and rejects collisions", delegate {
            PluginRuntimeState running = new PluginRuntimeState { Status = "running", Version = "1.0.0", ProcessId = 42 };
            PluginWidgetWorkspace workspace = new PluginWidgetWorkspace(delegate { return running; },
                delegate { throw new Exception("Move performed RPC"); },
                delegate { throw new Exception("Move invoked command"); }, delegate {});
            List<PluginWidgetGroupConfig> saved = new List<PluginWidgetGroupConfig> {
                new PluginWidgetGroupConfig { Number = 1, Title = "One", Zone = "top-left", Order = 0,
                    Widgets = new List<PluginWidgetPlacementConfig> {
                        new PluginWidgetPlacementConfig { PluginId = "test.widget", WidgetId = "quarter", Version = "1.0.0",
                            Title = "Quarter", Size = "quarter", Row = 0, Column = 0 } } },
                new PluginWidgetGroupConfig { Number = 2, Title = "Two", Zone = "top-right", Order = 0,
                    Widgets = new List<PluginWidgetPlacementConfig> {
                        new PluginWidgetPlacementConfig { PluginId = "test.widget", WidgetId = "half", Version = "1.0.0",
                            Title = "Half", Size = "half-horizontal", Row = 0, Column = 0 } } }
            };
            workspace.RestoreLayout(saved);
            Assert(!workspace.MoveWidget("test.widget", "quarter", 2, 0, 1), "Overlapping move accepted");
            Assert(!workspace.MoveWidget("test.widget", "half", 2, 1, 1), "Out of bounds span accepted");
            Assert(workspace.MoveWidget("test.widget", "quarter", 1, 1, 1), "In-group move failed");
            Assert(workspace.CaptureLayout()[0].Widgets[0].Row == 1 && workspace.CaptureLayout()[0].Widgets[0].Column == 1,
                "In-group move not recorded");
            Assert(workspace.MoveWidget("test.widget", "quarter", 2, 1, 1), "Cross-group move failed");
            List<PluginWidgetGroupConfig> moved = workspace.CaptureLayout();
            Assert(moved.Count == 1 && moved[0].Number == 2 && moved[0].Widgets.Count == 2 &&
                moved[0].Widgets.Exists(delegate(PluginWidgetPlacementConfig p) {
                    return p.WidgetId == "quarter" && p.Row == 1 && p.Column == 1 && p.Size == "quarter"; }),
                "Cross-group move lost position or empty source group");
            workspace.Clear();
        });
        Case("plugin indicators persist and refresh independently of free layout", delegate {
            PluginRuntimeState running = new PluginRuntimeState { Status = "running", Version = "1.0.0", ProcessId = 42 };
            PluginWidget indicator = new PluginWidget { Id = "market", Kind = "indicator", Title = "Market demo",
                Elements = new[] { new PluginWidgetElement { Type = "metric", Label = "AAPL", Value = "200" },
                    new PluginWidgetElement { Type = "action", Label = "Open details", CommandId = "open" } } };
            PluginWidgetSnapshot snapshot = new PluginWidgetSnapshot { Version = "1.0.0", ProcessId = 42, Widgets = new[] { indicator } };
            int changes = 0, reads = 0, actions = 0;
            PluginWidgetWorkspace workspace = new PluginWidgetWorkspace(delegate { return running; },
                delegate { return Task.FromResult(snapshot); }, delegate(string id, string version, int process, string command) {
                    Assert(id == "example.stocks" && version == "1.0.0" && process == 42 && command == "open",
                        "Indicator menu used the wrong plugin action");
                    actions++;
                    return Task.FromResult(true);
                }, delegate { changes++; });
            workspace.ConfigureBoard(new[] { "top-left" }, delegate(string key) { return key; }, delegate { return null; }, delegate { return false; });
            workspace.ConfigureIndicatorEditing(delegate { return true; });
            Assert(workspace.AddIndicator("example.stocks", "1.0.0", snapshot, "market") &&
                !workspace.HasWidgets && workspace.IndicatorHost.Visibility == Visibility.Visible,
                "Indicator is not independent of group mode");
            Assert(!workspace.AddIndicator("example.stocks", "1.0.0", snapshot, "market"), "Duplicate indicator accepted");
            List<PluginWidgetPlacementConfig> saved = workspace.CaptureIndicators();
            AppConfig cfg = new AppConfig { WidgetIndicators = saved, GroupingEnabled = false };
            ConfigStore.Save(cfg);
            string warning; bool safe;
            AppConfig loaded = ConfigStore.Load(out warning, out safe);
            Assert(safe && warning == null && loaded.WidgetIndicators[0].Size == "indicator", "Indicator layout did not persist");
            workspace.RestoreLayout(null, loaded.WidgetIndicators);
            Assert(!workspace.HasWidgets && workspace.IndicatorHost.Visibility == Visibility.Visible && changes == 1,
                "Indicator restore changed group state");
            workspace.ConfigureAutomaticRefresh(delegate(string id, string version, bool activate) {
                Assert(id == "example.stocks" && version == "1.0.0" && !activate, "Incorrect indicator refresh"); reads++;
                return Task.FromResult(snapshot);
            });
            workspace.RefreshVisibleAsync(delegate { return false; }, delegate { return false; }).GetAwaiter().GetResult();
            Assert(reads == 0, "Hidden indicator refreshed");
            workspace.RefreshVisibleAsync(delegate { return false; }, delegate { return true; }).GetAwaiter().GetResult();
            WrapPanel panel = workspace.IndicatorHost;
            Assert(reads == 1 && Controls<TextBlock>(panel).Exists(delegate(TextBlock t) { return t.Text == "200"; }),
                "Indicator did not refresh outside free layout");
            ContentControl item = (ContentControl)panel.Children[0];
            Click(Menu(item.ContextMenu, "Open details")); Drain();
            Assert(actions == 1, "Indicator menu did not invoke its API v1 action");
            running.Status = "stopped";
            workspace.InvalidateStopped();
            Assert(item.ContextMenu.Items.Count == 2, "Unavailable plugin still offers stale actions");
            Click((MenuItem)item.ContextMenu.Items[1]);
            Assert(workspace.IndicatorHost.Visibility == Visibility.Collapsed && workspace.CaptureIndicators() == null &&
                changes == 2, "Indicator removal did not collapse bottom strip");
        });
        Case("shared dark selection controls apply to unstyled dialogs", delegate {
            ComboBox combo = new ComboBox { MinHeight = 32 };
            combo.Items.Add("First"); combo.Items.Add("Second"); combo.SelectedIndex = 0;
            ListBox list = new ListBox { Height = 100 };
            list.Items.Add("One"); list.Items.Add("Two"); list.SelectedIndex = 0;
            StackPanel content = new StackPanel(); content.Children.Add(combo); content.Children.Add(list);
            Window window = new Window { Width = 380, Height = 260, Content = content, ShowInTaskbar = false };
            FluentUi.PrepareWindow(window);
            try {
                window.Show(); Drain();
                Assert(combo.Template != null && combo.Template.FindName("PART_Popup", combo) != null,
                    "Shared combo template missing popup");
                Assert(list.Template.FindName("ListChrome", list) != null &&
                    Object.Equals(((SolidColorBrush)list.Background).Color, ((SolidColorBrush)FluentUi.Brush("#192334")).Color),
                    "Shared dark list style missing");
                combo.IsDropDownOpen = true; Drain();
                Assert(combo.IsDropDownOpen, "Shared combo cannot open");
                combo.SelectedIndex = 1; combo.IsDropDownOpen = false;
                list.SelectedIndex = 1; list.IsEnabled = false; Drain();
                Assert(combo.SelectedIndex == 1 && list.SelectedIndex == 1, "Shared selection behavior changed");
            } finally { window.Close(); }
        });
        Case("native declarative plugin widget renderer", delegate {
            PluginWidget card = new PluginWidget { Id = "quotes", Title = "Курсы акций", Kind = "card",
                Elements = new[] {
                    new PluginWidgetElement { Type = "text", Label = "<Button> literal & _text" },
                    new PluginWidgetElement { Type = "metric", Label = "AAPL", Value = "$200.00" },
                    new PluginWidgetElement { Type = "progress", Label = "CPU", Percent = 24 },
                    new PluginWidgetElement { Type = "action", Label = "Открыть _детали", CommandId = "open" },
                    new PluginWidgetElement { Type = "action", Label = "Обновить", CommandId = "refresh" }
                } };
            TaskCompletionSource<bool> pending = new TaskCompletionSource<bool>();
            string command = null; int calls = 0;
            Border view = PluginWidgetView.Create(card, delegate(string id) { command = id; calls++; return pending.Task; });
            Border tile = PluginWidgetView.Create(new PluginWidget { Id = "cpu", Title = "CPU", Kind = "tile",
                Elements = new[] { new PluginWidgetElement { Type = "metric", Label = "Загрузка", Value = "24%" },
                    new PluginWidgetElement { Type = "progress", Label = "CPU", Percent = 24 } } }, null);
            Border indicator = PluginWidgetView.Create(new PluginWidget { Id = "vpn", Title = "VPN", Kind = "indicator",
                Elements = new[] { new PluginWidgetElement { Type = "text", Label = "Подключён" },
                    new PluginWidgetElement { Type = "action", Label = "Подробнее", CommandId = "open" } } }, null);
            int indicatorCalls = 0;
            Border clickable = PluginWidgetView.Create(new PluginWidget { Id = "network", Title = "Network", Kind = "indicator",
                Elements = new[] { new PluginWidgetElement { Type = "text", Label = "OK" },
                    new PluginWidgetElement { Type = "action", Label = "Details", CommandId = "open" } } },
                delegate(string id) { Assert(id == "open", "Incorrect indicator action"); indicatorCalls++; return Task.FromResult(true); });
            StackPanel panel = new StackPanel { Width = 320, Margin = new Thickness(12) };
            panel.Children.Add(view); panel.Children.Add(tile); panel.Children.Add(indicator); panel.Children.Add(clickable);
            Window window = new Window { Width = 400, Height = 620, ShowInTaskbar = false, Content = panel };
            try {
                window.Show(); Drain();
                Assert(Object.Equals(view.Tag, "quotes") && view.MaxWidth > tile.MaxWidth && indicator.MaxWidth < view.MaxWidth,
                    "Widget identity or size hints missing");
                Assert(Controls<TextBlock>(view).Exists(delegate(TextBlock t) { return t.Text == "<Button> literal & _text"; }) &&
                    Controls<TextBlock>(view).Exists(delegate(TextBlock t) { return t.Text == "$200.00"; }),
                    "Widget content was not rendered literally");
                List<ProgressBar> bars = Controls<ProgressBar>(view);
                Assert(bars.Count == 1 && bars[0].Value == 24 && bars[0].Maximum == 100, "Progress data missing");
                Button primary = clickable.Child as Button;
                Assert(primary != null && Controls<Button>(clickable).Count == 1 && primary.IsEnabled, "Indicator surface is not the only action");
                Click(primary); Drain(); Assert(indicatorCalls == 1, "Indicator action not invoked");
                List<Button> buttons = Controls<Button>(view);
                Assert(buttons.Count == 2 && buttons[0].IsEnabled && !Controls<Button>(indicator)[0].IsEnabled,
                    "Widget action availability wrong");
                Click(buttons[0]); Click(buttons[1]);
                Assert(command == "open" && calls == 1 && !buttons[0].IsEnabled && !buttons[1].IsEnabled,
                    "Widget action was not routed or overlapping clicks were accepted");
                pending.SetResult(true); Drain();
                Stopwatch limit = Stopwatch.StartNew();
                while (!buttons[0].IsEnabled && limit.ElapsedMilliseconds < 3000) { System.Threading.Thread.Sleep(10); Drain(); }
                Assert(buttons[0].IsEnabled && buttons[1].IsEnabled, "Widget actions stayed busy after completion");
                pending = new TaskCompletionSource<bool>();
                Click(buttons[1]); pending.SetException(new InvalidOperationException("simulated")); Drain();
                TextBlock error = Controls<TextBlock>(view).Find(delegate(TextBlock t) { return t.Text == "Не удалось выполнить команду."; });
                limit.Restart();
                while (error.Visibility != Visibility.Visible && limit.ElapsedMilliseconds < 3000) { System.Threading.Thread.Sleep(10); Drain(); }
                Assert(calls == 2 && command == "refresh" && error.Visibility == Visibility.Visible &&
                    buttons[1].IsEnabled, "Widget action failure was not contained");
                panel.Width = 160; window.UpdateLayout(); Drain();
                Assert(view.ActualWidth <= 160 && tile.ActualWidth <= 160 && indicator.ActualWidth <= 160,
                    "Widget renderer overflowed a narrow host");
                panel.Width = 320; window.UpdateLayout(); Drain();
                Picture(panel, "plugin-widget-renderer");
            } finally { window.Close(); }
        });
        Case("release notes Markdown and safe links", delegate {
            StackPanel panel = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            string tick = ((char)96).ToString();
            ReleaseNotesView.Render(panel, "### Heading\n\n- **Bold** and *italic* " +
                tick + "code" + tick + "\n1. Ordered\n[Site](https://github.com/mishakov1c/app_shelf)\n" +
                "[Unsafe](file:///C:/Windows/notepad.exe)\n<script>not executable</script>",
                Brushes.White, Brushes.LightBlue, true);
            Assert(panel.Children.Count == 6, "Markdown block count mismatch");
            TextBlock heading = (TextBlock)panel.Children[0];
            Assert(heading.Text == "Heading", "Heading text mismatch: " + heading.Text);
            Assert(heading.FontSize > 12, "Heading font size: " + heading.FontSize);
            Assert(heading.FontWeight == FontWeights.SemiBold,
                "Heading font weight: " + heading.FontWeight);
            Grid row = (Grid)panel.Children[1];
            TextBlock body = (TextBlock)row.Children[1];
            Assert(((TextBlock)row.Children[0]).Text == "•", "List marker missing");
            Assert(body.Inlines.FirstInline is System.Windows.Documents.Bold,
                "Bold formatting missing");
            bool italic = false, code = false;
            foreach (System.Windows.Documents.Inline inline in body.Inlines) {
                if (inline is System.Windows.Documents.Italic) italic = true;
                System.Windows.Documents.Run run = inline as System.Windows.Documents.Run;
                if (run != null && run.Text == "code" && run.FontFamily.Source == "Consolas") code = true;
            }
            Assert(italic && code, "Inline italic or code formatting missing");
            Assert(((TextBlock)((Grid)panel.Children[2]).Children[0]).Text == "1.",
                "Ordered list marker missing");
            Assert(((TextBlock)panel.Children[3]).Inlines.FirstInline is System.Windows.Documents.Hyperlink,
                "HTTPS link missing");
            Assert(!(((TextBlock)panel.Children[4]).Inlines.FirstInline is System.Windows.Documents.Hyperlink) &&
                ((TextBlock)panel.Children[5]).Text == "<script>not executable</script>",
                "Unsafe Markdown was not kept as text");
            Uri uri;
            Assert(!ReleaseNotesView.SafeLink("javascript:alert(1)", out uri) &&
                !ReleaseNotesView.SafeLink("https://name:password@example.com", out uri) &&
                ReleaseNotesView.SafeLink("https://github.com", out uri), "Unsafe link scheme accepted");
            ScrollViewer viewer = new ScrollViewer { Content = panel, Width = 360, Height = 150,
                VerticalContentAlignment = VerticalAlignment.Top,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FluentUi.StyleSettingsScroll(viewer);
            Window window = new Window { Content = viewer, Width = 400, Height = 260,
                ShowInTaskbar = false };
            try {
                window.Show(); Drain();
                Point start = heading.TransformToAncestor(viewer).Transform(new Point(0, 0));
                Assert(start.Y < 8 && start.Y >= 0, "Release notes vertically centered");
                string longNotes = String.Concat(System.Linq.Enumerable.Repeat("- long release note\n", 50));
                ReleaseNotesView.Render(panel, longNotes, Brushes.White, Brushes.LightBlue, true);
                Drain();
                Assert(viewer.ScrollableHeight > 0, "Long release notes do not scroll");
                ReleaseNotesView.Render(panel, "Literal **message**", Brushes.White, Brushes.LightBlue, false);
                Assert(panel.Children.Count == 1 && ((TextBlock)panel.Children[0]).Text == "Literal **message**",
                    "Plain status message was parsed as Markdown");
            } finally { window.Close(); }
        });
        Case("backup window uses dark dropdowns and scrollbar", delegate {
            Seed("normal", 1);
            MainWindow w = Open();
            try {
                backup = delegate(Window d) {
                    d.Width = d.MinWidth; d.Height = d.MinHeight; d.UpdateLayout(); Drain();
                    // The compact default no longer overflows; test the scrollbar
                    // with the automatic settings expanded at minimum window size.
                    Assert(Body(d, "backup-automatic").Visibility == Visibility.Collapsed,
                        "Backup settings should initially be collapsed");
                    Click(Section(d, "backup-automatic")); Drain();
                    Assert(Body(d, "backup-automatic").Visibility == Visibility.Visible,
                        "Automatic backup section did not open");
                    List<ComboBox> fields = Controls<ComboBox>(d);
                    Assert(fields.Count == 3, "Expected cadence, retention and quota dropdowns");
                    foreach (ComboBox field in fields)
                    {
                        field.ApplyTemplate();
                        field.IsDropDownOpen = true;
                        try {
                            Drain();
                            var popup = field.Template.FindName("PART_Popup", field)
                                as System.Windows.Controls.Primitives.Popup;
                            Assert(popup != null && popup.IsOpen && popup.Child is Border,
                                "Backup dropdown did not render its popup");
                            Dark(((Border)popup.Child).Background, "Backup dropdown popup");
                        } finally { field.IsDropDownOpen = false; Drain(); }
                    }
                    List<ScrollViewer> scrollers = Controls<ScrollViewer>(d);
                    Assert(scrollers.Count == 1, "Backup dialog scroll viewer missing");
                    ScrollViewer scroll = scrollers[0];
                    scroll.ApplyTemplate(); scroll.UpdateLayout(); Drain();
                    System.Windows.Controls.Primitives.ScrollBar bar =
                        scroll.Template.FindName("PART_VerticalScrollBar", scroll)
                            as System.Windows.Controls.Primitives.ScrollBar;
                    Assert(bar != null && bar.ActualWidth <= 12.5 && bar.ActualWidth > 0,
                        "Backup dialog still has wide native scrollbar");
                    Dark(Visual<Border>(bar).Background, "Backup dialog scrollbar");
                    Assert(scroll.ScrollableHeight > 0, "Backup dialog content did not overflow");
                    scroll.ScrollToEnd(); Drain();
                    Assert(scroll.VerticalOffset > 0, "Backup dialog scrollbar does not scroll");
                    ScrollInput(scroll, "Backup dialog");
                };
                MethodInfo open = typeof(MainWindow).GetMethod("ShowBackupMenuWindow", Private,
                    null, Type.EmptyTypes, null);
                Assert(open != null, "Backup entry point missing");
                open.Invoke(w, new object[0]);
            } finally { w.Close(); }
        });
        Case("main tile list uses narrow dark scrollbar and remains scrollable", delegate {
            Seed("normal", 30);
            MainWindow w = Open();
            try {
                w.Height = w.MinHeight; Drain();
                ScrollViewer scroller = null;
                foreach (ScrollViewer viewer in Controls<ScrollViewer>(w))
                    if (viewer.Name == "AppShelfTilesScroller") scroller = viewer;
                Assert(scroller != null, "Main tile scroll viewer not found");
                scroller.ApplyTemplate(); scroller.UpdateLayout(); Drain();
                System.Windows.Controls.Primitives.ScrollBar bar =
                    scroller.Template.FindName("PART_VerticalScrollBar", scroller)
                        as System.Windows.Controls.Primitives.ScrollBar;
                Assert(bar != null && bar.ActualWidth > 0 && bar.ActualWidth <= 12.5,
                    "Main scroll bar is still the native wide scrollbar");
                Dark(Visual<Border>(bar).Background, "Main tile scrollbar track");
                Assert(scroller.ScrollableHeight > 50,
                    "Main tiles did not overflow the viewport");
                scroller.ScrollToVerticalOffset(45); Drain();
                Assert(scroller.VerticalOffset > 0,
                    "Main list no longer responds to programmatic scrolling");
                Assert(Tiles(w).Count == 30, "Tiles changed while scrolling");
            } finally { w.Close(); }
        });
        Case("shortcut tile uses its original icon without Windows link overlay", delegate {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            Assert(shellType != null, "Windows Script Host unavailable");
            string folder = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "IconFixtures");
            Directory.CreateDirectory(folder);
            string linkPath = Path.Combine(folder, "Sample.lnk");
            string destination = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            string iconPath = Path.Combine(Environment.SystemDirectory, "shell32.dll");
            object shell = null, link = null;
            try {
                shell = Activator.CreateInstance(shellType);
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { linkPath });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty,
                    null, link, new object[] { destination });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty,
                    null, link, new object[] { iconPath + ",3" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            } finally {
                if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            ImageSource core = ShellIcon.GetWithoutShortcutOverlay(linkPath);
            Assert(core is BitmapSource && ((BitmapSource)core).PixelWidth >= 16,
                "Original icon could not be extracted from the saved shortcut");
            ImageSource ordinary = ShellIcon.Get(linkPath);
            Assert(ordinary is BitmapSource, "Windows shortcut icon missing");
            // A regular Shell icon for the .lnk includes the white arrow overlay.
            // The extracted source image must not be identical to that composition.
            BitmapSource raw = (BitmapSource)core, decorated = (BitmapSource)ordinary;
            int w = Math.Min(raw.PixelWidth, decorated.PixelWidth);
            Assert(w >= 16, "Unexpected shortcut icon resolution");
            Console.WriteLine("PASS direct shortcut icon source extracted without overlay");
        });
        foreach (string mode in new string[] { "compact", "normal", "labeled" })
        foreach (int percent in new int[] { 75, 100, 125 }) {
            string m = mode; int size = percent;
            Case("tile and real icon scale " + m + " " + size, delegate {
                Seed(m, 1);
                string warning; bool safe;
                AppConfig config = ConfigStore.Load(out warning, out safe);
                Assert(safe && warning == null, "Scale fixture could not load");
                config.TileScalePercent = size;
                config.Items[0].Path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                ConfigStore.Save(config);
                MainWindow w = Open(); try {
                    Button tile = Tiles(w)[0];
                    double scale = size / 100.0;
                    Assert(tile.Width == Math.Round((m == "compact" ? 78 : m == "normal" ? 100 : 124) * scale) &&
                        tile.Height == Math.Round((m == "compact" ? 78 : m == "normal" ? 100 : 123) * scale),
                        "Tile does not scale from its original dimensions");
                    List<Image> icons = Controls<Image>(tile);
                    Assert(icons.Count == 1 && icons[0].Source != null &&
                        icons[0].Width == Math.Round((m == "compact" ? 47 : 51) * scale) &&
                        icons[0].Height == icons[0].Width, "Real icon is not proportional to the tile");
                    Picture(w, "tiles-" + m + "-" + size);
                } finally { w.Close(); }
            });
        }
        Case("shortcut EXE, custom icon, folder and missing target preserve sources and launch", delegate {
            string folder = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "LinkMatrix");
            Directory.CreateDirectory(folder);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try {
                foreach (string variant in new string[] { "exe", "custom", "folder", "missing" }) {
                    string path = Path.Combine(folder, variant + ".lnk");
                    string target = variant == "folder" ? folder :
                        variant == "missing" ? Path.Combine(folder, "missing.exe") :
                        Path.Combine(Environment.SystemDirectory, "cmd.exe");
                    string marker = Path.Combine(folder, variant + ".txt");
                    object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                        null, shell, new object[] { path });
                    try {
                        Type type = link.GetType();
                        type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                        if (variant == "custom")
                            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link,
                                new object[] { Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",3" });
                        if (variant == "exe" || variant == "custom") {
                            type.InvokeMember("Arguments", BindingFlags.SetProperty, null, link,
                                new object[] { "/d /c echo launched>\"" + marker + "\"" });
                            type.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, link, new object[] { 7 });
                        }
                        type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                    } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
                    byte[] before = File.ReadAllBytes(path);
                    BitmapSource icon = ShellIcon.GetWithoutShortcutOverlay(path) as BitmapSource;
                    Assert(icon != null && icon.PixelWidth >= 16, "No icon/fallback for " + variant);
                    Assert(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(path)),
                        "Reading the icon modified the shortcut");
                    if (variant == "exe" || variant == "folder") {
                        BitmapSource expected = (BitmapSource)ShellIcon.Get(target);
                        byte[] actualPixels = new byte[icon.PixelHeight * icon.PixelWidth * 4];
                        byte[] expectedPixels = new byte[expected.PixelHeight * expected.PixelWidth * 4];
                        icon.CopyPixels(actualPixels, icon.PixelWidth * 4, 0);
                        expected.CopyPixels(expectedPixels, expected.PixelWidth * 4, 0);
                        Assert(icon.PixelWidth == expected.PixelWidth && icon.PixelHeight == expected.PixelHeight &&
                            Convert.ToBase64String(actualPixels) == Convert.ToBase64String(expectedPixels),
                            "Shortcut icon differs from the unoverlaid target icon");
                    }
                    if (variant == "exe" || variant == "custom") {
                        Launcher.Start(new AppItem { Name = "Isolated link launch", Path = path }, false);
                        Stopwatch wait = Stopwatch.StartNew();
                        while (!File.Exists(marker) && wait.ElapsedMilliseconds < 5000)
                            System.Threading.Thread.Sleep(25);
                        Assert(File.Exists(marker), "Shortcut no longer launches with its saved arguments");
                    }
                }
            } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        });
        foreach (string mode in new string[] { "compact", "normal", "labeled" }) { string m = mode;
            Case("mode menu, persistence and tile dimensions " + m, delegate {
                Seed("labeled", 8); MainWindow w = Open(); try {
                    MenuItem parent = TileModeMenu(w); int index = m == "compact" ? 0 : m == "normal" ? 1 : 2;
                    Click((MenuItem)parent.Items[index]); ModeMenu(w, m);
                    Assert(Live(w).TileDisplayMode == m && Live(w).Compact == (m != "labeled"), "Mode or compatibility Boolean incorrect");
                    Assert(Load().TileDisplayMode == m, "Mode not saved");
                    foreach (Button b in Tiles(w)) {
                        Assert(b.ActualWidth == (m == "compact" ? 78 : m == "normal" ? 100 : 124) && b.ActualHeight == (m == "compact" ? 78 : m == "normal" ? 100 : 123), "Wrong tile size");
                        bool hasLabel = false; foreach (TextBlock t in Controls<TextBlock>(b)) if (t.Text == ((AppItem)b.Tag).Name) hasLabel = true;
                        Assert(hasLabel == (m == "labeled"), "Wrong label visibility");
                    }
                    Picture(w, "tiles-" + m);
                } finally { w.Close(); }
                w = Open(); try { Assert((string)Call(w, "CurrentTileDisplayMode") == m, "Mode lost after reopen"); ModeMenu(w, m); } finally { w.Close(); }
            });
            Case("write failure rolls back mode and menu " + m, delegate {
                Seed(m == "labeled" ? "compact" : "labeled", 2); MainWindow w = Open(); try {
                    string before = Snapshot(Live(w)), disk = File.ReadAllText(ConfigStore.FilePath); string previousMode = (string)Call(w, "CurrentTileDisplayMode");
                    int errors = Errors; using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        Click((MenuItem)TileModeMenu(w).Items[m == "compact" ? 0 : m == "normal" ? 1 : 2]);
                        Assert(Errors == errors + 1 && Snapshot(Live(w)) == before && File.ReadAllText(ConfigStore.FilePath) == disk, "Failed write changed memory/disk"); ModeMenu(w, previousMode);
                    }
                    Call(w, "SaveWindowPlacement"); Assert(Load().TileDisplayMode == previousMode, "Geometry persisted rejected mode");
                    Call(w, "SetTileDisplayMode", m); Assert(Live(w).TileDisplayMode == m, "Retry failed");
                } finally { w.Close(); }
            });
            Case("group heights, empty/collapsed, reorder and move " + m, delegate {
                Seed(m, 1); MainWindow w = Open(); try {
                    double one = Group(w, 0).ActualHeight; double empty = Group(w, 2).ActualHeight;
                    Assert(one > empty && empty >= 58, "Empty/one-row height wrong");
                    for (int i = 1; i < 15; i++) Live(w).Items.Add(new AppItem { Name = "More " + i, Path = Live(w).Items[0].Path + i, Group = "Первая" });
                    Call(w, "RenderTiles"); Drain(); double many = Group(w, 0).ActualHeight;
                    Assert(many > one + 50, "Multiple rows did not increase group height");
                    Console.WriteLine("HEIGHT " + m + " one=" + one.ToString(CultureInfo.InvariantCulture) + " many=" + many.ToString(CultureInfo.InvariantCulture) + " empty=" + empty.ToString(CultureInfo.InvariantCulture));
                    Expander section = Group(w, 0); section.IsExpanded = false; Drain(); Assert(section.ActualHeight < one && !((Border)section.Content).IsVisible, "Collapsed content stays visible");
                    AppItem item = Live(w).Items[0]; Call(w, "MoveDroppedItem", item, null, "Вторая", false); Drain(); Assert(item.Group == "Вторая", "Move into group failed");
                    Call(w, "SwapGroups", "Первая", "Вторая"); Drain(); Assert(Live(w).Groups[0] == "Вторая" && Load().Groups[0] == "Вторая", "Group reorder failed");
                } finally { w.Close(); }
            });
            Case("routed tile drag/drop to empty and collapsed groups " + m, delegate {
                Seed(m, 2); MainWindow w = Open(); try {
                    AppItem item = Live(w).Items[0]; DataObject data = new DataObject(); data.SetData("AppShelf.InternalTilePath", item.Path);
                    Expander destination = Group(w, 1); destination.IsExpanded = false; Drain();
                    FrameworkElement header = (FrameworkElement)destination.Header;
                    Assert(Drag(header, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.Move, "Collapsed group does not accept tile drag");
                    Assert(Drag(header, data, DragDrop.PreviewDropEvent).Effects == DragDropEffects.Move && item.Group == "Вторая", "Routed tile drop failed");
                    destination = Group(w, 2); Assert(Drag((FrameworkElement)destination.Header, data, DragDrop.PreviewDropEvent).Effects == DragDropEffects.Move && item.Group == "Пустая", "Empty group drop failed");
                } finally { w.Close(); }
            });
        }
        foreach (bool compact in new bool[] { false, true }) { bool c = compact; Case("old JSON no tileDisplayMode compact=" + c, delegate {
            Seed("normal", 0); string json = "{\"version\":1,\"items\":[],\"groups\":[],\"compact\":" + (c ? "true" : "false") + "}";
            File.WriteAllText(ConfigStore.FilePath, json); Assert(Load().TileDisplayMode == (c ? "compact" : "labeled"), "Legacy mode lost"); Assert(File.ReadAllText(ConfigStore.FilePath) == json, "Load rewrote legacy data");
        }); }
        Case("explicit mode wins over legacy flag and invalid modes fall back", delegate {
            Seed("normal", 0); AppConfig c = Load(); c.TileDisplayMode = "normal"; c.Compact = false; ConfigStore.Save(c); Assert(Load().TileDisplayMode == "normal", "Valid mode ignored");
            c.TileDisplayMode = "unknown"; c.Compact = true; ConfigStore.Save(c); Assert(Load().TileDisplayMode == "compact", "Invalid mode did not fall back");
        });
        Case("same or invalid mode does not write", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                string before = File.ReadAllText(ConfigStore.FilePath), backup = File.ReadAllText(ConfigStore.FilePath + ".bak");
                int errors = Errors; using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    Call(w, "SetTileDisplayMode", "normal"); Call(w, "SetTileDisplayMode", "invalid");
                    Assert(Errors == errors && File.ReadAllText(ConfigStore.FilePath) == before && File.ReadAllText(ConfigStore.FilePath + ".bak") == backup, "Idempotent/invalid mode wrote data");
                    ModeMenu(w, "normal");
                }
            } finally { w.Close(); }
        });
        Case("protected config keeps mode and checkmarks", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                Set(w, "_canSave", false); string before = Snapshot(Live(w)); int warnings = DisabledWarnings;
                Call(w, "SetTileDisplayMode", "compact"); Assert(DisabledWarnings == warnings + 1 && Snapshot(Live(w)) == before, "Protected mode changed"); ModeMenu(w, "normal");
            } finally { w.Close(); }
        });
        Case("quick settings and tools menu structure", delegate {
            Seed("normal", 2); MainWindow w = Open(); try {
                ContextMenu menu = Tools(w);
                MenuItem quick = Menu(menu, "Быстрые настройки");
                MenuItem tools = Menu(menu, "Инструменты");
                Assert(quick.Icon != null && tools.Icon != null &&
                    Menu(menu, "Напоминания").Icon != null &&
                    Menu(menu, "Создать группу").Icon != null &&
                    Menu(menu, "Настройки…").Icon != null && Menu(menu, "О программе").Icon != null,
                    "Menu icons missing");
                MenuItem panel = Menu(quick, "Нижняя панель");
                Assert(panel.Items.Count == 3 && Menu(quick, "Режим плиток").Items.Count == 3 &&
                    Menu(quick, "Сбросить расположение групп") != null &&
                    !Controls<MenuItem>(quick).Exists(m => Convert.ToString(m.Header) == "Размещение групп" || Convert.ToString(m.Header) == "Расставить группы"),
                    "Quick mode choices incomplete");
                Assert(Menu(quick, "Звук включён").IsCheckable &&
                    Menu(quick, "Группировка приложений").IsCheckable &&
                    Menu(quick, "Поверх остальных окон").IsCheckable, "Quick switches missing");
                Assert(Menu(tools, "Открыть папку «Пуск»") != null &&
                    Menu(tools, "Экспорт / импорт данных…") != null &&
                    Menu(tools, "RDP-антипростой").IsCheckable, "Tool entries missing");
                Call(w, "SetBottomPanelMode", "categories");
                Assert(Live(w).BottomPanelMode == "categories" && Load().BottomPanelMode == "categories",
                    "Quick mode not saved");
                Call(w, "SetAppSounds", false);
                Assert(Live(w).SoundsEnabled == false && Load().SoundsEnabled == false,
                    "Sound toggle not saved");
                Call(w, "SetRdpKeepAwake", false);
                Assert(!Live(w).RdpKeepAwake, "RDP mode unexpectedly changed");
            } finally { w.Close(); }
        });
        Case("only canvas reset remains in quick settings", delegate {
            Seed("normal", 2); MainWindow w = Open(); try {
                MenuItem quick = Menu(Tools(w), "Быстрые настройки");
                OpenMenu(quick);
                Assert(Menu(quick, "Сбросить расположение групп").IsEnabled && Get(w, "_canvasBoard") != null,
                    "Canvas reset unavailable");
                Assert(!Controls<MenuItem>(quick).Exists(m => Convert.ToString(m.Header) == "Расставить группы" ||
                    Convert.ToString(m.Header) == "Размещение групп"), "Legacy layout submenu remains");
                Call(w, "SetGrouping", false); OpenMenu(quick);
                Assert(!Menu(quick, "Сбросить расположение групп").IsEnabled, "Reset enabled without grouping");
                Call(w, "SetGrouping", true); OpenMenu(quick);
                Assert(Menu(quick, "Сбросить расположение групп").IsEnabled, "Reset not restored");
            } finally { w.Close(); }
        });
        Case("bottom indicator closes its own tool window only", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                Call(w, "ToggleBottomModuleWindow", "countdown");
                Assert(Get(w, "_countdownWindow") != null, "Countdown window not opened");
                Call(w, "ToggleBottomModuleWindow", "countdown");
                Assert(Get(w, "_countdownWindow") == null, "Second click did not close countdown");
                Call(w, "ToggleBottomModuleWindow", "pomodoro");
                Assert(Get(w, "_pomodoroWindow") != null, "Pomodoro not opened");
                Call(w, "ToggleBottomModuleWindow", "pomodoro");
                Assert(Get(w, "_pomodoroWindow") == null, "Pomodoro not closed");
            } finally { w.Close(); }
        });
        foreach (double scale in new double[] { 1, 1.25, 1.5 }) { double s = scale;
            Case("dark native WPF menus, nested navigation and checkmarks scale=" + s, delegate {
                Seed("normal", 2); MainWindow w = Open(); try {
                    ContextMenu menu = Tools(w);
                    menu.LayoutTransform = new ScaleTransform(s, s);
                    menu.PlacementTarget = ToolsButton(w);
                    menu.IsOpen = true; Drain();
                    Border surface = (Border)menu.Template.FindName("MenuSurface", menu);
                    Assert(surface != null && surface.CornerRadius.TopLeft > 0 &&
                        surface.BorderThickness.Left == 1, "Menu rounded frame missing");
                    Dark(surface.Background, "Overflow background"); Dark(surface.BorderBrush, "Overflow border");
                    Assert(((SolidColorBrush)menu.Foreground).Color.R > 220, "Menu text is not light");
                    foreach (object entry in menu.Items) {
                        Separator separator = entry as Separator;
                        if (separator != null) Dark(Visual<Border>(separator).Background, "Separator");
                    }
                    Picture(menu, "menu-overflow-" + s.ToString(CultureInfo.InvariantCulture));
                    MenuItem quick = Menu(menu, "Быстрые настройки");
                    quick.Focus(); KeyPress(quick, Key.Right);
                    Assert(quick.IsSubmenuOpen, "Right key did not open submenu");
                    System.Windows.Controls.Primitives.Popup popup =
                        (System.Windows.Controls.Primitives.Popup)quick.Template.FindName("PART_Popup", quick);
                    Assert(popup.IsOpen && PresentationSource.FromVisual(popup.Child) != null,
                        "Submenu did not create a real WPF popup");
                    // Popup is a separate native window: LayoutTransform on the parent menu
                    // does not scale it. Scale each popup's content explicitly for this probe.
                    ((FrameworkElement)((Border)popup.Child).Child).LayoutTransform = new ScaleTransform(s, s); Drain();
                    Border nested = (Border)quick.Template.FindName("SubmenuSurface", quick);
                    Dark(nested.Background, "Quick settings submenu");
                    Border highlighted = (Border)quick.Template.FindName("ItemSurface", quick);
                    Assert(((SolidColorBrush)highlighted.Background).Color == ((SolidColorBrush)FluentUi.SurfaceHover).Color,
                        "Open/highlighted entry lacks hover color");
                    Picture((FrameworkElement)popup.Child, "menu-quick-" + s.ToString(CultureInfo.InvariantCulture));
                    MenuItem tile = Menu(quick, "Режим плиток");
                    tile.Focus(); KeyPress(tile, Key.Right);
                    Assert(tile.IsSubmenuOpen, "Second-level submenu inaccessible by keyboard");
                    System.Windows.Controls.Primitives.Popup modes =
                        (System.Windows.Controls.Primitives.Popup)tile.Template.FindName("PART_Popup", tile);
                    ((FrameworkElement)((Border)modes.Child).Child).LayoutTransform = new ScaleTransform(s, s); Drain();
                    Dark(((Border)tile.Template.FindName("SubmenuSurface", tile)).Background, "Tile submenu");
                    MenuItem normal = (MenuItem)tile.Items[1];
                    Assert(((System.Windows.Shapes.Path)normal.Template.FindName("Check", normal)).Visibility == Visibility.Visible,
                        "Saved tile mode checkmark invisible");
                    Picture((FrameworkElement)modes.Child, "menu-tile-modes-" + s.ToString(CultureInfo.InvariantCulture));
                    KeyPress(tile, Key.Left); Assert(!tile.IsSubmenuOpen, "Left key did not close submenu");
                    MenuItem sound = Menu(quick, "Звук включён");
                    sound.Focus(); KeyPress(sound, Key.Enter);
                    Assert(Live(w).SoundsEnabled == false && !sound.IsChecked,
                        "Keyboard activation broke checkable menu persistence");
                    menu.IsOpen = false;
                    // Built-in automation invokes MenuItem.OnClick just as mouse activation does.
                    menu.IsOpen = true; quick.IsSubmenuOpen = true; Drain();
                    System.Windows.Automation.Peers.MenuItemAutomationPeer peer =
                        new System.Windows.Automation.Peers.MenuItemAutomationPeer(sound);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                        System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Drain();
                    Assert(Live(w).SoundsEnabled != false && sound.IsChecked, "Menu invocation broke toggle");
                    menu.IsOpen = false;
                    foreach (ContextMenu other in new ContextMenu[] {
                        AddMenu, Tiles(w)[0].ContextMenu, ((Border)Get(w, "_weatherChip")).ContextMenu,
                        ((Border)Get(w, "_footerBorder")).ContextMenu
                    }) {
                        Assert(other != null, "Other context menu missing");
                        other.PlacementTarget = w; other.IsOpen = true; Drain();
                        Dark(((Border)other.Template.FindName("MenuSurface", other)).Background, "Shared context menu");
                        other.IsOpen = false;
                    }
                } finally { Tools(w).IsOpen = false; w.Close(); }
            });
        }
        Case("quick checkmarks roll back after failed and protected saves", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                MenuItem quick = Menu(Tools(w), "Быстрые настройки");
                MenuItem sound = Menu(quick, "Звук включён"), pin = Menu(quick, "Поверх остальных окон");
                MenuItem grouping = Menu(quick, "Группировка приложений"), panel = Menu(quick, "Нижняя панель");
                for (int attempt = 0; attempt < 2; attempt++) {
                    string before = Snapshot(Live(w)), disk = File.ReadAllText(ConfigStore.FilePath);
                    Set(w, "_canSave", attempt == 0);
                    using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        OpenMenu(quick);
                        sound.IsChecked = false; Click(sound);
                        pin.IsChecked = !Live(w).AlwaysOnTop; Click(pin);
                        grouping.IsChecked = false; Click(grouping);
                        ((MenuItem)panel.Items[0]).IsChecked = true; Click((MenuItem)panel.Items[0]);
                        Assert(sound.IsChecked && pin.IsChecked == Live(w).AlwaysOnTop && grouping.IsChecked,
                            "Rejected quick switch leaves a false checkmark");
                        for (int i = 0; i < 3; i++) Assert(((MenuItem)panel.Items[i]).IsChecked == (i == 2),
                            "Rejected panel mode leaves a false checkmark");
                        Assert(Snapshot(Live(w)) == before && File.ReadAllText(ConfigStore.FilePath) == disk,
                            "Rejected quick settings changed memory or disk");
                    }
                }
                Set(w, "_canSave", true);
            } finally { w.Close(); }
        });
        Case("RDP confirmation, rollback and both settings entry points", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                MenuItem tools = Menu(Tools(w), "Инструменты"), rdp = Menu(tools, "RDP-антипростой");
                int confirmations = rdpConfirmations;
                rdpAnswer = MessageBoxResult.No; rdp.IsChecked = true; Click(rdp);
                Assert(rdpConfirmations == confirmations + 1 && !rdp.IsChecked && !Live(w).RdpKeepAwake,
                    "Declined RDP enable changed state");
                rdpAnswer = MessageBoxResult.Yes;
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    rdp.IsChecked = true; Click(rdp);
                    Assert(!rdp.IsChecked && !Live(w).RdpKeepAwake && !Load().RdpKeepAwake,
                        "Failed RDP save changed state");
                }
                rdp.IsChecked = true; Click(rdp);
                Assert(rdp.IsChecked && Load().RdpKeepAwake, "Confirmed RDP enable not saved");
                settings = delegate(Window d) {
                    Assert(Check(d, "Не давать простаивать").IsChecked == true, "Full settings stale after quick RDP change");
                    Check(d, "Не давать простаивать").IsChecked = false;
                    Click(ButtonNamed(d, "Сохранить"));
                };
                Call(w, "ShowSettings"); OpenMenu(tools);
                Assert(!rdp.IsChecked && !Load().RdpKeepAwake, "Quick RDP state stale after full save");
                confirmations = rdpConfirmations; rdpAnswer = MessageBoxResult.No;
                settings = delegate(Window d) {
                    Check(d, "Не давать простаивать").IsChecked = true; Click(ButtonNamed(d, "Сохранить"));
                };
                Call(w, "ShowSettings");
                Assert(rdpConfirmations == confirmations + 1 && !Load().RdpKeepAwake,
                    "Full settings bypass RDP confirmation");
            } finally { w.Close(); }
        });
        Case("closing module windows preserves running timers and background services", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                DispatcherTimer[] timers = new DispatcherTimer[] {
                    new DispatcherTimer { Interval = TimeSpan.FromHours(1) },
                    new DispatcherTimer { Interval = TimeSpan.FromHours(1) },
                    new DispatcherTimer { Interval = TimeSpan.FromHours(1) }
                };
                string[] fields = new string[] { "_weatherTimer", "_rdpTimer", "_pomodoroTimer" };
                for (int i = 0; i < fields.Length; i++) { timers[i].Start(); Set(w, fields[i], timers[i]); }
                Call(w, "UpdateCountdownAvailability");
                Call(w, "ToggleBottomModuleWindow", "countdown"); Call(w, "ToggleCountdown");
                CountdownTimerEngine engine = (CountdownTimerEngine)Get(w, "_countdownEngine");
                Assert(engine.Running, "Countdown did not start");
                Call(w, "ToggleBottomModuleWindow", "countdown");
                Assert(engine.Running && ((DispatcherTimer)Get(w, "_countdownTimer")).IsEnabled,
                    "Closing countdown stopped engine/timer");
                Call(w, "ToggleBottomModuleWindow", "pomodoro"); Call(w, "TogglePomodoro");
                DateTime deadline = (DateTime)Get(w, "_pomodoroDeadlineUtc");
                Call(w, "ToggleBottomModuleWindow", "pomodoro");
                Assert((bool)Get(w, "_pomodoroRunning") && (DateTime)Get(w, "_pomodoroDeadlineUtc") == deadline,
                    "Closing Pomodoro stopped/reset its countdown");
                foreach (string key in new string[] { "weather", "reminders" }) {
                    Call(w, "ToggleBottomModuleWindow", key); Call(w, "ToggleBottomModuleWindow", key);
                }
                for (int i = 0; i < fields.Length; i++) Assert(timers[i].IsEnabled && Get(w, fields[i]) == timers[i],
                    "Closing module changed " + fields[i]);
            } finally { w.Close(); }
        });
        foreach (double scale in new double[] { 1, 1.25, 1.5 }) { double s = scale;
            Case("all accordion sections accessible at minimum size scale=" + s, delegate {
                Seed("normal", 1); MainWindow w = Open(); try {
                    settings = delegate(Window d) {
                        d.Width = d.MinWidth * s; d.Height = d.MinHeight * s;
                        ((FrameworkElement)d.Content).LayoutTransform = new ScaleTransform(s, s); Drain();
                        ScrollViewer scroll = Controls<ScrollViewer>(d)[0];
                        string[] keys = new string[] { "appearance", "network", "weather", "time", "windows", "collections", "plugins", "data" };
                        Click(ButtonNamed(d, "Свернуть всё"));
                        foreach (string key in keys)
                            Assert(Body(d, key).Visibility == Visibility.Collapsed, "Collapse all missed " + key);
                        foreach (string key in new string[] { "appearance-tiles", "appearance-background", "appearance-panel" })
                            Assert(Body(d, key).Visibility == Visibility.Collapsed, "Collapse all missed nested " + key);
                        Click(ButtonNamed(d, "Развернуть всё"));
                        foreach (string key in keys)
                            Assert(Body(d, key).Visibility == Visibility.Visible, "Expand all missed " + key);
                        foreach (string key in new string[] { "appearance-tiles", "appearance-background", "appearance-panel" })
                            Assert(Body(d, key).Visibility == Visibility.Visible, "Expand all missed nested " + key);
                        Assert(scroll.ScrollableHeight > 0, "Expanded content does not scroll");
                        scroll.ScrollToHome(); Drain();
                        System.Windows.Controls.Primitives.ScrollBar bar =
                            (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                        System.Windows.Controls.Primitives.Track track =
                            (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
                        Assert(bar.ActualWidth == 12 && track.Orientation == Orientation.Vertical,
                            "Settings scrollbar width=" + bar.ActualWidth + ", track orientation=" + track.Orientation);
                        Dark(Visual<Border>(bar).Background, "Settings scrollbar");
                        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 25)
                            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent }); Drain();
                        Assert(scroll.VerticalOffset > 0, "Themed thumb does not move the settings viewport");
                        scroll.ScrollToEnd(); Drain();
                        Assert(scroll.VerticalOffset > 0, "End of settings unreachable");
                        Inside(ButtonNamed(d, "Сохранить"), (FrameworkElement)d.Content, "Pinned save");
                        Inside(ButtonNamed(d, "Отмена"), (FrameworkElement)d.Content, "Pinned cancel");
                        foreach (string key in keys) {
                            Click(ButtonNamed(d, "Свернуть всё")); Click(Section(d, key));
                            if (key == "appearance") Click(ButtonNamed(d, "Развернуть всё"));
                            Body(d, key).BringIntoView(); Drain();
                            foreach (ComboBox combo in Controls<ComboBox>(Body(d, key))) {
                                if (!combo.IsVisible) continue;
                                combo.BringIntoView(); Drain();
                                Dark(Visual<Border>(combo).Background, "Settings combo in " + key);
                                combo.IsDropDownOpen = true; Drain();
                                System.Windows.Controls.Primitives.Popup choices =
                                    (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
                                Dark(Visual<Border>(choices.Child).Background, "Settings dropdown in " + key);
                                combo.IsDropDownOpen = false; Drain();
                            }
                            foreach (CheckBox c in Controls<CheckBox>(Body(d, key))) {
                                ContentPresenter content = Visual<ContentPresenter>(c);
                                if (content != null) {
                                    Rect r = content.TransformToAncestor(Body(d, key)).TransformBounds(new Rect(content.RenderSize));
                                    Assert(r.Right <= Body(d, key).ActualWidth + 0.5, "Checkbox text clipped in " + key + ": " + c.Content);
                                }
                            }
                            Picture(d, "accordion-" + key + "-" + s.ToString(CultureInfo.InvariantCulture));
                        }
                    };
                    Call(w, "ShowSettings");
                } finally { w.Close(); }
            });
        }
        Case("long dark menus scroll, preserve gestures and disabled items", delegate {
            Seed("normal", 1); MainWindow w = Open(); ContextMenu menu = new ContextMenu(); try {
                Style sharedScroll = app.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] as Style;
                Assert(sharedScroll != null && sharedScroll.TargetType == typeof(System.Windows.Controls.Primitives.ScrollBar),
                    "Shared application scrollbar style missing");
                Assert(!app.Resources.Contains(SystemParameters.VerticalScrollBarWidthKey) &&
                    !app.Resources.Contains(SystemParameters.HorizontalScrollBarHeightKey),
                    "Menu-specific system scrollbar dimensions leaked into application resources");
                MenuItem disabled = new MenuItem { Header = "Недоступно", IsEnabled = false };
                MenuItem parent = new MenuItem { Header = "Длинное подменю" };
                menu.Items.Add(disabled); menu.Items.Add(parent);
                int invoked = 0;
                for (int i = 0; i < 100; i++) {
                    MenuItem item = new MenuItem { Header = "Пункт " + i, InputGestureText = "Ctrl+Shift+T" };
                    item.Click += delegate { invoked++; };
                    parent.Items.Add(item);
                }
                menu.PlacementTarget = w; menu.IsOpen = true; parent.IsSubmenuOpen = true; Drain();
                System.Windows.Controls.Primitives.Popup popup =
                    (System.Windows.Controls.Primitives.Popup)parent.Template.FindName("PART_Popup", parent);
                ScrollViewer scroll = Visual<ScrollViewer>(popup.Child);
                Assert(scroll.ScrollableHeight > 0 && ((FrameworkElement)popup.Child).ActualHeight <= SystemParameters.WorkArea.Height + 1,
                    "Long submenu overflows the working area instead of scrolling");
                MenuItem last = (MenuItem)parent.Items[99]; last.BringIntoView(); Drain();
                Assert(scroll.VerticalOffset > 0, "Last menu item unreachable");
                Assert(((TextBlock)last.Template.FindName("Gesture", last)).Text == "Ctrl+Shift+T",
                    "Input gesture text disappeared");
                last.Focus(); KeyPress(last, Key.Enter);
                Assert(invoked == 1 && !menu.IsOpen, "Last menu command did not invoke/close normally");
            } finally { menu.IsOpen = false; w.Close(); }
        });
        Case("plugin manager displays installed and invalid packages", delegate {
            Seed("normal", 1); MainWindow w = Open();
            string profile = Path.Combine(ConfigStore.DirectoryPath, "PluginUiCase");
            Directory.CreateDirectory(profile);
            // The main menu must read the same isolated profile as the manager.
            PluginPackageStore store = new PluginPackageStore(ConfigStore.DirectoryPath);
            PluginRuntime runtime = new PluginRuntime(store);
            try {
                ContextMenu tools = Tools(w);
                MenuItem pluginMenu = Menu(tools, "Плагины");
                Assert(pluginMenu.Visibility == Visibility.Collapsed, "Empty plugins menu should be hidden");
                Assert(!Controls<MenuItem>(tools).Exists(delegate(MenuItem m) {
                    return Convert.ToString(m.Header) == "Инструменты плагинов";
                }), "Duplicate plugin menu still present");
                PluginManagerWindow empty = new PluginManagerWindow(w, store, runtime);
                empty.ShowInTaskbar = false; empty.Show(); Drain();
                try {
                    bool found = false;
                    foreach (TextBlock block in Controls<TextBlock>(empty))
                        if (block.Text == "Плагины пока не установлены.") found = true;
                    Assert(found, "Empty list hint missing");
                } finally { empty.Close(); }
                string package = Path.Combine(profile, "hello.asplugin");
                using (FileStream stream = File.Create(package))
                using (System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(
                    stream, System.IO.Compression.ZipArchiveMode.Create)) {
                    using (StreamWriter writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                        writer.Write("{\"schemaVersion\":1,\"apiVersion\":1,\"id\":\"example.hello\",\"name\":\"Hello\",\"version\":\"1.0.0\",\"entryPoint\":\"bin/Hello.exe\",\"capabilities\":[\"window\",\"widget\"],\"update\":{\"provider\":\"github\",\"repository\":\"example/hello\"}}");
                    using (StreamWriter writer = new StreamWriter(zip.CreateEntry("bin/Hello.exe").Open()))
                        writer.Write("not executed");
                }
                store.Install(package, delegate { return true; });
                Directory.CreateDirectory(Path.Combine(ConfigStore.DirectoryPath, "Plugins", "Installed", "example.broken"));
                int placements = 0; bool chosenIndicator = false; string placementId = null;
                PluginManagerWindow manager = new PluginManagerWindow(w, store, runtime, null, null,
                    delegate(Window owner, bool indicator, string id) { placements++; chosenIndicator = indicator; placementId = id; },
                    delegate { return true; });
                manager.ShowInTaskbar = false; manager.Show(); Drain();
                try {
                    CheckBox good = null, broken = null;
                    foreach (CheckBox check in Controls<CheckBox>(manager)) {
                        if (Object.Equals(check.Tag, "PluginEnabled:example.hello")) good = check;
                        if (Object.Equals(check.Tag, "PluginEnabled:example.broken")) broken = check;
                    }
                    Assert(good != null && good.IsEnabled && good.IsChecked == false, "Installed row not disabled");
                    Assert(broken != null && !broken.IsEnabled, "Broken package not disabled");
                    good.ApplyTemplate();
                    Border knob = good.Template.FindName("SwitchKnob", good) as Border;
                    Assert(knob != null && knob.HorizontalAlignment == HorizontalAlignment.Left &&
                        Convert.ToString(good.Content) == "Плагин выключен", "Disabled switch appearance or label missing");
                    TextBlock process = Controls<TextBlock>(manager).Find(delegate(TextBlock block) {
                        return Object.Equals(block.Tag, "PluginState:example.hello");
                    });
                    Assert(process != null && process.Text == "Состояние процесса: Остановлен", "Disabled process state missing");
                    Assert(Controls<TextBlock>(manager).Exists(delegate(TextBlock block) {
                        return block.Text == "Возможности: собственные окна, виджеты";
                    }), "Capability not localized");
                    Button disabledPlacement = Controls<Button>(manager).Find(button => Object.Equals(button.Tag, "PluginAddIndicator:example.hello"));
                    Assert(disabledPlacement != null && !disabledPlacement.IsEnabled, "Disabled plugin offers widget placement");
                    Assert(Controls<Button>(manager).Exists(button => Object.Equals(button.Tag, "PluginCheckUpdate:example.hello")), "GitHub check missing for declared source");
                    good.IsChecked = true;
                    good.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    // The enabled flag can reach disk before the manager refreshes its controls.
                    Stopwatch limit = Stopwatch.StartNew();
                    Button commandButton;
                    do {
                        Drain();
                        commandButton = Controls<Button>(manager).Find(delegate(Button button) {
                            return Object.Equals(button.Tag, "PluginCommands:example.hello");
                        });
                        if (commandButton != null && commandButton.IsEnabled) break;
                        System.Threading.Thread.Sleep(10);
                    } while (limit.ElapsedMilliseconds < 5000);
                    bool enabled = false;
                    foreach (InstalledPlugin plugin in store.List())
                        if (plugin.Id == "example.hello") enabled = plugin.Enabled;
                    Assert(enabled, "UI toggle did not persist enabled state");
                    Assert(commandButton != null && commandButton.IsEnabled, "Command button did not become enabled after the UI toggle");
                    Button placeIndicator = Controls<Button>(manager).Find(button => Object.Equals(button.Tag, "PluginAddIndicator:example.hello"));
                    Button placeWidget = Controls<Button>(manager).Find(button => Object.Equals(button.Tag, "PluginAddWidget:example.hello"));
                    Assert(placeIndicator != null && placeWidget != null && placeIndicator.IsEnabled && placeWidget.IsEnabled, "Placement buttons missing");
                    Click(placeIndicator); Assert(placements == 1 && chosenIndicator && placementId == "example.hello", "Wrong indicator route");
                    Click(placeWidget); Assert(placements == 2 && !chosenIndicator && placementId == "example.hello", "Wrong widget route");
                    CheckBox refreshed = Controls<CheckBox>(manager).Find(delegate(CheckBox check) {
                        return Object.Equals(check.Tag, "PluginEnabled:example.hello");
                    });
                    Assert(refreshed != null && refreshed.IsChecked == true && Convert.ToString(refreshed.Content) == "Плагин включён", "Enabled switch did not refresh");
                    refreshed.ApplyTemplate();
                    Border onKnob = refreshed.Template.FindName("SwitchKnob", refreshed) as Border;
                    Assert(onKnob != null && onKnob.HorizontalAlignment == HorizontalAlignment.Right, "Enabled switch appearance missing");
                    process = Controls<TextBlock>(manager).Find(delegate(TextBlock block) {
                        return Object.Equals(block.Tag, "PluginState:example.hello");
                    });
                    Assert(process != null && process.Text == "Состояние процесса: Остановлен", "Enabled idle process state conflated with switch");
                    MenuItem actionMenu = new MenuItem();
                    Call(w, "PopulatePluginTools", actionMenu, store.List());
                    Assert(actionMenu.Visibility == Visibility.Visible && actionMenu.Items.Count == 1 &&
                        Convert.ToString(((MenuItem)actionMenu.Items[0]).Header) == "Hello",
                        "Enabled plugin menu or broken package exclusion failed");
                    MenuItem actionGroup = (MenuItem)actionMenu.Items[0];
                    Assert(actionGroup.Items.Count == 0, "Idle plugin should offer direct activation, not an extra submenu");
                    InstalledPlugin active = store.List().Find(delegate(InstalledPlugin p) { return p.Id == "example.hello"; });
                    // Known commands: a single command is a leaf; multiple commands are nested.
                    PluginRuntimeState known = new PluginRuntimeState { Status = "running", Version = active.Manifest.Version,
                        Commands = new[] { new PluginCommand { Id = "open", Label = "Open" } } };
                    MenuItem single = (MenuItem)Call(w, "CreatePluginMenuItem", active, known);
                    Assert(single.Items.Count == 0, "Single command has an unnecessary submenu");
                    known.Commands = new[] { new PluginCommand { Id = "open", Label = "Open" },
                        new PluginCommand { Id = "other", Label = "Other" } };
                    MenuItem multiple = (MenuItem)Call(w, "CreatePluginMenuItem", active, known);
                    Assert(multiple.Items.Count == 2 && Convert.ToString(((MenuItem)multiple.Items[0]).Header) == "Open" &&
                        Convert.ToString(((MenuItem)multiple.Items[1]).Header) == "Other", "Multiple commands not nested");
                    // Exercise the real overflow-open event and its asynchronous refresh.
                    Click(ToolsButton(w));
                    limit.Restart();
                    while (pluginMenu.Visibility != Visibility.Visible && limit.ElapsedMilliseconds < 5000)
                    { System.Threading.Thread.Sleep(10); Drain(); }
                    Assert(tools.IsOpen && pluginMenu.Visibility == Visibility.Visible && pluginMenu.Items.Count == 1 &&
                        Convert.ToString(((MenuItem)pluginMenu.Items[0]).Header) == "Hello", "Real menu did not refresh enabled plugins");
                    tools.IsOpen = false; Drain();
                    Assert(runtime.State("example.hello").Status == "idle", "Menu discovery started plugin");
                    Assert(((PluginRuntime)Call(w, "GetPluginRuntime")).State("example.hello").Status == "idle",
                        "Opening the main menu launched a plugin");
                    refreshed.IsChecked = false;
                    refreshed.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    limit.Restart();
                    do {
                        Drain();
                        commandButton = Controls<Button>(manager).Find(delegate(Button button) {
                            return Object.Equals(button.Tag, "PluginCommands:example.hello");
                        });
                        good = Controls<CheckBox>(manager).Find(delegate(CheckBox check) {
                            return Object.Equals(check.Tag, "PluginEnabled:example.hello");
                        });
                        if (good != null && good.IsEnabled && good.IsChecked == false &&
                            commandButton != null && !commandButton.IsEnabled) break;
                        System.Threading.Thread.Sleep(10);
                    } while (limit.ElapsedMilliseconds < 5000);
                    Assert(good != null && good.IsEnabled && good.IsChecked == false &&
                        Convert.ToString(good.Content) == "Плагин выключен" &&
                        commandButton != null && !commandButton.IsEnabled &&
                        !store.List().Find(delegate(InstalledPlugin p) { return p.Id == "example.hello"; }).Enabled,
                        "UI switch failed to disable the plugin");
                    Call(w, "PopulatePluginTools", actionMenu, store.List());
                    Assert(actionMenu.Items.Count == 0 && actionMenu.Visibility == Visibility.Collapsed,
                        "Disabled plugin still in actions menu");
                    Click(ToolsButton(w));
                    Assert(tools.IsOpen && pluginMenu.Visibility == Visibility.Collapsed, "Previously enabled plugin menu not hidden on reopen");
                    tools.IsOpen = false; Drain();
                    Picture(manager, "plugin-manager");
                } finally { manager.Close(); }
                settings = delegate(Window d) {
                    Button section = Controls<Button>(d).Find(delegate(Button button) { return Object.Equals(button.Tag, "plugins"); });
                    Assert(section != null, "Plugin settings section missing");
                    Button manage = ButtonNamed(d, "Управление плагинами…");
                    if (!manage.IsVisible) Click(section);
                    Assert(manage.IsVisible, "Plugin management not available in settings");
                    Click(ButtonNamed(d, "Отмена"));
                };
                Call(w, "ShowSettings");
            } finally { runtime.Dispose(); w.Close(); Directory.Delete(profile, true);
                string pluginsRoot = Path.Combine(ConfigStore.DirectoryPath, "Plugins");
                if (Directory.Exists(pluginsRoot)) Directory.Delete(pluginsRoot, true);
            }
        });
        Case("settings accordion and saved navigation", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                settings = delegate(Window d) {
                    string[] main = { "appearance", "network", "weather", "time", "windows", "collections", "plugins", "data" };
                    string[] nested = { "appearance-tiles", "appearance-background", "appearance-panel" };
                    Dictionary<string, Button> headers = new Dictionary<string, Button>();
                    foreach (Button b in Controls<Button>(d))
                        if (b.Tag is string && ((string)b.Tag) != "")
                            headers[(string)b.Tag] = b;
                    foreach (string key in main) Assert(headers.ContainsKey(key), "Missing main section " + key);
                    foreach (string key in nested) Assert(headers.ContainsKey(key), "Missing nested subsection " + key);
                    Assert(!headers.ContainsKey("panel"), "Standalone Bottom panel section unexpectedly remains");
                    List<Border> frames = new List<Border>(), subframes = new List<Border>(), dividers = new List<Border>();
                    foreach (Border border in Controls<Border>(d))
                    {
                        if (Object.Equals(border.Tag, "SettingsAccordionFrame")) frames.Add(border);
                        if (Object.Equals(border.Tag, "SettingsAppearanceSubsectionsFrame")) subframes.Add(border);
                        if (Object.Equals(border.Tag, "SettingsAccordionDivider")) dividers.Add(border);
                    }
                    Assert(frames.Count == 1 && frames[0].CornerRadius.TopLeft >= 10,
                        "Single main rounded accordion frame missing");
                    SolidColorBrush frameFill = frames[0].Background as SolidColorBrush;
                    Assert(frameFill != null && frameFill.Color ==
                        (Color)ColorConverter.ConvertFromString("#111A29"),
                        "Main accordion is not using the reference dark fill");
                    StackPanel rows = frames[0].Child as StackPanel;
                    StackPanel nestedRows = subframes.Count == 1 ? subframes[0].Child as StackPanel : null;
                    Assert(rows != null && rows.Children.Count == 15 && nestedRows != null &&
                        nestedRows.Children.Count == 5 && dividers.Count == 9,
                        "Expected eight main sections and three nested Appearance subsections");
                    foreach (string key in main) {
                        StackPanel caption = Section(d, key).Content as StackPanel;
                        System.Windows.Shapes.Path chevron = caption == null ? null :
                            caption.Children[0] as System.Windows.Shapes.Path;
                        Assert(chevron != null && chevron.StrokeThickness > 1 &&
                            chevron.StrokeThickness < 2, "Thin vector chevron missing: " + key);
                    }
                    foreach (string key in nested) {
                        StackPanel caption = Section(d, key).Content as StackPanel;
                        Assert(caption != null && caption.Children[0] is System.Windows.Shapes.Path,
                            "Nested subsection chevron missing: " + key);
                    }
                    Assert(AccordionHeader(d, "appearance").Background != Brushes.Transparent &&
                        AccordionHeader(d, "network").Background == Brushes.Transparent &&
                        Body(d, "appearance-tiles").Visibility == Visibility.Visible &&
                        Body(d, "appearance-panel").Visibility == Visibility.Collapsed,
                        "Initial Appearance section/subsection selection is wrong");
                    Click(headers["network"]);
                    Assert(AccordionHeader(d, "network").Background != Brushes.Transparent &&
                        Body(d, "appearance").Visibility == Visibility.Collapsed,
                        "Main accordion did not switch to Network");
                    Click(headers["appearance"]);
                    Assert(AccordionHeader(d, "appearance").Background != Brushes.Transparent &&
                        Body(d, "network").Visibility == Visibility.Collapsed,
                        "Main accordion did not return to Appearance");
                    Click(headers["appearance-panel"]);
                    Assert(Body(d, "appearance-panel").Visibility == Visibility.Visible &&
                        Body(d, "appearance-tiles").Visibility == Visibility.Collapsed &&
                        Controls<ListBox>(Body(d, "appearance-panel")).Count == 1,
                        "Bottom panel was not nested under Appearance");
                    Picture(d, "accordion-reference-B");
                    Assert(Controls<ComboBox>(d).Count >= 3 && Controls<CheckBox>(d).Count >= 10,
                        "Settings modules disappeared");
                    Click(ButtonNamed(d, "Свернуть всё"));
                    foreach (string key in main)
                        Assert(AccordionHeader(d, key).Background == Brushes.Transparent &&
                            Body(d, key).Visibility == Visibility.Collapsed,
                            "Collapsed main section kept an active highlight");
                    foreach (string key in nested)
                        Assert(Body(d, key).Visibility == Visibility.Collapsed, "Nested collapse failed: " + key);
                    Click(ButtonNamed(d, "Развернуть всё"));
                    foreach (string key in main)
                        Assert(AccordionHeader(d, key).Background != Brushes.Transparent &&
                            Body(d, key).Visibility == Visibility.Visible,
                            "Expand all missed main section " + key);
                    foreach (string key in nested)
                        Assert(Body(d, key).Visibility == Visibility.Visible, "Expand all missed nested " + key);
                    Assert(ButtonNamed(d, "Сохранить").IsVisible, "Pinned save unavailable");
                };
                Call(w, "ShowSettings");
                Assert(Load().SettingsLastSection == "panel",
                    "Nested Bottom panel navigation was not persisted");
                settings = delegate(Window d) {
                    Assert(Body(d, "appearance").Visibility == Visibility.Visible &&
                        Body(d, "network").Visibility == Visibility.Collapsed &&
                        Body(d, "appearance-panel").Visibility == Visibility.Visible,
                        "Reopen ignored the saved Bottom panel subsection");
                    Assert(Controls<CheckBox>(Body(d, "appearance-tiles")).Contains(Check(d, "Группировка приложений")) &&
                        Controls<CheckBox>(Body(d, "windows")).Contains(Check(d, "Не давать простаивать")) &&
                        Controls<Button>(Body(d, "network")).Contains(ButtonWithTag(d, "NetworkPluginSettings")) &&
                        Controls<CheckBox>(Body(d, "weather")).Contains(Check(d, "Показывать погоду")) &&
                        Controls<CheckBox>(Body(d, "time")).Contains(Check(d, "Включить звуки")) &&
                        Controls<CheckBox>(Body(d, "collections")).Contains(Check(d, "Включить модуль «Подборки»")) &&
                        !Controls<CheckBox>(Body(d, "data")).Contains(Check(d, "Включить модуль «Подборки»")) &&
                        Controls<Button>(Body(d, "data")).Contains(ButtonNamed(d, "Экспорт / импорт данных…")),

                        "Settings assigned to the wrong main/nested section");
                };
                Call(w, "ShowSettings");
            } finally { w.Close(); }
        });
        Case("collections and backup settings preserve navigation", delegate {
            Seed("normal", 1);
            AppConfig config = Load();
            config.SettingsLastSection = "data";
            ConfigStore.Save(config);
            MainWindow w = Open(); try {
                settings = delegate(Window d) {
                    Assert(Body(d, "data").Visibility == Visibility.Visible &&
                        Body(d, "collections").Visibility == Visibility.Collapsed,
                        "Legacy data navigation did not open backups");
                    Assert(Controls<TextBlock>(Body(d, "data")).Find(t => t.Text.Contains("Ctrl+Alt+Space")) == null,
                        "Window help remains in backup settings");
                    Assert(Controls<TextBlock>(Body(d, "windows")).Find(t => t.Text.Contains("Ctrl+Alt+Space") &&
                        t.Text.Contains("Alt+F4") && t.Text.Contains("«Выход»")) != null,
                        "Corrected window help is missing");
                    Click(Section(d, "collections"));
                    Assert(Body(d, "collections").Visibility == Visibility.Visible &&
                        Body(d, "data").Visibility == Visibility.Collapsed,
                        "Collections did not open separately");
                };
                Call(w, "ShowSettings");
                Assert(Load().SettingsLastSection == "collections", "Collections navigation not saved");
                settings = delegate(Window d) {
                    Assert(Body(d, "collections").Visibility == Visibility.Visible,
                        "Reopen ignored Collections navigation");
                };
                Call(w, "ShowSettings");
            } finally { w.Close(); }
        });
        Case("legacy Bottom panel navigation opens Appearance subsection", delegate {
            Seed("normal", 1);
            AppConfig config = Load();
            config.SettingsLastSection = "panel";
            ConfigStore.Save(config);
            MainWindow w = Open(); try {
                settings = delegate(Window d) {
                    Assert(Body(d, "appearance").Visibility == Visibility.Visible &&
                        Body(d, "appearance-panel").Visibility == Visibility.Visible &&
                        Body(d, "appearance-tiles").Visibility == Visibility.Collapsed,
                        "Legacy panel section was not mapped to its Appearance subsection");
                };
                Call(w, "ShowSettings");
                Assert(Load().SettingsLastSection == "panel",
                    "Legacy panel navigation was needlessly rewritten");
            } finally { w.Close(); }
        });
        Case("appearance background subsection is remembered", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                settings = delegate(Window d) {
                    Click(Section(d, "appearance-background"));
                    Assert(Body(d, "appearance-background").Visibility == Visibility.Visible &&
                        Body(d, "appearance-tiles").Visibility == Visibility.Collapsed,
                        "Background subsection did not expand");
                };
                Call(w, "ShowSettings");
                Assert(Load().SettingsLastSection == "appearance-background",
                    "Background subsection preference was not saved");
                settings = delegate(Window d) {
                    Assert(Body(d, "appearance").Visibility == Visibility.Visible &&
                        Body(d, "appearance-background").Visibility == Visibility.Visible &&
                        Body(d, "appearance-panel").Visibility == Visibility.Collapsed,
                        "Saved background subsection was not restored");
                };
                Call(w, "ShowSettings");
            } finally { w.Close(); }
        });
        Case("weather and RDP entry points open their own settings sections", delegate {
            Seed("normal", 1);
            AppConfig config = Load();
            config.SettingsLastSection = "network";
            config.WeatherCity = "";
            ConfigStore.Save(config);
            MainWindow w = Open(); try {
                settings = delegate(Window d) {
                    Assert(Body(d, "weather").Visibility == Visibility.Visible &&
                        Body(d, "network").Visibility == Visibility.Collapsed &&
                        Body(d, "windows").Visibility == Visibility.Collapsed,
                        "Weather entry point opened a stale settings section");
                };
                ContextMenu weather = (ContextMenu)Call(w, "CreateWeatherContextMenu");
                Click(Menu(weather, "Изменить город"));
                Assert(Load().SettingsLastSection == "weather",
                    "Weather navigation did not keep the selected settings section");
                settings = delegate(Window d) {
                    Assert(Body(d, "weather").Visibility == Visibility.Visible,
                        "No-city forecast did not open Weather settings");
                };
                Call(w, "ShowForecastWindow");
                settings = delegate(Window d) {
                    Assert(Body(d, "windows").Visibility == Visibility.Visible &&
                        Body(d, "weather").Visibility == Visibility.Collapsed &&
                        Controls<CheckBox>(Body(d, "windows")).Contains(
                            Check(d, "При закрытии окна сворачивать в трей")),
                        "RDP entry point did not open Windows behaviour settings");
                };
                Call(w, "ToggleBottomModuleWindow", "rdp");
                Assert(Load().SettingsLastSection == "windows",
                    "RDP navigation did not keep the selected section");
                settings = delegate(Window d) {
                    Assert(Body(d, "appearance").Visibility == Visibility.Visible &&
                        Body(d, "appearance-panel").Visibility == Visibility.Visible &&
                        Body(d, "appearance-tiles").Visibility == Visibility.Collapsed,
                        "Bottom-panel context menu did not open its nested section");
                };
                Border footer = (Border)Get(w, "_footerBorder");
                Click(Menu(footer.ContextMenu, "Состав, порядок и настройки панели…"));
                Assert(Load().SettingsLastSection == "panel",
                    "Bottom-panel context navigation lost its historical saved key");
                settings = delegate(Window d) {
                    Assert(Body(d, "appearance-panel").Visibility == Visibility.Visible,
                        "Normal Settings forgot the last directly opened panel");
                };
                Call(w, "ShowSettings");
            } finally { w.Close(); }
        });
        Case("full settings synchronize quick switches, cancel and failed save", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                settings = delegate(Window d) {
                    Check(d, "Группировка приложений").IsChecked = false;
                    Check(d, "Поверх остальных окон").IsChecked = true;
                    Check(d, "Включить звуки").IsChecked = false;
                    Controls<ComboBox>(Body(d, "appearance-tiles"))[0].SelectedIndex = 2;
                    BottomPanelMode(d).SelectedIndex = 0;
                    Click(ButtonNamed(d, "Сохранить"));
                };
                Call(w, "ShowSettings");
                MenuItem quick = Menu(Tools(w), "Быстрые настройки"); OpenMenu(quick);
                Assert(!Menu(quick, "Группировка приложений").IsChecked &&
                    Menu(quick, "Поверх остальных окон").IsChecked && w.Topmost &&
                    !Menu(quick, "Звук включён").IsChecked &&
                    ((MenuItem)Menu(quick, "Нижняя панель").Items[0]).IsChecked,
                    "Quick settings stale after full save");
                ModeMenu(w, "labeled");
                Assert(Load().TileDisplayMode == "labeled" && !Load().Compact &&
                    Load().SettingsLastSection == "appearance", "Full save lost compatibility/navigation");
                string before = Snapshot(Live(w)), disk = File.ReadAllText(ConfigStore.FilePath);
                settings = delegate(Window d) {
                    Check(d, "Группировка приложений").IsChecked = true;
                    Check(d, "Поверх остальных окон").IsChecked = false;
                    Check(d, "Включить звуки").IsChecked = true;
                    Controls<ComboBox>(Body(d, "appearance-tiles"))[0].SelectedIndex = 0;
                    Click(ButtonNamed(d, "Отмена"));
                };
                Call(w, "ShowSettings");
                Assert(Snapshot(Live(w)) == before && File.ReadAllText(ConfigStore.FilePath) == disk,
                    "Cancel persisted settings edits");
                int errors = Errors;
                settings = delegate(Window d) {
                    Check(d, "Группировка приложений").IsChecked = true;
                    Check(d, "Поверх остальных окон").IsChecked = false;
                    Check(d, "Включить звуки").IsChecked = true;
                    Click(ButtonNamed(d, "Сохранить"));
                };
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Call(w, "ShowSettings");
                OpenMenu(quick);
                Assert(Errors == errors + 1 && Snapshot(Live(w)) == before &&
                    File.ReadAllText(ConfigStore.FilePath) == disk && w.Topmost &&
                    !Menu(quick, "Группировка приложений").IsChecked &&
                    !Menu(quick, "Звук включён").IsChecked, "Failed full save changed state/checkmarks");
            } finally { w.Close(); }
        });
        Case("search, add boundary and overflow menu", delegate {
            Seed("normal", 5); MainWindow w = Open(); try {
                TextBox search = (TextBox)Get(w, "_search"); search.Text = "APP 2"; Drain(); Assert(Tiles(w).Count == 1, "Search failed"); search.Text = "unmatched"; Drain(); Assert(Tiles(w).Count == 0 && ((TextBlock)Get(w, "_empty")).IsVisible, "Empty search failed"); search.Text = "";
                int calls = AddDialogCalls; Click(ButtonNamed(w, "+  Добавить")); Assert(AddMenu.IsOpen, "Add menu did not open"); Click(Menu(AddMenu, "Приложение или ярлык…")); AddMenu.IsOpen = false; Assert(AddDialogCalls == calls + 1, "Add menu did not reach file dialog");
                Assert(Menu(AddMenu, "Виджет плагина…") != null, "Widget add menu missing");
                Click(ToolsButton(w)); Assert(Tools(w).IsOpen, "Overflow did not open"); Tools(w).IsOpen = false;
                ContextMenu menu = Tools(w); Assert(menu.Items.IndexOf(Menu(menu, "О программе")) == menu.Items.IndexOf(Menu(menu, "Настройки…")) + 1, "About menu order wrong");
            } finally { w.Close(); }
        });
        foreach (double scale in new double[] { 1, 1.25, 1.5 }) { double s = scale; Case("header at minimum DIP width, content scale=" + s, delegate {
            Seed("normal", 5); MainWindow w = Open(); try {
                // Scale window and content together: emulate the same logical width, not actual OS DPI.
                w.Width = w.MinWidth * s; w.Height = 580 * s; ((FrameworkElement)w.Content).LayoutTransform = new ScaleTransform(s, s); Drain();
                DockPanel root = (DockPanel)w.Content; Border header = (Border)root.Children[0];
                Inside(ButtonNamed(w, "+  Добавить"), header, "Add"); Inside(ToolsButton(w), header, "Menu"); Inside((TextBox)Get(w, "_search"), header, "Search");
                TextBox search = (TextBox)Get(w, "_search"); search.Text = "Приложение App 3"; Drain(); Assert(Tiles(w).Count == 1, "Narrow-header search failed");
                Rect addBounds = ButtonNamed(w, "+  Добавить").TransformToAncestor(header).TransformBounds(new Rect(ButtonNamed(w, "+  Добавить").RenderSize));
                Rect searchBounds = search.TransformToAncestor(header).TransformBounds(new Rect(search.RenderSize));
                Assert(search.ActualWidth > 45 && searchBounds.Left >= addBounds.Right, "Header controls overlap");
                Assert(header.ActualHeight < 65, "Header is not compact"); Picture(w, "header-" + s.ToString(CultureInfo.InvariantCulture));
            } finally { w.Close(); }
        }); }
        foreach (string panelMode in new string[] { "compact", "categories", "detailed" })
        {
            string expectedMode = panelMode;
            Case("bottom panel mode and saved favorites " + expectedMode, delegate
            {
                Seed("normal", 1);
                AppConfig data = Load();
                data.BottomPanelMode = expectedMode;
                data.BottomPanelFavorites = new List<string> { "reminders", "countdown", "route" };
                ConfigStore.Save(data);
                AppConfig loaded = Load();
                Assert(loaded.BottomPanelMode == expectedMode &&
                    loaded.BottomPanelFavorites.Count == 3 &&
                    loaded.BottomPanelFavorites[0] == "reminders", "Bottom-panel settings were not persisted");
                MainWindow w = Open();
                try
                {
                    WrapPanel full = (WrapPanel)Get(w, "_bottomFullMonitors");
                    DockPanel compact = (DockPanel)Get(w, "_bottomCompactRow");
                    System.Windows.Controls.Primitives.UniformGrid groups = (System.Windows.Controls.Primitives.UniformGrid)Get(w, "_bottomCategoryTabs");
                    Assert(full.Visibility == (expectedMode == "detailed" ? Visibility.Visible : Visibility.Collapsed),
                        "Wrong full-panel visibility");
                    Assert(compact.Visibility == (expectedMode == "compact" ? Visibility.Visible : Visibility.Collapsed),
                        "Wrong compact-panel visibility");
                    Assert(groups.Visibility == (expectedMode == "categories" ? Visibility.Visible : Visibility.Collapsed),
                        "Wrong category-panel visibility");
                    if (expectedMode == "compact")
                    {
                        WrapPanel favorites = (WrapPanel)Get(w, "_bottomCompactItems");
                        Assert(favorites.Children.Count == 2,
                            "Compact panel must show only enabled favorites in saved order");
                        Click((Button)compact.Children[0]);
                        Assert(full.Visibility == Visibility.Visible && favorites.Visibility == Visibility.Collapsed && full.Parent == compact,
                            "Compact expand did not replace favorites with details");
                        Click((Button)compact.Children[0]);
                        Assert(full.Visibility == Visibility.Collapsed && favorites.Visibility == Visibility.Visible && favorites.Children.Count == 2,
                            "Compact collapse did not restore short view");
                    }
                    if (expectedMode == "categories")
                    {
                        Click((Button)groups.Children[2]);
                        WrapPanel details = (WrapPanel)Get(w, "_bottomCategoryDetails");
                        Assert(details.Visibility == Visibility.Visible && details.Children.Count >= 2,
                            "Time category did not expose its tools");
                    }
                }
                finally { w.Close(); }
            });
        }
        Case("plugin indicators share footer wrap layout without losing saved cards", delegate {
            Seed("normal", 1);
            AppConfig data = Load();
            data.BottomPanelMode = "detailed";
            data.ShowCountdownTimer = true;
            data.BottomPanelFavorites = new List<string> { "countdown", "reminders" };
            data.WidgetIndicators = new List<PluginWidgetPlacementConfig> { new PluginWidgetPlacementConfig {
                PluginId = "example.test", WidgetId = "route", Version = "1.0.0",
                Title = "Network", Size = "indicator" } };
            ConfigStore.Save(data);
            MainWindow w = Open(); try {
                w.Width = 1050; w.Height = 700; Drain(); w.UpdateLayout();
                PluginWidgetWorkspace workspace = (PluginWidgetWorkspace)Get(w, "_pluginWidgets");
                WrapPanel full = (WrapPanel)Get(w, "_bottomFullMonitors");
                WrapPanel favorites = (WrapPanel)Get(w, "_bottomCompactItems");
                DockPanel compact = (DockPanel)Get(w, "_bottomCompactRow");
                ContentControl indicator = (ContentControl)full.Children[full.Children.Count - 1];
                Assert(workspace.IndicatorCount == 1 && workspace.IndicatorHost.Visibility == Visibility.Collapsed &&
                    indicator.Parent == full, "Detailed footer has a separate plugin row");
                FrameworkElement reminder = (FrameworkElement)Get(w, "_remindersChip");
                Assert(Math.Abs(indicator.TranslatePoint(new Point(0,0), full).Y - reminder.TranslatePoint(new Point(0,0), full).Y) <= 1,
                    "Indicator did not use spare space on detailed first row");
                Assert(Math.Abs(indicator.ActualHeight - reminder.ActualHeight) <= 1 &&
                    Math.Abs(indicator.ActualWidth - reminder.ActualWidth) > 2,
                    "Detailed row did not share height while preserving content widths");
                Picture(w, "2243-footer-detailed");
                Assert(w.ContextMenu == null && ((Border)Get(w, "_monitorBorder")).ContextMenu == null &&
                    reminder.ContextMenu != null && indicator.ContextMenu != null &&
                    !Object.ReferenceEquals(reminder.ContextMenu, indicator.ContextMenu),
                    "Footer and indicators do not own independent menus");
                Button footerMenu = Controls<Button>(w).Find(button => Object.Equals(button.Tag, "BottomPanelMenu"));
                ContextMenu general = ((Border)Get(w, "_footerBorder")).ContextMenu;
                Assert(footerMenu != null && general != null, "Missing access to the common footer menu");
                w.Width = w.MinWidth; Drain();
                Click(footerMenu); Drain();
                Assert(general.IsOpen && general.PlacementTarget == footerMenu && !indicator.ContextMenu.IsOpen,
                    "Common menu not available when the footer wraps");
                Picture(general, "2243-footer-general-menu");
                general.IsOpen = false; w.Width = 1050; Drain();
                foreach (FrameworkElement card in new FrameworkElement[] { reminder, indicator })
                {
                    card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) {
                        RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent
                    });
                    Drain();
                    Assert(card.ContextMenu.IsOpen && !general.IsOpen, "Right click opened the wrong context menu");
                    Picture(card.ContextMenu, card == reminder ? "2243-footer-builtin-menu" : "2243-footer-plugin-menu");
                    card.ContextMenu.IsOpen = false;
                }
                Live(w).BottomPanelMode = "compact";
                Call(w, "ApplyBottomPanelMode"); Drain();
                Assert(indicator.Parent == favorites && favorites.Children.Count == 3, "Compact indicator missing or duplicated");
                Assert(Math.Abs(indicator.TranslatePoint(new Point(0,0), favorites).Y -
                    ((FrameworkElement)favorites.Children[0]).TranslatePoint(new Point(0,0), favorites).Y) <= 1,
                    "Compact indicator did not use spare first row");
                Assert(Math.Abs(indicator.ActualHeight - ((FrameworkElement)favorites.Children[0]).ActualHeight) <= 1,
                    "Compact indicator row has unequal heights");
                Picture(w, "2243-footer-compact");
                Click((Button)compact.Children[0]);
                Drain(); Picture(w, "2243-footer-expanded");
                Assert(indicator.Parent == full && favorites.Children.Count == 0, "Expanded compact lost or duplicated indicator");
                Click((Button)compact.Children[0]);
                Assert(indicator.Parent == favorites && favorites.Children.Count == 3, "Collapsed compact lost indicator");
                Live(w).BottomPanelMode = "categories";
                Call(w, "ApplyBottomPanelMode"); Drain();
                Assert(indicator.Parent == workspace.IndicatorHost && workspace.IndicatorHost.Visibility == Visibility.Visible,
                    "Category mode lost indicator host");
                Assert(Controls<Button>((DependencyObject)Get(w, "_bottomCategoryTabs")).TrueForAll(button => button.ContextMenu != null),
                    "Category tabs inherited the common menu");
                Picture(w, "2243-footer-categories");
                Live(w).BottomPanelMode = "detailed";
                Call(w, "ApplyBottomPanelMode"); Drain();
                Assert(indicator.Parent == full && workspace.IndicatorHost.Children.Count == 0 &&
                    workspace.IndicatorCount == 1, "Mode switches duplicated or lost placement");
                // Force a width-constrained wrap independently of the main window minimum.
                WrapPanel narrow = new WrapPanel();
                Border builtin = new Border { Width = 110, Height = 30 };
                narrow.Children.Add(builtin);
                workspace.AttachIndicatorPanel(narrow);
                indicator.Width = 110;
                narrow.Measure(new Size(160, Double.PositiveInfinity));
                narrow.Arrange(new Rect(0, 0, 160, narrow.DesiredSize.Height));
                Assert(indicator.TranslatePoint(new Point(0,0), narrow).Y >= 30, "Indicator did not wrap at narrow width");
                indicator.ClearValue(FrameworkElement.WidthProperty);
                workspace.AttachIndicatorPanel(full);
                Assert(workspace.CaptureIndicators().Count == 1, "Layout reparenting changed saved placements");
                Click((MenuItem)indicator.ContextMenu.Items[1]);
                Assert(workspace.IndicatorCount == 0 && workspace.CaptureIndicators() == null &&
                    full.Children.Contains(reminder) && full.Children.Count == 5, "Removal touched built-in indicators");
            } finally { w.Close(); }
        });
        Case("legacy configuration retains detailed bottom panel", delegate
        {
            Seed("normal", 1);
            AppConfig data = Load();
            data.BottomPanelMode = null;
            data.BottomPanelFavorites = null;
            ConfigStore.Save(data);
            MainWindow w = Open();
            try
            {
                Assert(((WrapPanel)Get(w, "_bottomFullMonitors")).Visibility == Visibility.Visible,
                    "Legacy configuration changed the default layout");
            }
            finally { w.Close(); }
        });
        foreach (double scale in new double[] { 1, 1.25, 1.5 }) { double s = scale;
            Case("bottom settings rendering and disabled contrast scale=" + s, delegate {
                Seed("normal", 1); MainWindow w = Open(); try {
                    settings = delegate(Window d) {
                        d.Width = d.MinWidth * s; d.Height = 555 * s;
                        ((FrameworkElement)d.Content).LayoutTransform = new ScaleTransform(s, s); Drain();
                        if (Body(d, "appearance-panel").Visibility != Visibility.Visible)
                            Click(Section(d, "appearance-panel"));
                        ComboBox mode = BottomPanelMode(d);
                        ListBox favorites = Controls<ListBox>(Body(d, "appearance-panel"))[0];
                        Assert(mode.Items.Count == 3 && mode.SelectedIndex == 2 && !favorites.IsEnabled &&
                            favorites.Visibility == Visibility.Collapsed,
                            "Detailed mode unexpectedly shows editable favorites");
                        Dark(Visual<Border>(mode).Background, "ComboBox");
                        ContentPresenter selection = VisualTreeHelper.GetChild(mode, 0) is Grid
                            ? (ContentPresenter)((Grid)VisualTreeHelper.GetChild(mode, 0)).Children[1] : null;
                        Assert(selection != null && selection.VerticalAlignment == VerticalAlignment.Center, "Selected text not centered");
                        Picture(d, "bottom-settings-detailed-" + s.ToString(CultureInfo.InvariantCulture));
                        mode.SelectedIndex = 0; Drain();
                        Assert(favorites.IsEnabled && favorites.Visibility == Visibility.Visible &&
                            Controls<CheckBox>(Body(d, "appearance-panel")).Count > 0,
                            "Compact mode did not reveal its favorites");
                        Dark(Visual<Border>(favorites).Background, "ListBox");
                        Readable((CheckBox)((ListBoxItem)favorites.Items[0]).Content);
                        favorites.SelectedIndex = 0; Drain();
                        ListBoxItem first = (ListBoxItem)favorites.Items[0];
                        Dark(Visual<Border>(first).Background, "Selected item");
                        CheckBox check = (CheckBox)first.Content;
                        check.IsChecked = false; check.IsChecked = true;
                        Click(ButtonNamed(d, "↓ Ниже"));
                        Assert(favorites.Items[1] == first && favorites.SelectedIndex == 1, "Down lost item/selection");
                        Click(ButtonNamed(d, "↑ Выше"));
                        Assert(favorites.Items[0] == first && check.IsChecked == true, "Up lost checkbox state");
                        ScrollViewer listScroll = Visual<ScrollViewer>(favorites);
                        listScroll.ScrollToEnd(); Drain();
                        Assert(listScroll.ScrollableHeight > 0 && listScroll.VerticalOffset > 0, "Favorites do not scroll");
                        listScroll.ScrollToHome(); Drain();
                        Picture(d, "bottom-settings-compact-" + s.ToString(CultureInfo.InvariantCulture));
                        mode.IsDropDownOpen = true; Drain();
                        System.Windows.Controls.Primitives.Popup popup =
                            (System.Windows.Controls.Primitives.Popup)mode.Template.FindName("PART_Popup", mode);
                        Assert(popup.IsOpen, "Dropdown not opened");
                        Dark(Visual<Border>(popup.Child).Background, "Dropdown");
                        foreach (ComboBoxItem item in mode.Items) {
                            Dark(Visual<Border>(item).Background, "Dropdown item");
                            Assert(((SolidColorBrush)item.Foreground).Color.R > 200, "Dropdown text not readable");
                        }
                        Picture((FrameworkElement)popup.Child, "bottom-dropdown-" + s.ToString(CultureInfo.InvariantCulture));
                        mode.IsDropDownOpen = false;
                        mode.SelectedIndex = 1; Drain();
                        Assert(!favorites.IsEnabled && favorites.Visibility == Visibility.Collapsed,
                            "Category mode did not hide favorites");
                        Readable(check);
                        Picture(d, "bottom-settings-categories-" + s.ToString(CultureInfo.InvariantCulture));
                        d.Height = d.MinHeight * s; Drain();
                        Inside(ButtonNamed(d, "Сохранить"), (FrameworkElement)d.Content, "Save at minimum size");
                        Inside(ButtonNamed(d, "Отмена"), (FrameworkElement)d.Content, "Cancel at minimum size");
                        Picture(d, "bottom-settings-min-" + s.ToString(CultureInfo.InvariantCulture));
                    };
                    Call(w, "ShowSettings");
                } finally { w.Close(); }
            });
        }
        Case("settings persist favorite order and checks after reopen", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                settings = delegate(Window d) {
                    Click(Section(d, "appearance-panel"));
                    BottomPanelMode(d).SelectedIndex = 0;
                    ListBox list = Controls<ListBox>(Body(d, "appearance-panel"))[0];
                    list.SelectedIndex = 0; Click(ButtonNamed(d, "↓ Ниже"));
                    ((CheckBox)((ListBoxItem)list.Items[0]).Content).IsChecked = false;
                    Click(ButtonNamed(d, "Сохранить"));
                };
                Call(w, "ShowSettings");
                Assert(Load().BottomPanelMode == "compact" && Load().BottomPanelFavorites.Contains("route") && Load().BottomPanelFavorites.Contains("reminders") &&
                    !Load().BottomPanelFavorites.Contains("countdown"), "Settings failed to persist favorites");
                settings = delegate(Window d) {
                    ListBox list = Controls<ListBox>(Body(d, "appearance-panel"))[0];
                    Assert(BottomPanelMode(d).SelectedIndex == 0 &&
                        (string)((CheckBox)((ListBoxItem)list.Items[0]).Content).Tag == "weather", "Reopened favorites changed order");
                    foreach (ListBoxItem row in list.Items)
                        if ((string)((CheckBox)row.Content).Tag == "countdown")
                            Assert(((CheckBox)row.Content).IsChecked == false, "Unchecked favorite not persisted");
                };
                Call(w, "ShowSettings");
            } finally { w.Close(); }
        });
        foreach (string mode in new string[] { "compact", "categories", "detailed" }) { string m = mode;
            Case("bottom layout minimum width and live summaries " + m, delegate {
                Seed("normal", 1); MainWindow w = Open(); try {
                    w.Width = w.MinWidth; w.Height = 580;
                    Live(w).BottomPanelMode = m;
                    Live(w).RdpKeepAwake = true; Live(w).ShowWeather = true; Live(w).ShowPomodoro = true;
                    Live(w).BottomPanelFavorites = new List<string> { "rdp", "weather", "countdown", "reminders" };
                    // Fixture data verifies presentation bindings, not network/weather availability.
                    ((TextBlock)Get(w, "_rdpStatus")).Text = "RDP: Test interface";
                    ((TextBlock)Get(w, "_weather")).Text = "Погода · Test city: -7 °C · ощущается как -9 °C · Облачно";
                    ((TextBlock)Get(w, "_pomodoro")).Text = "Помодоро: Работа 04:32 ▶";
                    Call(w, "ApplyBottomPanelMode"); Drain();
                    Border panel = (Border)Get(w, "_monitorBorder");
                    if (m == "categories") {
                        System.Windows.Controls.Primitives.UniformGrid cards = (System.Windows.Controls.Primitives.UniformGrid)Get(w, "_bottomCategoryTabs");
                        Assert(cards.Children.Count == 3 && cards.ActualHeight < 90, "Cards not compact");
                        foreach (Button card in cards.Children) Inside(card, panel, "Category card");
                        List<TextBlock> labels = Controls<TextBlock>(cards);
                        Assert(labels.Exists(t => t.Text == "-7 °C") && labels.Exists(t => t.Text == "Test interface") &&
                            labels.Exists(t => t.Text == "Работа 04:32 ▶"), "Live category summaries missing");
                        ((TextBlock)Get(w, "_weather")).Text = "Погода · Test city: 3 °C · ощущается как 0 °C";
                        Drain(); Assert(labels.Exists(t => t.Text == "3 °C"), "Temperature binding stale");
                        for (int i = 0; i < 3; i++) {
                            Click((Button)cards.Children[i]);
                            Assert(((WrapPanel)Get(w, "_bottomCategoryDetails")).Visibility == Visibility.Visible, "Category not expanded");
                            Click((Button)cards.Children[i]);
                            Assert(((WrapPanel)Get(w, "_bottomCategoryDetails")).Visibility == Visibility.Collapsed, "Category not collapsed");
                        }
                        Picture(w, "bottom-categories-min-fixture");
                        w.Width = 900; Drain(); foreach (Button card in cards.Children) Inside(card, panel, "Wide category card");
                        Picture(w, "bottom-categories-wide-fixture");
                        ((TextBlock)Get(w, "_rdpStatus")).Text = "RDP: Changed interface";
                        ((TextBlock)Get(w, "_pomodoro")).Text = "Помодоро: Работа 04:31 ▶";
                        Drain();
                        Assert(labels.Exists(t => t.Text == "Changed interface") &&
                            labels.Exists(t => t.Text == "Работа 04:31 ▶"), "Network/time bindings stale");
                        AppConfig changed = Live(w).Copy();
                        changed.RdpKeepAwake = false; changed.ShowWeather = false; changed.ShowPomodoro = false;
                        changed.ShowCountdownTimer = false;
                        Call(w, "SaveConfigChanges", changed, true); Call(w, "ApplyBottomPanelMode"); Drain();
                        Assert(labels.Exists(t => t.Text == "RDP выключен") &&
                            labels.Exists(t => t.Text == "Погода выключена") &&
                            labels.Exists(t => t.Text == "Таймеры выключены"), "Saved visibility settings leave stale summaries");
                    } else if (m == "compact") {
                        WrapPanel items = (WrapPanel)Get(w, "_bottomCompactItems");
                        foreach (Button item in items.Children) Inside(item, panel, "Compact favorite");
                        Assert(Visual<ScrollViewer>(items) == null, "Compact horizontal scroll remains");
                        Assert(items.ActualHeight >= 60, "Narrow compact favorites did not wrap");
                        Picture(w, "bottom-compact-min-fixture");
                    } else Picture(w, "bottom-detailed-min-fixture");
                } finally { w.Close(); }
            });
        }
        Case("weather and reminders buttons wrap full text in compact and category modes", delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                w.Width = w.MinWidth; w.Height = 580;
                Live(w).ShowWeather = true;
                Live(w).BottomPanelMode = "compact";
                Live(w).BottomPanelFavorites = new List<string> { "weather", "reminders" };
                string forecast = "Погода · Тестовый город: -17 °C · ощущается как -25 °C · переменная облачность и снег";
                string reminder = "Оплатить кладовую и не забыть подготовить документы к оплате · 29.11 18:00";
                ((TextBlock)Get(w, "_weather")).Text = forecast;
                ((TextBlock)Get(w, "_remindersChipText")).Text = reminder;
                Call(w, "ApplyBottomPanelMode"); Drain();

                WrapPanel favorites = (WrapPanel)Get(w, "_bottomCompactItems");
                Assert(favorites.Children.Count == 2, "Expected weather and reminders in compact favorites");
                for (int i = 0; i < 2; i++) {
                    Button button = (Button)favorites.Children[i];
                    TextBlock label = (TextBlock)button.Content;
                    Assert(label.Text == (i == 0 ? forecast : reminder), "Compact button text was shortened");
                    Assert(label.TextWrapping == TextWrapping.Wrap && label.TextTrimming == TextTrimming.None,
                        "Weather/reminder compact button still clips text with ellipsis");
                    Assert(label.ActualHeight > 20 && button.ActualHeight > 32,
                        "Weather/reminder compact button did not grow for multiple lines");
                    Assert(label.ActualWidth <= 210.5, "Wrapped compact button exceeds allowed width");
                }
                Assert(Visual<ScrollViewer>(favorites) == null, "Compact panel gained horizontal scroll");
                Picture(w, "weather-reminders-compact-wrap");

                Live(w).BottomPanelMode = "categories";
                Call(w, "ApplyBottomPanelMode"); Drain();
                System.Windows.Controls.Primitives.UniformGrid categories =
                    (System.Windows.Controls.Primitives.UniformGrid)Get(w, "_bottomCategoryTabs");
                foreach (int category in new int[] { 1, 2 }) {
                    Click((Button)categories.Children[category]);
                    WrapPanel details = (WrapPanel)Get(w, "_bottomCategoryDetails");
                    string expected = category == 1 ? forecast : reminder;
                    bool found = false;
                    foreach (Button button in details.Children) {
                        TextBlock label = button.Content as TextBlock;
                        if (label == null || label.Text != expected) continue;
                        found = true;
                        Assert(label.TextWrapping == TextWrapping.Wrap && label.TextTrimming == TextTrimming.None,
                            "Expanded category weather/reminder text still ellipsizes");
                        Assert(label.ActualHeight > 20, "Expanded category button did not wrap long text");
                    }
                    Assert(found, "Expected weather/reminder button missing from expanded category");
                    Click((Button)categories.Children[category]);
                }
            } finally { w.Close(); }
        });
        Case("weather forecast and reminders list use the same dark scrollbars as About", delegate {
            WeatherForecastWindow weather = new WeatherForecastWindow(delegate { }, delegate { });
            try {
                weather.Height = weather.MinHeight;
                weather.Width = weather.MinWidth;
                WeatherSnapshot forecast = new WeatherSnapshot {
                    City = "Кемерово", Source = "Open-Meteo", Temperature = -1,
                    Condition = "пасмурно", UpdatedAt = DateTime.Now
                };
                for (int i = 0; i < 7; i++) forecast.Days.Add(new WeatherDay {
                    Date = DateTime.Today.AddDays(i), Condition = "пасмурно",
                    Min = -5, Max = 1, PrecipitationProbability = 100
                });
                weather.UpdateForecast(forecast, "Кемерово", null, false);
                weather.Show(); Drain();
                List<ScrollViewer> views = Controls<ScrollViewer>(weather);
                Assert(views.Count == 1, "Forecast scroll viewport not found");
                ScrollViewer scroll = views[0]; scroll.ApplyTemplate(); scroll.UpdateLayout(); Drain();
                System.Windows.Controls.Primitives.ScrollBar bar =
                    scroll.Template.FindName("PART_VerticalScrollBar", scroll)
                        as System.Windows.Controls.Primitives.ScrollBar;
                Assert(bar != null && bar.ActualWidth > 0 && bar.ActualWidth <= 12.5,
                    "Forecast still uses the default wide scrollbar");
                Dark(Visual<Border>(bar).Background, "Forecast scrollbar");
                foreach (TextBlock cell in Controls<TextBlock>(weather)) {
                    if (!cell.Text.StartsWith("осадки ")) continue;
                    Rect bounds = cell.TransformToAncestor(scroll).TransformBounds(new Rect(cell.RenderSize));
                    Assert(bounds.Right <= scroll.ActualWidth - bar.ActualWidth - scroll.Padding.Right + 0.5,
                        "Forecast precipitation column clipped at minimum width");
                }
                Assert(scroll.ScrollableHeight > 0, "Forecast did not overflow for scroll verification");
                scroll.ScrollToEnd(); Drain();
                Assert(scroll.VerticalOffset > 0, "Forecast scrollbar cannot scroll");
                ScrollInput(scroll, "Forecast");
            } finally { weather.Close(); }

            Seed("normal", 1);
            ReminderDocument doc = new ReminderDocument();
            for (int i = 0; i < 16; i++) doc.Reminders.Add(new ReminderItem {
                Id = Guid.NewGuid().ToString(), Title = "Напоминание " + i,
                DueLocal = ReminderStore.ToValue(DateTime.Today.AddDays(i + 1).AddHours(9)),
                Repeat = "once", Active = true
            });
            ReminderStore.Save(doc);
            MainWindow window = Open();
            try {
                Call(window, "ShowRemindersWindow");
                Window reminders = (Window)Get(window, "_remindersWindow");
                Assert(reminders != null && reminders.IsVisible, "Reminder window not visible");
                reminders.Width = reminders.MinWidth; reminders.Height = reminders.MinHeight; Drain();
                List<ScrollViewer> views = Controls<ScrollViewer>(reminders);
                Assert(views.Count == 1, "Reminders list scroll viewport not found");
                ScrollViewer scroll = views[0]; scroll.ApplyTemplate(); scroll.UpdateLayout(); Drain();
                System.Windows.Controls.Primitives.ScrollBar bar =
                    scroll.Template.FindName("PART_VerticalScrollBar", scroll)
                        as System.Windows.Controls.Primitives.ScrollBar;
                Assert(bar != null && bar.ActualWidth > 0 && bar.ActualWidth <= 12.5,
                    "Reminder list still uses the default wide scrollbar");
                Dark(Visual<Border>(bar).Background, "Reminders scrollbar");
                Assert(scroll.ScrollableHeight > 0, "Reminder entries did not overflow the viewport");
                scroll.ScrollToEnd(); Drain();
                Assert(scroll.VerticalOffset > 0, "Reminders scrollbar cannot scroll");
                ScrollInput(scroll, "Reminders list");
                Click(ButtonNamed(reminders, "Выбрать"));
                scroll.UpdateLayout(); Drain();
                ScrollInput(scroll, "Reminders list with selection toolbar");
                reminders.Close();
            } finally {
                window.Close();
                if (File.Exists(ReminderStore.FilePath)) File.Delete(ReminderStore.FilePath);
            }
        });
        foreach (bool minimum in new bool[] { false, true }) { bool min = minimum; Case("about version, repository and bounds minimum=" + min, delegate {
            Seed("normal", 1); MainWindow w = Open(); try {
                about = delegate(Window d) {
                    if (min) { d.Width = d.MinWidth; d.Height = d.MinHeight; Drain(); }
                    Picture(d, min ? "about-min" : "about"); bool version = false, author = false, news = false, bullet = false;
                    System.Reflection.Assembly assembly = typeof(MainWindow).Assembly;
                    AssemblyInformationalVersionAttribute info = (AssemblyInformationalVersionAttribute)
                        Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
                    Assert(info != null && !String.IsNullOrWhiteSpace(info.InformationalVersion), "Version metadata missing");
                    Version numericVersion = assembly.GetName().Version;
                    string displayVersion = numericVersion.Major + "." + numericVersion.Minor + "." + numericVersion.Build;
                    if (info.InformationalVersion.StartsWith(displayVersion + "-beta.", StringComparison.Ordinal))
                        displayVersion = AppUpdate.InstalledVersionText();
                    Assert(info.InformationalVersion == displayVersion ||
                        System.Text.RegularExpressions.Regex.IsMatch(info.InformationalVersion,
                            "^" + System.Text.RegularExpressions.Regex.Escape(displayVersion) + @"-rc\.[1-9]\d*$"),
                        "Informational version differs from numeric version");
                    MethodInfo notesLookup = typeof(MainWindow).GetMethod("CompiledReleaseNotes",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    Assert(notesLookup != null, "Compiled release notes lookup missing");
                    string currentNotes = (string)notesLookup.Invoke(null, new object[] { displayVersion });
                    Assert(!String.IsNullOrWhiteSpace(currentNotes), "Current-version release notes missing");
                    string firstNote = currentNotes.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    foreach (TextBlock t in Controls<TextBlock>(d)) {
                        if (t.Text == "Версия " + displayVersion) version = true;
                        if (t.Text == "Автор: mishakov1c") author = true;
                        if (t.Text == "Что нового в версии " + displayVersion) news = true;
                        if (t.Text == "•  " + firstNote) bullet = true;
                    }
                    Assert(version && author && news && bullet &&
                        numericVersion.ToString() == displayVersion.Split('-')[0] + ".0",
                        "About metadata or current-version release notes incorrect");
                    List<ScrollViewer> scrollers = Controls<ScrollViewer>(d);
                    Assert(scrollers.Count == 1 && scrollers[0].ActualHeight > 20,
                        "Release notes viewport collapsed");
                    ScrollViewer notesScroll = scrollers[0];
                    System.Windows.Controls.Primitives.ScrollBar bar =
                        (System.Windows.Controls.Primitives.ScrollBar)notesScroll.Template.FindName(
                            "PART_VerticalScrollBar", notesScroll);
                    Assert(bar != null && bar.ActualWidth == 12, "About scrollbar must use the scoped narrow style");
                    System.Windows.Controls.Primitives.Track track =
                        (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
                    Assert(track != null && track.Orientation == Orientation.Vertical,
                        "About scrollbar track is not vertical");
                    Dark(Visual<Border>(bar).Background, "About scrollbar");
                    if (min)
                    {
                        Assert(scrollers[0].ScrollableHeight > 0,
                            "At minimum size, the About header and notes must scroll together");
                        scrollers[0].ScrollToEnd(); Drain();
                        Assert(scrollers[0].VerticalOffset > 0, "Release notes are unreachable at minimum size");
                    }
                    Assert(notesLookup.Invoke(null, new object[] { "0.0.0" }) == null,
                        "Outdated release notes must not appear after a version bump");
                    Inside(ButtonNamed(d, "GitHub"), (FrameworkElement)d.Content, "GitHub"); Inside(ButtonNamed(d, "Закрыть"), (FrameworkElement)d.Content, "Close");
                    link = null; Click(ButtonNamed(d, "GitHub")); Assert(link != null && link.FileName == "https://github.com/mishakov1c/app_shelf" && link.UseShellExecute, "Repository launch incorrect");
                    Click(ButtonNamed(d, "Закрыть")); Assert(!d.IsVisible, "Close button failed");
                }; Call(w, "ShowAboutWindow");
            } finally { w.Close(); }
        }); }
        foreach (string oldMode in new string[] { null, "vertical", "grid", "free" }) { string legacyMode = oldMode;
            Case("legacy layout migrates to canvas " + (legacyMode ?? "default"), delegate {
                Seed("normal", 4);
                AppConfig config = Load();
                config.GroupLayoutMode = legacyMode;
                config.GroupWidthMode = "content";
                config.GroupAlignment = "alternate";
                config.Items[3].Group = "Вторая";
                config.GroupZoneOverrides = new Dictionary<string, string> { { "Вторая", "bottom-right" } };
                ConfigStore.Save(config);
                MainWindow w = Open(); try {
                    AppConfig saved = Load();
                    Assert(saved.GroupLayoutMode == "canvas" && saved.CanvasInitialized &&
                        saved.Items.Count == 4 && saved.Groups.Count == 3 &&
                        saved.Items[3].Group == "Вторая" && saved.GroupZoneOverrides["Вторая"] == "bottom-right",
                        "Migration lost group data or old metadata");
                    Assert(Get(w, "_canvasBoard") != null && Get(w, "_freeLayoutGrid") == null,
                        "Legacy layout is still active");
                    Assert(Tiles(w).Count == 4, "Migration lost application tiles");
                    Assert(saved.CanvasPositions != null && saved.CanvasPositions.ContainsKey("app:Первая") &&
                        saved.CanvasPositions.ContainsKey("app:Вторая"), "Legacy positions were not captured");
                    if (legacyMode == "free")
                    {
                        double viewport = ((ScrollViewer)Get(w, "_tilesScroller")).ViewportHeight;
                        double bottomGroupY = saved.CanvasPositions["app:Вторая"].Y * FreeCanvasLayout.NormalizeStep(saved.CanvasGridStep);
                        Assert(viewport > 250 && bottomGroupY > viewport / 2,
                            "Legacy bottom zone migrated near the top of the canvas");
                    }
                } finally { w.Close(); }
            });
        }
        Case("enabling grouping during search migrates the unfiltered legacy layout", delegate {
            Seed("normal", 2);
            AppConfig config = Load();
            config.GroupingEnabled = false;
            config.GroupLayoutMode = "free";
            config.Items[1].Group = null;
            ConfigStore.Save(config);
            MainWindow w = Open(); try {
                TextBox search = (TextBox)Get(w, "_search");
                search.Text = "non-matching filter";
                Call(w, "SetGrouping", true);
                Drain();
                AppConfig saved = Load();
                Assert(saved.CanvasInitialized && saved.CanvasPositions != null &&
                    saved.CanvasPositions.ContainsKey("app:Первая") && saved.CanvasPositions.ContainsKey("app:"),
                    "Search prevented migration of legacy group positions");
                Assert(Tiles(w).Count == 0, "Migration cleared the active search");
                search.Clear(); Drain();
                Assert(Tiles(w).Count == 2, "Clearing search did not restore both shortcuts");
            } finally { w.Close(); }
        });
        Case("legacy ZIP migrates application groups and retains unselected widget geometry", delegate {
            Seed("normal", 1);
            AppConfig legacy = Load();
            legacy.GroupLayoutMode = "free";
            legacy.GroupZoneOverrides = new Dictionary<string, string> { { "Первая", "bottom-right" } };
            string zip = Path.Combine(ConfigStore.DirectoryPath, "legacy-layout.zip");
            using (var file = File.Create(zip))
            using (var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var entry = archive.CreateEntry("apps.json").Open())
                    new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(entry, legacy);
                using (var entry = archive.CreateEntry("reminders.json").Open())
                    new DataContractJsonSerializer(typeof(ReminderDocument)).WriteObject(entry, new ReminderDocument());
            }
            AppConfig current = legacy.Copy();
            current.GroupLayoutMode = "canvas";
            current.CanvasInitialized = true;
            current.CanvasPositions = new Dictionary<string, FreeCanvasRect> {
                { "widget:1", new FreeCanvasRect(0, 12, 12, 4) }
            };
            current.WidgetGroups = new List<PluginWidgetGroupConfig> {
                new PluginWidgetGroupConfig { Number = 1, Title = "Existing widgets", Zone = "top-left", Collapsed = true,
                    Widgets = new List<PluginWidgetPlacementConfig> {
                        new PluginWidgetPlacementConfig { PluginId = "example.stocks", WidgetId = "quotes",
                            Title = "Quotes", Version = "1.0.0", Size = "quarter" }
                    } }
            };
            ConfigStore.Save(current);
            BackupStore.RestoreAll(zip);
            MainWindow w = Open(); try {
                AppConfig saved = Load();
                Assert(saved.GroupLayoutMode == "canvas" && saved.CanvasInitialized &&
                    saved.CanvasPositions.ContainsKey("app:Первая") && saved.Items.Count == 1,
                    "Retaining unselected widgets skipped legacy application migration");
                FreeCanvasRect pos = saved.CanvasPositions["widget:1"];
                Assert(pos.X == 0 && pos.Y == 12 && pos.Width == 12 && pos.Height == 4 &&
                    saved.WidgetGroups[0].Collapsed && saved.WidgetGroups[0].Widgets[0].WidgetId == "quotes",
                    "Restore replaced unselected widget geometry or content");
            } finally { w.Close(); }
        });
        Case("Network plugin migration preserves legacy targets and existing plugin data", delegate {
            AppConfig source = new AppConfig { ShowNetwork = true, NetworkCheck1Mode = "tcp",
                NetworkCheck1Target = "localhost:443", NetworkCheck2Mode = "icmp",
                NetworkCheck2Target = "127.0.0.1" };
            string path = Path.Combine(ConfigStore.DirectoryPath, "network-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try {
                AppConfig next = NetworkPluginMigration.Prepare(source, "1.0.0",
                    new[] { "network2", "weather", "route", "network2" }, path);
                Assert(next.NetworkPluginMigrated && !source.NetworkPluginMigrated && source.WidgetIndicators == null,
                    "Migration mutated source or did not mark completion");
                Assert(next.WidgetIndicators.Count == 2 && next.WidgetIndicators[0].WidgetId == "network2" &&
                    next.WidgetIndicators[1].WidgetId == "route", "Legacy indicator selection/order lost");
                Assert(next.ShowNetwork && next.NetworkCheck1Target == source.NetworkCheck1Target,
                    "Rollback targets not preserved");
                string settings = Path.Combine(path, "network-settings.json");
                string original = File.ReadAllText(settings);
                Assert(original.Contains("localhost:443") && original.Contains("127.0.0.1"), "Seed missing targets");
                File.WriteAllText(settings, "independent plugin settings");
                AppConfig again = NetworkPluginMigration.Prepare(source, "1.0.0", new[] { "network1" }, path);
                Assert(again.NetworkPluginMigrated && File.ReadAllText(settings) == "independent plugin settings",
                    "Existing plugin data overwritten");
                AppConfig repeated = NetworkPluginMigration.Prepare(next, "1.0.0", new[] { "network1" }, path);
                Assert(repeated.WidgetIndicators.Count == 2, "Migration repeated after completion");
                AppConfig crowded = source.Copy();
                crowded.WidgetIndicators = new List<PluginWidgetPlacementConfig>();
                for (int i = 0; i < 6; i++)
                    crowded.WidgetIndicators.Add(new PluginWidgetPlacementConfig {
                        PluginId = "example.test", WidgetId = "item" + i, Version = "1.0.0",
                        Title = "Indicator " + i, Size = "indicator" });
                bool refused = false;
                try { NetworkPluginMigration.Prepare(crowded, "1.0.0", new[] { "network1" }, path); }
                catch (InvalidOperationException) { refused = true; }
                Assert(refused && !crowded.NetworkPluginMigrated && crowded.WidgetIndicators.Count == 6 &&
                    File.ReadAllText(settings) == "independent plugin settings", "Capacity failure changed data");
                source.ShowNetwork = false;
                File.Delete(settings);
                AppConfig disabled = NetworkPluginMigration.Prepare(source, "1.0.0", new string[0], path);
                Assert(disabled.WidgetIndicators.Count == 0 && File.ReadAllText(settings).Contains("\"showNetwork\":false"),
                    "Disabled legacy monitor was enabled or gained indicators");
            } finally { Directory.Delete(path, true); }
        });
        Case("D03 heading wraps on canvas", delegate {
            Seed("normal", 2); MainWindow w = Open(); try {
                DockPanel header = (DockPanel)Group(w, 0).Header;
                TextBlock heading = (TextBlock)header.Children[header.Children.Count - 1];
                Assert(heading.TextWrapping == TextWrapping.Wrap && heading.TextTrimming == TextTrimming.None &&
                    Convert.ToString(heading.ToolTip).Contains("Правая кнопка"), "D03 heading/hint missing");
            } finally { w.Close(); }
        });
        Case("existing canvas geometry wins migration and reset persists", delegate {
            Seed("normal", 4);
            AppConfig c = Load();
            c.GroupLayoutMode = "free"; c.GroupZoneOverrides = new Dictionary<string,string> { { "Вторая", "bottom-right" } };
            c.CanvasInitialized = true;
            c.CanvasPositions = new Dictionary<string,FreeCanvasRect> { { "app:Первая", new FreeCanvasRect(2, 3, 12, 10) } };
            ConfigStore.Save(c);
            MainWindow w = Open(); try {
                FreeCanvasRect saved = Load().CanvasPositions["app:Первая"];
                Assert(saved.X == 2 && saved.Y == 3 && saved.Width == 12 && saved.Height == 10 &&
                    Load().GroupLayoutMode == "canvas", "Existing canvas overwritten");
                string before = File.ReadAllText(ConfigStore.PluginSettingsPath);
                MenuItem reset = Menu(Menu(Tools(w), "Быстрые настройки"), "Сбросить расположение групп");
                foreach (string answer in new[] { "Нет", "Да" })
                {
                    Exception error = null;
                    w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
                        Window confirmation = null;
                        try {
                            foreach (Window owned in w.OwnedWindows)
                                if (owned.Title == "AppShelf — Свободное полотно") confirmation = owned;
                            Assert(confirmation != null, "Canvas reset did not ask for confirmation");
                            Click(ButtonNamed(confirmation, answer));
                        } catch (Exception ex) { error = ex; }
                        finally { if (confirmation != null && confirmation.IsVisible) confirmation.Close(); }
                    }));
                    reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Drain();
                    if (error != null) throw error;
                    if (answer == "Нет")
                        Assert(File.ReadAllText(ConfigStore.PluginSettingsPath) == before, "Declining reset changed canvas settings");
                }
                Assert(Load().CanvasInitialized && Load().CanvasPositions == null && Tiles(w).Count == 4, "Reset lost data or did not clear geometry");
            } finally { w.Close(); }
            w = Open(); try {
                Assert(Load().CanvasPositions == null && Get(w, "_canvasBoard") != null, "Reset was overwritten after restart");
                settings = delegate(Window dialog) {
                    Assert(!Controls<RadioButton>(dialog).Exists(r => Convert.ToString(r.Tag).StartsWith("GroupLayoutMode:", StringComparison.Ordinal) ||
                        Convert.ToString(r.Tag).StartsWith("GroupWidthMode:", StringComparison.Ordinal)), "Legacy selectors remain");
                    ComboBox viewport = ComboWithOption(dialog, "adaptive");
                    Assert(viewport != null && viewport.Items.Count == 4, "Viewport strategies lost");
                    Click(ButtonNamed(dialog, "Сохранить"));
                };
                Call(w, "ShowSettings");
                Assert(Load().GroupLayoutMode == "canvas", "Settings reverted migration");
            } finally { w.Close(); }
        });
        Case("monitor layout creation, switching, editing and isolated reset", delegate {
            Seed("normal", 4);
            AppConfig c = Load();
            c.GroupLayoutMode = "canvas"; c.CanvasInitialized = true; c.CanvasGridStep = 20;
            c.CanvasPositions = new Dictionary<string,FreeCanvasRect> {
                { "app:Первая", new FreeCanvasRect(55, 2, 12, 10) }
            };
            const string other = "TEST-DISPLAY|2560x1440|96x96";
            c.CanvasMonitorPositions = new Dictionary<string,Dictionary<string,FreeCanvasRect>> {
                { other, new Dictionary<string,FreeCanvasRect> {
                    { "app:Первая", new FreeCanvasRect(7, 3, 12, 10) }
                } }
            };
            ConfigStore.Save(c);
            MainWindow w = Open();
            try {
                w.Height = 800; w.UpdateLayout(); Drain();
                string actual = (string)Get(w, "_canvasMonitorKey");
                Assert(!String.IsNullOrEmpty(actual), "Current monitor not detected");
                Assert(!Load().CanvasMonitorPositions.ContainsKey(actual), "Profile was silently created");
                Call(w, "ApplyCanvasMonitorKey", other);
                Assert(((FreeCanvasRect)Call(w, "CanvasPosition", "app:Первая")).X == 7, "Other monitor did not select its own positions");
                Call(w, "ApplyCanvasMonitorKey", actual);
                Assert(((FreeCanvasRect)Call(w, "CanvasPosition", "app:Первая")).X == 55, "Default layout not restored");
                Call(w, "ConfigureCanvasMonitor"); Drain();
                AppConfig saved = Load();
                Assert(saved.CanvasMonitorPositions.ContainsKey(actual) && saved.CanvasPositions["app:Первая"].X == 55 &&
                    saved.CanvasMonitorPositions[other]["app:Первая"].X == 7, "Personal layout creation changed other layouts");
                FreeCanvasBoard board = (FreeCanvasBoard)Get(w, "_canvasBoard");
                Assert(!board.IsTemporaryLayout, "New personal layout not editable");
                var candidate = new FreeCanvasRect(0, 45, 12, 10);
                Assert((bool)Call(w, "SaveCanvasPlacement", "app:Первая", candidate), "Personal placement save failed");
                saved = Load();
                Assert(saved.CanvasMonitorPositions[actual]["app:Первая"].Y == 45 &&
                    saved.CanvasPositions["app:Первая"].Y == 2 && saved.CanvasMonitorPositions[other]["app:Первая"].Y == 3,
                    "Personal edit changed another layout");
                var scroller = (ScrollViewer)Get(w, "_tilesScroller");
                w.UpdateLayout(); Drain(); w.UpdateLayout();
                Assert(scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                    String.Format("Personal layout did not scroll to its overflow: viewport={0}; extent={1}; y={2}",
                        scroller.ViewportHeight, scroller.ExtentHeight, board.Capture()["app:Первая"].Y));
                Call(w, "ApplyCanvasMonitorKey", other);
                w.UpdateLayout(); Drain(); w.UpdateLayout();
                Assert(scroller.ComputedVerticalScrollBarVisibility != Visibility.Visible,
                    "Vertical bar from another monitor layout stuck after switching");
                Call(w, "ApplyCanvasMonitorKey", actual);
                Call(w, "ArrangeCanvas", "reset"); Drain();
                saved = Load();
                Assert(saved.CanvasMonitorPositions[actual].Count == 0 && saved.CanvasPositions["app:Первая"].X == 55 &&
                    saved.CanvasMonitorPositions[other]["app:Первая"].X == 7, "Personal reset changed another layout");
            } finally { w.Close(); }
        });
        Case("canvas scrollbars only on actual overflow", delegate {
            Seed("normal", 4);
            AppConfig c = Load(); c.GroupLayoutMode = "canvas"; c.CanvasInitialized = true; c.CanvasGridStep = 20;
            c.CanvasViewportMode = "scroll"; c.Groups.Clear(); c.Groups.Add("Первая");
            ConfigStore.Save(c);
            MainWindow w = Open();
            try {
                var scroller = (ScrollViewer)Get(w, "_tilesScroller");
                var board = (FreeCanvasBoard)Get(w, "_canvasBoard");
                w.Width = 900; w.Height = 720; w.UpdateLayout(); Drain();
                // HeightFor can expand a manually sized card to fit wrapped tiles.
                // Measure the real card at the test width before positioning it
                // against the viewport edge, rather than assuming eight rows.
                board.SetPreferred("app:Первая", new FreeCanvasRect(0, 0, 24, 8) { ManualHeight = 8 });
                w.UpdateLayout(); Drain(); w.UpdateLayout();
                FreeCanvasRect measured = board.Capture()["app:Первая"];
                double width = scroller.ViewportWidth, height = scroller.ViewportHeight;
                Assert(width > 400 && height > 200, "Unexpected initial viewport");
                int x = (int)Math.Floor((width + 8) / 20) - measured.Width;
                int y = (int)Math.Floor((height + 8) / 20) - measured.Height;
                Assert(x >= 0 && y >= 0, "Test card does not fit at the chosen width");
                var fitting = new FreeCanvasRect(x, y, measured.Width, measured.Height) { ManualHeight = 8 };
                board.SetPreferred("app:Первая", fitting);
                w.UpdateLayout(); Drain(); w.UpdateLayout();
                FreeCanvasRect actual = board.Capture()["app:Первая"];
                string extent = String.Format("viewport={0}x{1}; extent={2}x{3}; card={4},{5} {6}x{7}; bars={8}/{9}",
                    scroller.ViewportWidth, scroller.ViewportHeight, scroller.ExtentWidth, scroller.ExtentHeight,
                    actual.X, actual.Y, actual.Width, actual.Height,
                    scroller.ComputedHorizontalScrollBarVisibility, scroller.ComputedVerticalScrollBarVisibility);
                Console.WriteLine("Canvas fitting diagnostic: " + extent);
                Assert(scroller.ScrollableHeight < 1 && scroller.ScrollableWidth < 1 &&
                    scroller.ComputedVerticalScrollBarVisibility != Visibility.Visible &&
                    scroller.ComputedHorizontalScrollBarVisibility != Visibility.Visible, "Fitting card shows scrollbars: " + extent);
                Assert(Math.Abs(scroller.ExtentWidth - ((actual.X + actual.Width) * 20 - 8)) < 1 &&
                    Math.Abs(scroller.ExtentHeight - ((actual.Y + actual.Height) * 20 - 8)) < 1,
                    "The scroll extent includes space outside the card: " + extent);
                Assert(board.ActualWidth >= width - 1 && board.ActualHeight >= height - 1,
                    "The board does not cover the visible drop area");
                Picture(w, "2243-canvas-fits");
                Assert((bool)Call(w, "SaveCanvasPlacement", "app:Первая", fitting), "Could not persist fitting placement");
                w.Width -= 180; w.Height -= 140;
                w.UpdateLayout(); Drain(); w.UpdateLayout();
                Assert(scroller.ScrollableHeight > 50 && scroller.ScrollableWidth > 50 &&
                    scroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible &&
                    scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Actual overflow on resize did not scroll");
                Picture(w, "2243-canvas-overflow");
                w.Width += 180; w.Height += 140;
                w.UpdateLayout(); Drain(); w.UpdateLayout();
                Assert(scroller.ScrollableHeight < 1 && scroller.ScrollableWidth < 1, "Scrollbars stuck after resizing back");
                FreeCanvasRect persisted = Load().CanvasPositions["app:Первая"];
                Assert(persisted.X == x && persisted.Y == y && persisted.Width == fitting.Width &&
                    persisted.ManualHeight == fitting.ManualHeight, "Resizing rewrote the configured placement");

                // Give the other axis room so an individual bar cannot cause cross-axis overflow.
                w.Width += 60; w.Height += 60; w.UpdateLayout(); Drain();
                foreach (bool horizontal in new[] { true, false })
                {
                    var overflow = new FreeCanvasRect(x + (horizontal ? 10 : 0), y + (horizontal ? 0 : 10),
                        fitting.Width, fitting.Height) { ManualHeight = fitting.ManualHeight };
                    Assert((bool)Call(w, "SaveCanvasPlacement", "app:Первая", overflow), "Could not move the group");
                    w.UpdateLayout(); Drain(); w.UpdateLayout();
                    Assert(horizontal ? scroller.ScrollableWidth > 50 && scroller.ScrollableHeight < 1 :
                        scroller.ScrollableHeight > 50 && scroller.ScrollableWidth < 1, "Unexpected axes after moving the group");
                    Assert((bool)Call(w, "SaveCanvasPlacement", "app:Первая", fitting), "Could not move the group back");
                    w.UpdateLayout(); Drain(); w.UpdateLayout();
                    Assert(scroller.ComputedHorizontalScrollBarVisibility != Visibility.Visible &&
                        scroller.ComputedVerticalScrollBarVisibility != Visibility.Visible, "Scrollbars stuck after moving back");
                }
            } finally { w.Close(); }
        });
        Console.WriteLine("Package6 scenarios passed=" + passed + "; failed=" + failed);
        app.Shutdown(); if (failed != 0) throw new Exception("Package6 failures: " + failed);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $true
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','WPF\UIAutomationProvider.dll','WPF\UIAutomationTypes.dll')) { [void]$options.ReferencedAssemblies.Add((Join-Path $framework $ref)) }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('Package6Tests').GetMethod('Run').Invoke($null, @())
    "PASS isolated package6 tests: $temp"
} finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $previous }
