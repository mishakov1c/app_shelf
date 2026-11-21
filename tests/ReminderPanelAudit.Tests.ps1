# Windows PowerShell 5.1 -STA. Independent package audit: isolated storage, no monitors,
# startup registry writes, audio, live websites, or production Program.Main.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5 -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-IndependentAudit-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $text = $text.Replace('if (_networkTimer == null) StartMonitors();', '')
            $pattern = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $pattern).Count -ne 1) { throw 'Config error boundary missing.' }
            $text = [regex]::Replace($text, $pattern, 'IndependentAudit.Errors++;')
            $settingsAnchor = 'bool? savedSettings = dialog.ShowDialog();'
            if ($text.Split(@($settingsAnchor), [StringSplitOptions]::None).Length -ne 2) {
                throw 'Settings modal boundary missing.'
            }
            $text = $text.Replace($settingsAnchor, 'bool? savedSettings = IndependentAudit.Modal(dialog);')
        }
        if ($file -eq 'Reminders.cs') {
            $text = $text.Replace('DateTime.Now', 'IndependentAudit.Now')
            $pattern = 'MessageBox.Show\(this, "[^"\r\n]*\\n" \+ error.Message,\s*"AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $pattern).Count -ne 2) { throw 'Reminder error boundaries missing.' }
            $text = [regex]::Replace($text, $pattern, 'IndependentAudit.Errors++;')
            $text = $text.Replace('dialog.ShowDialog()', 'IndependentAudit.Modal(dialog)')
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $text.Replace('SiteLinks.FetchFavicon(site)', 'false')
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;

public static class IndependentAudit
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed, failed;
    public static int Errors;
    public static DateTime Now = DateTime.Now;
    private static Action<Window> modal;
    private static string Artifacts { get { return Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"); } }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static object Call(MainWindow w, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> result = new List<T>();
        if (root is T) result.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject) result.AddRange(Controls<T>((DependencyObject)child));
        return result;
    }
    private static Button Button(Window w, string text)
    {
        foreach (Button b in Controls<Button>(w)) if (Convert.ToString(b.Content) == text) return b;
        throw new Exception("Missing button: " + text);
    }
    private static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Drain(); }
    public static bool? Modal(Window dialog)
    {
        Exception error = null;
        dialog.Loaded += delegate
        {
            dialog.Dispatcher.BeginInvoke(new Action(delegate
            {
                try { modal(dialog); }
                catch (Exception ex) { error = ex; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
        };
        bool? result = dialog.ShowDialog();
        if (error != null) throw error;
        return result;
    }
    private static ReminderDocument Live(MainWindow w) { return (ReminderDocument)Get(w, "_reminderDocument"); }
    private static ReminderItem Item(string id, bool active, string repeat, DateTime due)
    {
        return new ReminderItem { Id = id, Title = "Audit " + id, Active = active,
            Repeat = repeat, DueLocal = ReminderStore.ToValue(due) };
    }
    private static MainWindow Open()
    {
        ConfigStore.Save(new AppConfig { ShowNetwork = false, ShowWeather = false,
            MinimizeToTray = false, RdpKeepAwake = false });
        ReminderStore.Save(new ReminderDocument());
        MainWindow w = new MainWindow();
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        System.Windows.Forms.NotifyIcon tray = (System.Windows.Forms.NotifyIcon)Get(w, "_trayIcon");
        if (tray != null) { tray.Dispose(); typeof(MainWindow).GetField("_trayIcon", Private).SetValue(w, null); }
        w.ShowActivated = false;
        w.Show(); Drain();
        return w;
    }
    private static void Picture(Window w, string name)
    {
        w.UpdateLayout();
        FrameworkElement view = (FrameworkElement)VisualTreeHelper.GetParent((FrameworkElement)w.Content);
        RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),
            (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(w.Background, null, new Rect(view.RenderSize));
            VisualBrush brush = new VisualBrush(view) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(view.RenderSize) };
            context.DrawRectangle(brush, null, new Rect(view.RenderSize));
        }
        bmp.Render(drawing);
        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (FileStream stream = File.Create(Path.Combine(Artifacts, name + ".png"))) encoder.Save(stream);
    }
    private static void ButtonsInside(Window w)
    {
        FrameworkElement client = (FrameworkElement)w.Content;
        FrameworkElement viewport = (FrameworkElement)VisualTreeHelper.GetParent(client);
        foreach (Button b in Controls<Button>(client))
        {
            if (!b.IsVisible) continue;
            Rect r = b.TransformToAncestor(viewport).TransformBounds(new Rect(b.RenderSize));
            Assert(r.Top >= -0.5 && r.Bottom <= viewport.ActualHeight + 0.5 &&
                r.Left >= -0.5 && r.Right <= viewport.ActualWidth + 0.5,
                "Clipped button " + b.Content + ": " + r + ", viewport=" + viewport.RenderSize);
        }
    }
    private static void Case(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void SelectionReactivation()
    {
        MainWindow w = Open();
        try
        {
            ReminderItem item = Item("selected", false, "once", DateTime.Now.AddMinutes(-1));
            Live(w).Reminders.Add(item); ReminderStore.Save(Live(w));
            Call(w, "ShowRemindersWindow");
            Window list = (Window)Get(w, "_remindersWindow");
            Click(Button(list, "Выбрать"));
            Controls<CheckBox>(list)[0].IsChecked = true;
            Assert((bool)Call(w, "SnoozeReminderOccurrences", new List<string> { item.Id }, 10), "Snooze failed");
            Button delete = (Button)Get(w, "_removeSelectedRemindersButton");
            Assert(!delete.IsEnabled, "Reactivated occurrence remains selected for bulk deletion");
            Assert(Live(w).Reminders.Contains(item) && item.Active, "Active occurrence lost");
        }
        finally { w.Close(); }
    }
    private static void RepeatedDelivery()
    {
        MainWindow w = Open(); DateTime previous = Now;
        try
        {
            Now = new DateTime(2030, 1, 31, 18, 0, 0);
            ReminderItem parent = Item("monthly-parent", true, "monthly", Now);
            parent.RepeatDay = 31;
            Live(w).Reminders.Add(parent); ReminderStore.Save(Live(w));
            Call(w, "CheckDueReminders");
            Assert(Live(w).Reminders.Count == 2, "First delivery did not create exactly one occurrence");
            ReminderItem child = Live(w).Reminders[1]; string id = child.Id, schedule = parent.DueLocal;
            Assert(schedule == "2030-02-28T18:00:00", "Monthly schedule changed");
            foreach (int minutes in new int[] { 10, 30, 60 })
            {
                Window alert = (Window)Get(w, "_reminderAlertWindow");
                Click(Button(alert, minutes == 60 ? "1 ч." : minutes + " мин."));
                Assert(child.Active && child.Id == id && child.ParentId == parent.Id &&
                    Live(w).Reminders.Count == 2 && parent.DueLocal == schedule,
                    "Snooze duplicated occurrence or shifted monthly parent");
                Now = Now.AddMinutes(minutes);
                Call(w, "CheckDueReminders");
                Assert(!child.Active && Live(w).Reminders.Count == 2 &&
                    Get(w, "_reminderAlertWindow") != null, "Redelivery duplicated or lost occurrence");
            }
            Click(Button((Window)Get(w, "_reminderAlertWindow"), "Готово"));
            string warning; bool safe;
            ReminderDocument loaded = ReminderStore.Load(out warning, out safe);
            Assert(safe && loaded.Reminders.Count == 2 && loaded.Reminders[0].DueLocal == schedule &&
                loaded.Reminders[1].Id == id && loaded.Reminders[1].ParentId == parent.Id,
                "Repeated delivery did not survive reload");
        }
        finally { Now = previous; w.Close(); }
    }
    private static void CleanupOpenAlert()
    {
        MainWindow w = Open();
        try
        {
            Live(w).Reminders.Add(Item("daily", true, "daily", DateTime.Now.AddMinutes(-1)));
            ReminderStore.Save(Live(w)); Call(w, "CheckDueReminders");
            Window alert = (Window)Get(w, "_reminderAlertWindow");
            Call(w, "ShowRemindersWindow");
            modal = delegate(Window d)
            {
                if (d.Title.EndsWith("Подтверждение")) { Click(Button(d, "Удалить")); return; }
                foreach (TextBlock t in Controls<TextBlock>(d))
                    if (t.Text.StartsWith("Будет удалено"))
                        Assert(t.Text == "Будет удалено записей: 0", "Cleanup targets an occurrence with an open alert");
            };
            Call(w, "ClearCompletedReminders");
            Click(Button(alert, "10 мин."));
            Assert(Get(w, "_reminderAlertWindow") == null && Live(w).Reminders.Count == 2 &&
                Live(w).Reminders[1].Active, "Cleanup broke snooze in the open alert");
        }
        finally { w.Close(); }
    }
    private static void EditOccurrence()
    {
        MainWindow w = Open();
        try
        {
            ReminderItem item = Item("child", true, "once", DateTime.Now.AddDays(2));
            item.ParentId = "parent"; Live(w).Reminders.Add(item); ReminderStore.Save(Live(w));
            modal = delegate(Window d) { Click(Button(d, "Сохранить")); };
            Call(w, "EditReminder", item);
            string warning; bool safe;
            Assert(ReminderStore.Load(out warning, out safe).Reminders[0].ParentId == "parent",
                "Editing an occurrence discards its parentId on disk");
        }
        finally { w.Close(); }
    }
    private static void Intervals()
    {
        MainWindow w = Open();
        try
        {
            modal = delegate(Window d)
            {
                List<TextBox> boxes = Controls<TextBox>(d);
                boxes[0].Text = "0"; Click(Button(d, "Сохранить"));
                Assert(d.IsVisible, "Invalid quick interval was accepted");
                boxes[0].Text = "5"; boxes[1].Text = "120"; boxes[2].Text = "10080";
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string before = File.ReadAllText(ConfigStore.FilePath); int errors = Errors;
                    Click(Button(d, "Сохранить"));
                    Assert(d.IsVisible && Errors == errors + 1 && File.ReadAllText(ConfigStore.FilePath) == before,
                        "Failed quick-interval save closed dialog or changed data");
                }
                Click(Button(d, "Сохранить"));
            };
            Call(w, "ConfigureQuickSnoozeIntervals", w);
            string warning; bool safe;
            AppConfig config = ConfigStore.Load(out warning, out safe);
            Assert(config.SnoozeQuickMinutes[0] == 5 && config.SnoozeQuickMinutes[1] == 120 &&
                config.SnoozeQuickMinutes[2] == 10080, "Quick intervals were not persisted");
            foreach (int unit in new int[] { 0, 1, 2 })
            {
                int selected = unit;
                modal = delegate(Window d)
                {
                    Controls<ComboBox>(d)[0].SelectedIndex = selected;
                    Controls<TextBox>(d)[0].Text = "10081"; Click(Button(d, "Отложить"));
                    Assert(d.IsVisible, "Excessive custom interval was accepted");
                    Controls<TextBox>(d)[0].Text = "2"; Click(Button(d, "Отложить"));
                };
                int expected = selected == 0 ? 2 : selected == 1 ? 120 : 2880;
                Assert((int)Call(w, "ShowCustomSnoozeInterval", w) == expected, "Incorrect custom units");
            }
            modal = delegate(Window d) { Click(Button(d, "Отмена")); };
            Assert(Call(w, "ShowCustomSnoozeInterval", w) == null, "Custom cancel returned an interval");
        }
        finally { w.Close(); }
    }
    private static void CleanupScopes()
    {
        MainWindow w = Open();
        try
        {
            Live(w).Reminders.Add(Item("old", false, "once", DateTime.Now.AddDays(-40)));
            Live(w).Reminders.Add(Item("week", false, "once", DateTime.Now.AddDays(-8)));
            Live(w).Reminders.Add(Item("recent", false, "once", DateTime.Now.AddDays(-2)));
            Live(w).Reminders.Add(Item("future", true, "once", DateTime.Now.AddDays(2)));
            Live(w).Reminders.Add(Item("parent", true, "weekly", DateTime.Now.AddDays(3)));
            ReminderStore.Save(Live(w)); Call(w, "ShowRemindersWindow");
            modal = delegate(Window d)
            {
                ComboBox scope = Controls<ComboBox>(d)[0];
                foreach (int index in new int[] { 0, 1, 2 })
                {
                    scope.SelectedIndex = index; Drain();
                    string expected = "Будет удалено записей: " + (index == 0 ? 3 : index == 1 ? 2 : 1);
                    Assert(Controls<TextBlock>(d).Exists(delegate(TextBlock t) { return t.Text == expected; }),
                        "Incorrect cleanup count for scope " + index);
                }
                modal = delegate(Window confirmation) { Click(Button(confirmation, "Удалить")); };
                Click(Button(d, "Очистить"));
            };
            Call(w, "ClearCompletedReminders");
            Assert(Live(w).Reminders.Count == 4 &&
                !Live(w).Reminders.Exists(delegate(ReminderItem item) { return item.Id == "old"; }),
                "30-day cleanup removed wrong records");
        }
        finally { w.Close(); }
    }
    private static void PanelSettings()
    {
        MainWindow w = Open();
        try
        {
            modal = delegate(Window d)
            {
                // The first ComboBox controls the lower panel; the appearance section
                // has a separate tile-mode ComboBox with the same 'compact' tag.
                ComboBox combo = Controls<ComboBox>(d)[0];
                foreach (object value in combo.Items)
                {
                    ComboBoxItem option = value as ComboBoxItem;
                    if (option != null && Convert.ToString(option.Tag) == "compact") combo.SelectedItem = option;
                }
                ListBox favorites = Controls<ListBox>(d)[0];
                foreach (ListBoxItem row in favorites.Items) ((CheckBox)row.Content).IsChecked = false;
                for (int i = 0; i < favorites.Items.Count; i++)
                {
                    CheckBox check = (CheckBox)((ListBoxItem)favorites.Items[i]).Content;
                    if (Convert.ToString(check.Tag) != "reminders") continue;
                    check.IsChecked = true; favorites.SelectedIndex = i; break;
                }
                while (favorites.SelectedIndex > 0) Click(Button(d, "↑ Выше"));
                foreach (ListBoxItem row in favorites.Items)
                    if (Convert.ToString(((CheckBox)row.Content).Tag) == "countdown") ((CheckBox)row.Content).IsChecked = true;
                Click(Button(d, "Сохранить"));
            };
            Call(w, "ShowSettings");
            string warning; bool safe;
            AppConfig loaded = ConfigStore.Load(out warning, out safe);
            Assert(loaded.BottomPanelMode == "compact" && loaded.BottomPanelFavorites.Count == 2 &&
                loaded.BottomPanelFavorites[0] == "reminders" && loaded.BottomPanelFavorites[1] == "countdown",
                "Settings dialog lost favorite order or mode");
            WrapPanel items = (WrapPanel)Get(w, "_bottomCompactItems");
            Assert(items.Children.Count == 2 &&
                ((TextBlock)((Button)items.Children[0]).Content).Text == ((TextBlock)Get(w, "_remindersChipText")).Text,
                "Compact display differs from saved favorite order");
            Button expand = (Button)((DockPanel)Get(w, "_bottomCompactRow")).Children[0];
            Click(expand);
            ContextMenu menu = ((Border)Get(w, "_monitorBorder")).ContextMenu;
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            System.Windows.Controls.Primitives.UniformGrid groups =
                (System.Windows.Controls.Primitives.UniformGrid)Get(w, "_bottomCategoryTabs");
            foreach (Button category in groups.Children)
            {
                Click(category);
                WrapPanel details = (WrapPanel)Get(w, "_bottomCategoryDetails");
                if (groups.Children.IndexOf(category) == 2)
                    Assert(details.Visibility == Visibility.Visible && details.Children.Count >= 2, "Time tools absent");
                else Assert(details.Visibility == Visibility.Collapsed, "Disabled category rendered tools");
                Click(category);
                Assert(details.Visibility == Visibility.Collapsed, "Category did not collapse");
            }
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(((WrapPanel)Get(w, "_bottomFullMonitors")).Visibility == Visibility.Collapsed &&
                Convert.ToString(expand.Content) == "Подробнее ▾", "Mode switch retained stale expansion");
            string before = File.ReadAllText(ConfigStore.FilePath); int errors = Errors;
            using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                ((MenuItem)menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(Errors == errors + 1 && File.ReadAllText(ConfigStore.FilePath) == before &&
                ((AppConfig)Get(w, "_config")).BottomPanelMode == "compact", "Failed mode save changed settings");
        }
        finally { w.Close(); }
    }
    private static void DialogLayout(string kind, bool minimum)
    {
        MainWindow w = Open();
        try
        {
            modal = delegate(Window d)
            {
                if (minimum) { d.Width = d.MinWidth; d.Height = d.MinHeight; }
                Drain(); Picture(d, kind + (minimum ? "-minimum" : "-default")); ButtonsInside(d);
                if (minimum && (kind == "quick" || kind == "confirm")) ScrollInput(d, kind);
            };
            if (kind == "quick") Call(w, "ConfigureQuickSnoozeIntervals", w);
            else if (kind == "custom") Call(w, "ShowCustomSnoozeInterval", w);
            else if (kind == "cleanup")
            {
                Call(w, "ShowRemindersWindow");
                Call(w, "ClearCompletedReminders");
            }
            else if (kind == "confirm") Call(w, "ConfirmReminderAction", w,
                "Удалить напоминание «" + new string('Ж', 200) + "»?", "Удалить");
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    ReminderItem item = Item("alert-" + i, true, "once", DateTime.Now.AddMinutes(-1));
                    item.Title = new string('Ж', 100); Live(w).Reminders.Add(item);
                }
                ReminderStore.Save(Live(w)); Call(w, "CheckDueReminders");
                Window alert = (Window)Get(w, "_reminderAlertWindow");
                if (minimum) { alert.Width = alert.MinWidth; alert.Height = alert.MinHeight; }
                Drain(); Picture(alert, kind + (minimum ? "-minimum" : "-default")); ButtonsInside(alert);
                if (minimum) ScrollInput(alert, "due alert");
            }
        }
        finally { w.Close(); }
    }
    private static T Visual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T) return (T)root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            T found = Visual<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
    private static void ScrollInput(Window dialog, string name)
    {
        List<ScrollViewer> viewers = Controls<ScrollViewer>(dialog);
        Assert(viewers.Count == 1, name + ": scroll viewport missing");
        ScrollViewer scroll = viewers[0];
        scroll.ApplyTemplate(); scroll.UpdateLayout(); Drain();
        Assert(scroll.ScrollableHeight > 0, name + ": no overflow at minimum size");
        ScrollBar bar = scroll.Template.FindName("PART_VerticalScrollBar", scroll) as ScrollBar;
        Assert(bar != null && bar.ActualWidth > 0 && bar.ActualWidth <= 12.5,
            name + ": missing compact scrollbar");
        Border surface = Visual<Border>(bar);
        SolidColorBrush brush = surface == null ? null : surface.Background as SolidColorBrush;
        Assert(brush != null && brush.Color.R < 100 && brush.Color.G < 100 && brush.Color.B < 120,
            name + ": scrollbar is not dark");
        scroll.ScrollToHome(); Drain();
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.MouseWheelEvent }); Drain();
        Assert(scroll.VerticalOffset > 0, name + ": wheel did not scroll");
        scroll.ScrollToHome(); Drain();
        Track track = bar.Template.FindName("PART_Track", bar) as Track;
        Assert(track != null && track.Thumb != null, name + ": drag thumb missing");
        track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 25) { RoutedEvent = Thumb.DragDeltaEvent }); Drain();
        Assert(scroll.VerticalOffset > 0, name + ": thumb did not scroll");
    }
    private static void PanelLayout(string mode, double scale, double width)
    {
        MainWindow w = Open();
        try
        {
            AppConfig data = (AppConfig)Get(w, "_config");
            data.BottomPanelMode = mode; data.ShowNetwork = true; data.ShowWeather = true;
            data.BottomPanelFavorites = new List<string> { "reminders", "countdown", "weather", "route" };
            Call(w, "UpdateMonitorVisibility");
            w.Width = width * scale; w.Height = 580 * scale;
            ((FrameworkElement)w.Content).LayoutTransform = new ScaleTransform(scale, scale);
            if (mode == "compact") Click((Button)((DockPanel)Get(w, "_bottomCompactRow")).Children[0]);
            if (mode == "categories") Click((Button)((System.Windows.Controls.Primitives.UniformGrid)Get(w, "_bottomCategoryTabs")).Children[2]);
            Drain();
            Border border = (Border)Get(w, "_monitorBorder");
            FrameworkElement content = (FrameworkElement)w.Content;
            Rect r = border.TransformToAncestor(content).TransformBounds(new Rect(border.RenderSize));
            Assert(r.Bottom <= content.ActualHeight + 0.5 && r.Left >= 0 &&
                r.Right <= content.ActualWidth + 0.5, "Bottom panel outside client");
            Picture(w, "panel-" + mode + "-" + scale.ToString(CultureInfo.InvariantCulture) + "-" + width);
        }
        finally { w.Close(); }
    }
    private static void ReminderChipShowsNearest()
    {
        MainWindow w = Open();
        try
        {
            TextBlock chip = (TextBlock)Get(w, "_remindersChipText");
            Assert(chip.Text == "Нет напоминаний", "Empty reminders chip should say 'Нет напоминаний'");
            Assert(chip.TextWrapping == TextWrapping.Wrap && chip.MaxWidth <= 310,
                "Detailed reminder chip must wrap long text");

            DateTime baseDate = Now.Date.AddDays(4).AddHours(9).AddMinutes(30);
            ReminderItem later = Item("later", true, "once", baseDate.AddHours(5));
            later.Title = "Позднее напоминание";
            ReminderItem nearest = Item("closest", true, "monthly", baseDate);
            nearest.Title = "Оплатить кладовую и сохранить квитанцию";
            ReminderItem inactive = Item("inactive", false, "once", baseDate.AddDays(-2));
            inactive.Title = "Неактивное напоминание";
            ReminderItem invalid = Item("invalid", true, "once", baseDate.AddDays(-3));
            invalid.DueLocal = "bad-date";
            Live(w).Reminders.AddRange(new ReminderItem[] { later, inactive, invalid, nearest });
            Call(w, "UpdateReminderChip");
            Assert(chip.Text == nearest.Title + " · " + baseDate.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture),
                "Reminder chip did not show nearest active title and date");
            Assert(!chip.Text.StartsWith("Напоминания:", StringComparison.Ordinal),
                "Reminder chip still shows the old count prefix");

            nearest.Active = false;
            Call(w, "UpdateReminderChip");
            Assert(chip.Text == later.Title + " · " + baseDate.AddHours(5).ToString("dd.MM HH:mm", CultureInfo.CurrentCulture),
                "Reminder chip failed to update after the nearest reminder was disabled");

            later.Active = false;
            Call(w, "UpdateReminderChip");
            Assert(chip.Text == "Нет напоминаний", "No active reminders should display empty state");
        }
        finally { w.Close(); }
    }
    public static void Run()
    {
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        FluentUi.Install(app);
        Assert(ConfigStore.DirectoryPath == Path.Combine(Artifacts, "AppShelf"), "Isolation failed");
        Case("three snooze/redelivery cycles preserve identity and monthly anchor", RepeatedDelivery);
        Case("selected history reactivated by snooze is no longer deletable", SelectionReactivation);
        Case("cleanup preserves occurrences awaiting alert acknowledgement", CleanupOpenAlert);
        Case("edit preserves occurrence parentId", EditOccurrence);
        Case("quick/custom intervals, validation, cancellation and save failure", Intervals);
        Case("cleanup scopes preserve active and recent records", CleanupScopes);
        Case("panel settings order, categories, switching and save failure", PanelSettings);
        Case("nearest reminder title/date and empty state on bottom button", ReminderChipShowsNearest);
        foreach (string kind in new string[] { "quick", "custom", "confirm", "alert", "cleanup" })
            foreach (bool minimum in new bool[] { false, true })
            {
                string k = kind; bool m = minimum;
                Case("dialog bounds " + k + " minimum=" + m, delegate { DialogLayout(k, m); });
            }
        foreach (string mode in new string[] { "compact", "categories", "detailed" })
            foreach (double scale in new double[] { 1, 1.25, 1.5 })
                foreach (double width in new double[] { 510, 850 })
                {
                    string m = mode; double s = scale, size = width;
                    Case("panel " + m + " content-scale=" + s + " DIP-width=" + size,
                        delegate { PanelLayout(m, s, size); });
                }
        Console.WriteLine("Independent audit passed=" + passed + "; failed=" + failed);
        app.Shutdown();
        if (failed != 0) throw new Exception("Independent audit failures: " + failed);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $true
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $ref))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    "Audit artifacts: $temp"
    $result.CompiledAssembly.GetType('IndependentAudit').GetMethod('Run').Invoke($null, @())
}
finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $previous }
