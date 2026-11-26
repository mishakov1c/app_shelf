// Package management and process lifecycle UI.
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows.Threading;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace AppShelf
{
    internal sealed class PluginManagerWindow : Window
    {
        private readonly PluginPackageStore store;
        private readonly PluginRuntime runtime;
        private readonly string backupRoot;
        private readonly Func<InstalledPlugin, Func<Task>> prepareEnable;
        private readonly Action<Window, bool, string> placeWidget;
        private readonly Func<bool> canPlaceWidget;
        private List<PluginBackupItem> recovered = new List<PluginBackupItem>();
        private readonly DispatcherTimer stateTimer;
        private readonly List<Action> stateUpdates = new List<Action>();
        private readonly StackPanel rows = new StackPanel();
        private readonly TextBlock status = new TextBlock();
        private readonly Button install;
        private bool busy;

        internal PluginManagerWindow(Window owner, PluginPackageStore packages, PluginRuntime controller, string selectedBackupRoot = null, Func<InstalledPlugin, Func<Task>> beforeEnable = null,
            Action<Window, bool, string> placement = null, Func<bool> widgetPlacementAvailable = null)
        {
            store = packages;
            runtime = controller;
            backupRoot = selectedBackupRoot;
            prepareEnable = beforeEnable;
            placeWidget = placement;
            canPlaceWidget = widgetPlacementAvailable;
            Owner = owner;
            Title = "AppShelf — Плагины";
            Width = 760; Height = 580; MinWidth = 480; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FluentUi.PrepareWindow(this);
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            Content = root;
            StackPanel heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
            TextBlock title = Label("Плагины", 24, FluentUi.Foreground);
            title.FontWeight = FontWeights.Bold;
            heading.Children.Add(title);
            heading.Children.Add(Label("Устанавливайте дополнительные инструменты из доверенных источников.", 12, FluentUi.Secondary));
            install = ButtonFor("＋ Установить плагин…", true);
            install.MinHeight = 52;
            install.Margin = new Thickness(0, 12, 0, 4);
            install.Tag = "PluginInstall";
            install.Click += delegate { ChoosePackage(null); };
            heading.Children.Add(install);
            heading.Children.Add(Label("Или перетащите файл .asplugin в это окно.", 12, FluentUi.Secondary));
            TextBlock phase = Label("Фоновые плагины запускаются автоматически. Остальные — по требованию.", 12, FluentUi.Secondary);
            phase.Margin = new Thickness(0, 8, 0, 0);
            heading.Children.Add(phase);
            status.Foreground = FluentUi.Secondary; status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 9, 0, 0);
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            ScrollViewer scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            FluentUi.StyleSettingsScroll(scroll); root.Children.Add(scroll);
            AllowDrop = true;
            DragOver += OnDragOver;
            Drop += OnDrop;
            Closing += delegate(object sender, CancelEventArgs e) { if (busy) e.Cancel = true; };
            stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            stateTimer.Tick += delegate { UpdateStates(); };
            Closed += delegate { stateTimer.Stop(); };
            Refresh(); stateTimer.Start();
        }

        private static TextBlock Label(string text, double size, Brush brush)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap };
        }

        private static Button ButtonFor(string text, bool primary)
        {
            return new Button { Content = text, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 4), Background = primary ? FluentUi.Accent : FluentUi.SurfaceRaised,
                Foreground = primary ? FluentUi.Background : FluentUi.Foreground,
                BorderBrush = primary ? FluentUi.Accent : FluentUi.BorderColor, BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand };
        }

        private static string CapabilityLabels(string[] capabilities)
        {
            return String.Join(", ", Array.ConvertAll(capabilities, delegate(string capability)
            {
                switch (capability)
                {
                    case "command": return "команды";
                    case "window": return "собственные окна";
                    case "settings": return "настройки";
                    case "notification": return "уведомления";
                    case "background": return "фоновая работа";
                    case "widget": return "виджеты";
                    default: return capability;
                }
            }));
        }

        // Only the plugin enable control uses this switch; backup consent stays a checkbox.
        private static Style PluginSwitchStyle()
        {
            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory line = new FrameworkElementFactory(typeof(StackPanel));
            line.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            line.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            FrameworkElementFactory track = new FrameworkElementFactory(typeof(Border));
            track.Name = "SwitchTrack";
            track.SetValue(FrameworkElement.WidthProperty, 42.0);
            track.SetValue(FrameworkElement.HeightProperty, 24.0);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            track.SetValue(Border.BackgroundProperty, FluentUi.SurfaceRaised);
            track.SetValue(Border.BorderBrushProperty, FluentUi.Secondary);
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            FrameworkElementFactory knob = new FrameworkElementFactory(typeof(Border));
            knob.Name = "SwitchKnob";
            knob.SetValue(FrameworkElement.WidthProperty, 16.0);
            knob.SetValue(FrameworkElement.HeightProperty, 16.0);
            knob.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 3, 0));
            knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            knob.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            knob.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            knob.SetValue(Border.BackgroundProperty, FluentUi.Secondary);
            track.AppendChild(knob);
            line.AppendChild(track);
            FrameworkElementFactory caption = new FrameworkElementFactory(typeof(ContentPresenter));
            caption.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
            caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            caption.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            line.AppendChild(caption);
            template.VisualTree = line;
            Trigger on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BackgroundProperty, FluentUi.Accent, "SwitchTrack"));
            on.Setters.Add(new Setter(Border.BorderBrushProperty, FluentUi.Accent, "SwitchTrack"));
            on.Setters.Add(new Setter(Border.BackgroundProperty, FluentUi.Background, "SwitchKnob"));
            on.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "SwitchKnob"));
            template.Triggers.Add(on);
            Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, FluentUi.Foreground, "SwitchTrack"));
            focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "SwitchTrack"));
            template.Triggers.Add(focus);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            template.Triggers.Add(disabled);
            Style style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, FluentUi.Foreground));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(ContentControl.ContentProperty, "Плагин выключен"));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "Нажмите, чтобы включить плагин."));
            Trigger checkedLabel = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedLabel.Setters.Add(new Setter(ContentControl.ContentProperty, "Плагин включён"));
            checkedLabel.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "Нажмите, чтобы выключить плагин."));
            style.Triggers.Add(checkedLabel);
            return style;
        }

        private void Refresh()
        {
            stateUpdates.Clear();
            rows.Children.Clear();
            try
            {
                var plugins = store.List();
                try { recovered = store.ReadRecovered(); }
                catch (Exception error)
                {
                    recovered = new List<PluginBackupItem>();
                    status.Text = "Не удалось прочитать восстановленные данные плагинов: " + error.Message;
                }
                if (plugins.Count == 0)
                {
                    TextBlock empty = Label("Плагины пока не установлены.", 14, FluentUi.Secondary);
                    empty.Margin = new Thickness(0, 20, 0, 0);
                    rows.Children.Add(empty);
                }
                foreach (InstalledPlugin plugin in plugins) AddRow(plugin);
                foreach (PluginBackupItem item in recovered)
                {
                    if (plugins.Exists(delegate(InstalledPlugin p) { return p.Id == item.Id; })) continue;
                    TextBlock missing = Label("Из резервной копии: " + item.Name + " · " + item.Version +
                        " (" + item.Id + "). Установите пакет вручную." +
                        (item.Files.Count > 0 ? " Сохранённые данные доступны после установки." : ""), 13, FluentUi.Secondary);
                    missing.Margin = new Thickness(0, 9, 0, 5);
                    rows.Children.Add(missing);
                }
                UpdateStates();
            }
            catch (Exception error) { status.Text = "Не удалось прочитать список: " + error.Message; }
        }

        private void UpdateStates()
        {
            foreach (Action update in stateUpdates) update();
        }

        private async void RunAction(Func<Task> action, string success)
        {
            if (busy) return;
            busy = true; install.IsEnabled = false; rows.IsEnabled = false;
            status.Text = "Выполнение…";
            try { await action(); status.Text = success; }
            catch (Exception error) { status.Text = error.Message; }
            finally { busy = false; install.IsEnabled = true; rows.IsEnabled = true; Refresh(); }
        }

        private void AddRow(InstalledPlugin plugin)
        {
            PluginManifest manifest = plugin.Manifest;
            StackPanel body = new StackPanel();
            Border border = new Border { Child = body, Background = FluentUi.Surface,
                BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(13), Margin = new Thickness(0, 0, 0, 8) };
            border.Tag = "PluginRow:" + plugin.Id;
            rows.Children.Add(border);
            TextBlock name = Label(manifest == null ? plugin.Id : manifest.Name, 17, FluentUi.Foreground);
            name.FontWeight = FontWeights.SemiBold; body.Children.Add(name);
            string detail = plugin.Id;
            if (manifest != null) detail += " · " + manifest.Version + " · API " + manifest.ApiVersion;
            body.Children.Add(Label(detail, 12, FluentUi.Secondary));
            if (manifest != null && !String.IsNullOrWhiteSpace(manifest.Publisher))
                body.Children.Add(Label("Издатель (указан автором): " + manifest.Publisher, 12, FluentUi.Secondary));
            if (manifest != null && !String.IsNullOrWhiteSpace(manifest.Description))
                body.Children.Add(Label(manifest.Description, 13, FluentUi.Foreground));
            if (manifest != null) body.Children.Add(Label("Возможности: " + CapabilityLabels(manifest.Capabilities), 12, FluentUi.Secondary));
            TextBlock liveState = Label("", 12, FluentUi.Secondary);
            liveState.Tag = "PluginState:" + plugin.Id;
            CheckBox enabled = new CheckBox { Style = PluginSwitchStyle(),
                IsChecked = plugin.Enabled, IsEnabled = plugin.Error == null,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 5), Tag = "PluginEnabled:" + plugin.Id };
            System.Windows.Automation.AutomationProperties.SetName(enabled, "Включить плагин " + name.Text);
            enabled.Click += delegate
            {
                bool enable = enabled.IsChecked == true;
                Func<Task> action = delegate { return runtime.SetEnabledAsync(plugin.Id, enable); };
                if (enable && prepareEnable != null)
                {
                    try
                    {
                        action = prepareEnable(plugin);
                        if (action == null) { enabled.IsChecked = false; return; }
                    }
                    catch (Exception error) { enabled.IsChecked = false; status.Text = error.Message; return; }
                }
                RunAction(action, "Состояние сохранено.");
            };
            body.Children.Add(enabled);
            body.Children.Add(liveState);
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
            body.Children.Add(actions);
            Button start = ButtonFor("Запустить", false);
            start.Tag = "PluginStart:" + plugin.Id;
            start.Click += delegate
            {
                bool restart = runtime.State(plugin.Id).Status != "idle";
                RunAction(delegate { return runtime.StartAsync(plugin.Id, restart); }, "Плагин запущен.");
            };
            actions.Children.Add(start);
            if (manifest != null && (Array.IndexOf(manifest.Capabilities, "command") >= 0 ||
                Array.IndexOf(manifest.Capabilities, "window") >= 0))
            {
                Button commands = ButtonFor("Команды…", false);
                commands.Tag = "PluginCommands:" + plugin.Id;
                commands.IsEnabled = plugin.Enabled && plugin.Error == null;
                commands.Click += delegate
                {
                    RunAction(delegate { return PluginUiActions.ChooseAsync(this, runtime, plugin.Id, manifest.Version); },
                        "Готово.");
                };
                actions.Children.Add(commands);
            }
            if (manifest != null && Array.IndexOf(manifest.Capabilities, "settings") >= 0)
            {
                Button settings = ButtonFor("Настройки…", false);
                settings.Tag = "PluginSettings:" + plugin.Id;
                settings.IsEnabled = plugin.Enabled && plugin.Error == null;
                settings.Click += delegate
                {
                    RunAction(delegate { return runtime.OpenSettingsAsync(plugin.Id, manifest.Version); },
                        "Запрос открытия настроек выполнен.");
                };
                actions.Children.Add(settings);
            }
            if (placeWidget != null && manifest != null && Array.IndexOf(manifest.Capabilities, "widget") >= 0)
            {
                foreach (bool indicator in new[] { true, false })
                {
                    bool chosen = indicator;
                    Button place = ButtonFor(chosen ? "Добавить индикатор…" : "Добавить виджет…", false);
                    place.Tag = (chosen ? "PluginAddIndicator:" : "PluginAddWidget:") + plugin.Id;
                    place.IsEnabled = plugin.Enabled && plugin.Error == null && (chosen || canPlaceWidget == null || canPlaceWidget());
                    place.Click += delegate { if (!busy && place.IsEnabled) placeWidget(this, chosen, plugin.Id); };
                    actions.Children.Add(place);
                }
            }
            stateUpdates.Add(delegate
            {
                PluginRuntimeState state = runtime.State(plugin.Id);
                string value;
                Brush color = FluentUi.Secondary;
                if (plugin.Error != null) { value = "Ошибка: " + plugin.Error; color = Brushes.Salmon; }
                else if (state.Status == "running") { value = "Работает"; color = Brushes.LightGreen; }
                else if (state.Status == "starting") value = "Запускается…";
                else if (state.Status == "stopping") value = "Останавливается…";
                else if (state.Status == "error") { value = "Ошибка: " + state.Error; color = Brushes.Salmon; }
                else value = "Остановлен";
                liveState.Text = "Состояние процесса: " + value; liveState.Foreground = color;
                start.Content = state.Status == "idle" ? "Запустить" : "Перезапустить";
                start.IsEnabled = plugin.Enabled && plugin.Error == null &&
                    state.Status != "starting" && state.Status != "stopping";
            });
            Button update = ButtonFor("Обновить…", false);
            update.IsEnabled = plugin.Error == null; update.Click += delegate { ChoosePackage(plugin.Id); };
            actions.Children.Add(update);
            Button remove = ButtonFor("Удалить…", false);
            remove.Click += delegate
            {
                if (AppDialog.Show(this, "Удалить плагин " + (manifest == null ? plugin.Id : manifest.Name) +
                    "?\nПользовательские данные плагина останутся на диске.", "AppShelf — Плагины",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                RunAction(delegate { return runtime.RemoveAsync(plugin.Id); }, "Плагин удалён. Данные сохранены.");
            };
            actions.Children.Add(remove);
            if (manifest != null && plugin.Error == null && manifest.BackupFiles != null && manifest.BackupFiles.Length != 0)
            {
                CheckBox backup = new CheckBox { Content = "Включать данные в резервные копии",
                    IsChecked = store.BackupDataEnabled(plugin.Id), Foreground = FluentUi.Foreground,
                    Margin = new Thickness(0, 8, 0, 4), Tag = "PluginBackup:" + plugin.Id };
                backup.Click += delegate
                {
                    bool enableBackup = backup.IsChecked == true;
                    if (enableBackup && AppDialog.Show(this,
                        "Включать следующие файлы в ручные, автоматические копии и копии перед обновлением?\n\n" +
                        String.Join("\n", manifest.BackupFiles) +
                        "\n\nАрхив может синхронизироваться с облаком. Содержимое не шифруется.",
                        "AppShelf — Данные плагина", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    { backup.IsChecked = false; return; }
                    RunAction(delegate { return Task.Run(delegate { store.SetBackupDataEnabled(plugin.Id, enableBackup); }); },
                        "Настройка резервирования сохранена.");
                };
                body.Children.Add(backup);
            }
            PluginBackupItem saved = recovered.Find(delegate(PluginBackupItem item) { return item.Id == plugin.Id; });
            if (saved != null)
            {
                body.Children.Add(Label("Резервная копия: " + saved.Version + " · " + saved.Files.Count + " файлов" +
                    (saved.Enabled ? " · был включён" : " · был отключён"), 12, FluentUi.Secondary));
                if (saved.Files.Count != 0)
                {
                    Button restore = ButtonFor("Восстановить данные…", false);
                    restore.Margin = new Thickness(0, 10, 6, 4);
                    restore.Tag = "PluginRestore:" + plugin.Id;
                    restore.IsEnabled = manifest != null && plugin.Error == null && manifest.Version == saved.Version;
                    restore.Click += delegate
                    {
                        if (String.IsNullOrWhiteSpace(backupRoot))
                        {
                            AppDialog.Show(this, "Сначала выберите папку резервных копий AppShelf.", "AppShelf — Плагины",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                        if (AppDialog.Show(this, "Остановить и отключить плагин, затем заменить перечисленные файлы сохранёнными данными?\n\n" +
                            String.Join("\n", saved.Files.ConvertAll(delegate(PluginBackupFile file) { return file.Path; }).ToArray()) +
                            "\n\nТекущие файлы будут сохранены в папке резервных копий. Остальные данные не изменятся.",
                            "AppShelf — Восстановление данных плагина", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                            != MessageBoxResult.Yes) return;
                        RunAction(delegate { return runtime.RestoreDataAsync(plugin.Id, saved.Version,
                            BackupAutomation.ValidateRoot(backupRoot)); }, "Данные восстановлены. Плагин отключён; включите его при необходимости.");
                    };
                    body.Children.Add(restore);
                }
            }
        }

        private static string DroppedPackage(IDataObject data)
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Length == 1 &&
                String.Equals(Path.GetExtension(files[0]), ".asplugin", StringComparison.OrdinalIgnoreCase) ? files[0] : null;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = !busy && DroppedPackage(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            string path = DroppedPackage(e.Data);
            if (!busy && path != null) Install(path, null);
        }

        private void ChoosePackage(string expectedId)
        {
            if (busy) return;
            OpenFileDialog picker = new OpenFileDialog { Filter = "Пакеты AppShelf (*.asplugin)|*.asplugin",
                CheckFileExists = true, Multiselect = false, Title = "Выберите пакет плагина" };
            if (picker.ShowDialog(this) == true) Install(picker.FileName, expectedId);
        }

        // Confirmation applies to the validated staged bytes, not to a second read
        // of a source file which may have changed while the dialog was open.
        private async void Install(string path, string expectedId)
        {
            if (busy) return;
            busy = true; install.IsEnabled = false; rows.IsEnabled = false;
            status.Text = "Проверка пакета…";
            try
            {
                PluginManifest manifest = await Task.Run(delegate
                {
                    return store.Install(path, delegate(PluginManifest candidate)
                    {
                        return Dispatcher.Invoke(new Func<bool>(delegate
                        {
                            return AppDialog.Show(this, candidate.Name + " " + candidate.Version +
                                "\nID: " + candidate.Id +
                                "\nИздатель (не проверен): " + (String.IsNullOrWhiteSpace(candidate.Publisher) ? "не указан" : candidate.Publisher) +
                                "\nФайл: " + Path.GetFileName(path) +
                                "\nВозможности: " + CapabilityLabels(candidate.Capabilities) +
                                "\n\nПлагин — сторонняя программа с правами вашей учётной записи. " +
                                "Устанавливайте только пакеты из доверенных источников. " +
                                "После установки или обновления плагин будет отключён.\n\nДоверять этому пакету и установить его?",
                                "AppShelf — Доверие к плагину", MessageBoxButton.YesNo,
                                MessageBoxImage.Warning) == MessageBoxResult.Yes;
                        }));
                    }, expectedId, runtime.BeforePackageCommit,
                        String.IsNullOrWhiteSpace(backupRoot) ? null : BackupAutomation.ValidateRoot(backupRoot), true);
                });
                status.Text = manifest == null ? "Установка отменена." : "Установлен " + manifest.Name + " " + manifest.Version + ". Включите его переключателем в списке.";
            }
            catch (Exception error) { status.Text = "Не удалось установить пакет: " + error.Message; }
            finally { busy = false; install.IsEnabled = true; rows.IsEnabled = true; Refresh(); }
        }
    }

    internal static class PluginUiActions
    {
        internal static string LiteralMenuText(string value)
        {
            // WPF menu headers otherwise interpret underscores as access keys.
            return (value ?? "").Replace("_", "__");
        }

        internal static async Task ChooseAsync(Window owner, PluginRuntime runtime, string id, string version)
        {
            PluginCommand[] commands = await runtime.GetCommandsAsync(id, version);
            if (commands.Length == 0)
            {
                AppDialog.Show(owner, "Плагин не предоставил команд.", "AppShelf — Плагины",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            PluginCommand chosen = commands.Length == 1 ? commands[0] : null;
            if (chosen == null)
            {
                Window dialog = new Window { Owner = owner, Title = "AppShelf — Команды плагина",
                    Width = 420, Height = 360, MinWidth = 300, MinHeight = 220,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner };
                FluentUi.PrepareWindow(dialog);
                StackPanel list = new StackPanel { Margin = new Thickness(15) };
                ScrollViewer scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                FluentUi.StyleSettingsScroll(scroll);
                dialog.Content = scroll;
                foreach (PluginCommand command in commands)
                {
                    PluginCommand item = command;
                    Button button = new Button { Content = new TextBlock { Text = item.Label, TextWrapping = TextWrapping.Wrap },
                        Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6),
                        Foreground = FluentUi.Foreground, Background = FluentUi.SurfaceRaised,
                        BorderBrush = FluentUi.BorderColor, MinHeight = 34 };
                    button.Click += delegate { chosen = item; dialog.DialogResult = true; };
                    list.Children.Add(button);
                }
                if (dialog.ShowDialog() != true) return;
            }
            await runtime.InvokeAsync(id, version, chosen.Id);
        }
    }

    // The only special migration is an explicit user-approved transition from the
    // historical built-in monitor to its separately installed replacement.
    internal static class NetworkPluginMigration
    {
        internal const string Id = "appshelf.network";
        [DataContract]
        private sealed class Seed
        {
            [DataMember(Name = "showNetwork")] public bool Enabled;
            [DataMember(Name = "networkCheck1Mode", EmitDefaultValue = false)] public string Mode1;
            [DataMember(Name = "networkCheck1Target", EmitDefaultValue = false)] public string Target1;
            [DataMember(Name = "networkCheck2Mode", EmitDefaultValue = false)] public string Mode2;
            [DataMember(Name = "networkCheck2Target", EmitDefaultValue = false)] public string Target2;
        }

        internal static AppConfig Prepare(AppConfig source, string version, IEnumerable<string> keys, string directory)
        {
            AppConfig next = source.Copy();
            if (next.NetworkPluginMigrated) return next;
            if (next.WidgetIndicators == null) next.WidgetIndicators = new List<PluginWidgetPlacementConfig>();
            foreach (string key in keys)
            {
                if (key != "network1" && key != "network2" && key != "route") continue;
                if (next.WidgetIndicators.Exists(delegate(PluginWidgetPlacementConfig item) {
                    return item.PluginId == Id && item.WidgetId == key;
                })) continue;
                if (next.WidgetIndicators.Count >= 6)
                    throw new InvalidOperationException("Недостаточно места для переноса сетевых индикаторов. Освободите место в нижней панели и повторите.");
                string title = key == "route" ? "Сеть" :
                    LegacyNetworkTargets.Label(LegacyNetworkTargets.Resolve(source, key == "network1" ? 1 : 2), key == "network1" ? 1 : 2);
                if (title.Length > 100) title = title.Substring(0, 99) + "…";
                next.WidgetIndicators.Add(new PluginWidgetPlacementConfig {
                    PluginId = Id, WidgetId = key, Version = version, Title = title, Size = "indicator"
                });
            }
            PluginWidgetGroupConfig.Validate(next.WidgetGroups, next.WidgetIndicators);
            string path = Path.Combine(directory, "network-settings.json");
            if (!File.Exists(path))
            {
                LegacyNetworkTargets.Target target; string error;
                if (!LegacyNetworkTargets.TryParse(source.NetworkCheck1Mode, source.NetworkCheck1Target, 1, out target, out error) ||
                    !LegacyNetworkTargets.TryParse(source.NetworkCheck2Mode, source.NetworkCheck2Target, 2, out target, out error))
                    throw new InvalidDataException(error);
                string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(Seed)).WriteObject(stream, new Seed {
                            Enabled = source.ShowNetwork, Mode1 = source.NetworkCheck1Mode, Target1 = source.NetworkCheck1Target,
                            Mode2 = source.NetworkCheck2Mode, Target2 = source.NetworkCheck2Target
                        });
                        stream.Flush(true);
                    }
                    // Never overwrite a plugin's independently created or restored settings.
                    try { File.Move(temp, path); }
                    catch (IOException) { if (!File.Exists(path)) throw; }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            next.NetworkPluginMigrated = true;
            return next;
        }
    }

    public sealed partial class MainWindow
    {
        private PluginRuntime _pluginRuntime;
        private DispatcherTimer _pluginHealthTimer;
        private DispatcherTimer _pluginNoticeTimer;
        private readonly Queue<PluginDelivery> _pluginNotices = new Queue<PluginDelivery>();
        private bool _pluginMenuBusy;
        private bool _pluginRuntimeStopping;

        private Func<Task> PrepareNetworkPluginEnable(Window owner, InstalledPlugin plugin)
        {
            PluginRuntime runtime = GetPluginRuntime();
            if (plugin.Id != NetworkPluginMigration.Id || _config.NetworkPluginMigrated)
                return delegate { return runtime.SetEnabledAsync(plugin.Id, true); };
            if (!_canSave) { ShowSaveDisabled(); return null; }
            foreach (string capability in new[] { "background", "widget", "settings", "command" })
                if (Array.IndexOf(plugin.Manifest.Capabilities, capability) < 0)
                    throw new InvalidDataException("Пакет не поддерживает перенос прежних сетевых проверок.");
            if (AppDialog.Show(owner, "Перенести прежние настройки сетевых проверок в плагин «Сеть»?\n\n" +
                "Существующий файл настроек плагина не будет заменён. Сетевые индикаторы перейдут в панель индикаторов плагинов; " +
                "встроенный монитор больше не входит в ядро. Настройки прежней версии сохранятся для отката.",
                "AppShelf — перенос сети", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return null;
            List<string> keys = new List<string>();
            if (_config.ShowNetwork)
            {
                string mode = NormalizeBottomPanelMode(_config.BottomPanelMode);
                if (mode == "compact") keys.AddRange(BottomPanelFavorites(_config));
                else if (mode != "categories" || BottomPanelCategories(_config).Contains("network"))
                    keys.AddRange(new[] { "network1", "network2", "route" });
            }
            PluginPackageStore store = new PluginPackageStore(ConfigStore.DirectoryPath);
            string directory = store.GetDataDirectory(plugin.Id);
            // Validate capacity and seed data before launching the replacement.
            // Only commit placement/marker after the plugin successfully starts.
            NetworkPluginMigration.Prepare(_config, plugin.Manifest.Version, keys, directory);
            return async delegate
            {
                Exception failure = null;
                try
                {
                    await runtime.SetEnabledAsync(plugin.Id, true);
                    // Rebase on live config: geometry/reminder timers may have saved
                    // unrelated fields while the plugin process was starting.
                    AppConfig pending = NetworkPluginMigration.Prepare(_config, plugin.Manifest.Version, keys, directory);
                    if (!SaveConfigChanges(pending, false))
                        throw new IOException("Не удалось сохранить раскладку после переноса сети.");
                }
                catch (Exception original) { failure = original; }
                // C# 5 does not permit await inside a catch block.
                if (failure != null)
                {
                    try { await runtime.SetEnabledAsync(plugin.Id, false); }
                    catch (Exception rollback)
                    {
                        ConfigStore.Log(failure);
                        ConfigStore.Log(rollback);
                        throw new IOException("Перенос не завершён, не удалось отключить плагин: " + rollback.Message, failure);
                    }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }
                if (_pluginWidgets != null) _pluginWidgets.RestoreLayout(_config.WidgetGroups, _config.WidgetIndicators);
                UpdateMonitorVisibility();
                RenderTiles();
                if (_pluginWidgets != null && !_pluginRuntimeStopping)
                    await _pluginWidgets.RefreshVisibleAsync(
                        delegate { return !_pluginRuntimeStopping && WidgetsInFreeLayout() && IsVisible && WindowState != WindowState.Minimized; },
                        delegate { return !_pluginRuntimeStopping && IsVisible && WindowState != WindowState.Minimized; });
            };
        }

        private PluginRuntime GetPluginRuntime()
        {
            if (_pluginRuntime == null)
                _pluginRuntime = new PluginRuntime(new PluginPackageStore(ConfigStore.DirectoryPath));
            return _pluginRuntime;
        }

        private async void StartPluginRuntime()
        {
            if (_pluginHealthTimer != null) return;
            PluginRuntime runtime = GetPluginRuntime();
            _pluginHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _pluginHealthTimer.Tick += async delegate
            {
                try
                {
                    List<PluginDelivery> deliveries = await runtime.CheckHealthAsync();
                    if (_pluginRuntimeStopping) return;
                    if (_pluginWidgets != null)
                    {
                        _pluginWidgets.InvalidateStopped();
                        await _pluginWidgets.RefreshVisibleAsync(delegate { return !_pluginRuntimeStopping && WidgetsInFreeLayout() && IsVisible && WindowState != WindowState.Minimized; },
                            delegate { return !_pluginRuntimeStopping && IsVisible && WindowState != WindowState.Minimized; });
                        if (_pluginRuntimeStopping) return;
                    }
                    foreach (PluginDelivery delivery in deliveries)
                        if (_pluginNotices.Count < 12) _pluginNotices.Enqueue(delivery);
                    if (_pluginNotices.Count > 0 && _pluginNoticeTimer != null && !_pluginNoticeTimer.IsEnabled)
                    {
                        DeliverPluginNotice();
                        if (_pluginNotices.Count > 0) _pluginNoticeTimer.Start();
                    }
                }
                catch (Exception error) { ConfigStore.Log(error); }
            };
            _pluginNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _pluginNoticeTimer.Tick += delegate { DeliverPluginNotice(); };
            _pluginHealthTimer.Start();
            try
            {
                await runtime.StartBackgroundsAsync();
                if (!_pluginRuntimeStopping && _pluginWidgets != null)
                    await _pluginWidgets.RefreshVisibleAsync(delegate { return !_pluginRuntimeStopping && WidgetsInFreeLayout() && IsVisible && WindowState != WindowState.Minimized; },
                            delegate { return !_pluginRuntimeStopping && IsVisible && WindowState != WindowState.Minimized; });
            }
            catch (Exception error) { ConfigStore.Log(error); }
        }

        private void StopPluginRuntime()
        {
            _pluginRuntimeStopping = true;
            if (_pluginWidgets != null) _pluginWidgets.Clear();
            if (_pluginHealthTimer != null) _pluginHealthTimer.Stop();
            if (_pluginNoticeTimer != null) _pluginNoticeTimer.Stop();
            _pluginNotices.Clear();
            if (_pluginRuntime != null) _pluginRuntime.Dispose();
        }

        private void DeliverPluginNotice()
        {
            if (_pluginRuntimeStopping) return;
            while (_pluginNotices.Count != 0)
            {
                PluginDelivery item = _pluginNotices.Dequeue();
                PluginRuntimeState state = GetPluginRuntime().State(item.Id);
                if (state.Status != "running" || state.Version != item.Version || state.ProcessId != item.ProcessId) continue;
                string title = "AppShelf · " + item.Name + " · " + item.Notice.Title;
                if (title.Length > 63) title = title.Substring(0, 63);
                string message = item.Notice.Message;
                if (message.Length > 255) message = message.Substring(0, 254) + "…";
                try
                {
                    if (_trayIcon != null && _trayIcon.Visible && _trayIcon.Icon != null)
                        _trayIcon.ShowBalloonTip(6000, title, message, Forms.ToolTipIcon.Info);
                    else ShowStatusMessage(title + ": " + message);
                }
                catch (Exception error) { ConfigStore.Log(error); }
                // Keep the timer running for one more tick to guarantee a gap even
                // when a new batch arrives just after the last item was shown.
                if (_pluginNoticeTimer != null) _pluginNoticeTimer.Start();
                return;
            }
            if (_pluginNoticeTimer != null) _pluginNoticeTimer.Stop();
        }

        private MenuItem CreatePluginToolsMenu(ContextMenu owner)
        {
            MenuItem menu = new MenuItem { Header = "Плагины", Icon = FluentUi.MenuGlyph("puzzle"),
                Visibility = Visibility.Collapsed };
            int revision = 0;
            // Refresh from storage each time the main menu opens. Do not launch a
            // plugin to discover its commands just because the user opens a menu.
            owner.Closed += delegate { revision++; };
            owner.Opened += async delegate
            {
                int current = ++revision;
                menu.Items.Clear(); menu.Visibility = Visibility.Collapsed;
                try
                {
                    List<InstalledPlugin> installed = await Task.Run(delegate
                    {
                        PluginPackageStore store = new PluginPackageStore(ConfigStore.DirectoryPath);
                        return store.HasInstalledDirectory ? store.List() : new List<InstalledPlugin>();
                    });
                    if (current != revision || !owner.IsOpen || _pluginRuntimeStopping) return;
                    PopulatePluginTools(menu, installed);
                }
                catch (Exception error)
                {
                    if (current != revision) return;
                    ConfigStore.Log(error);
                }
            };
            return menu;
        }

        private MenuItem CreatePluginMenuItem(InstalledPlugin plugin, PluginRuntimeState state)
        {
            if (!plugin.Enabled || plugin.Error != null || plugin.Manifest == null) return null;
            string id = plugin.Id, version = plugin.Manifest.Version;
            bool commands = Array.IndexOf(plugin.Manifest.Capabilities, "command") >= 0 ||
                Array.IndexOf(plugin.Manifest.Capabilities, "window") >= 0;
            bool settings = Array.IndexOf(plugin.Manifest.Capabilities, "settings") >= 0;
            if (!commands && !settings) return null;
            MenuItem group = new MenuItem { Header = PluginUiActions.LiteralMenuText(plugin.Manifest.Name), ToolTip = id };
            if (!commands)
            {
                group.Click += delegate { RunPluginMenuAction(delegate { return GetPluginRuntime().OpenSettingsAsync(id, version); }); };
                return group;
            }

            bool known = state.Status == "running" && state.Version == version && state.Commands != null;
            if (known && state.Commands.Length == 1)
            {
                string commandId = state.Commands[0].Id;
                group.Click += delegate { RunPluginMenuAction(delegate { return GetPluginRuntime().InvokeAsync(id, version, commandId); }); };
            }
            else if (known && state.Commands.Length > 1)
            {
                foreach (PluginCommand command in state.Commands)
                {
                    string commandId = command.Id;
                    MenuItem item = new MenuItem { Header = PluginUiActions.LiteralMenuText(command.Label) };
                    item.Click += delegate { RunPluginMenuAction(delegate { return GetPluginRuntime().InvokeAsync(id, version, commandId); }); };
                    group.Items.Add(item);
                }
            }
            else if (known && settings)
            {
                group.Click += delegate { RunPluginMenuAction(delegate { return GetPluginRuntime().OpenSettingsAsync(id, version); }); };
            }
            else
            {
                // An idle plugin has no known command list until its first explicit
                // activation. ChooseAsync opens one command or presents a chooser.
                group.Click += delegate { RunPluginMenuAction(delegate { return PluginUiActions.ChooseAsync(this, GetPluginRuntime(), id, version); }); };
            }
            return group;
        }

        private void PopulatePluginTools(MenuItem menu, List<InstalledPlugin> installed)
        {
            menu.Items.Clear();
            PluginRuntime runtime = GetPluginRuntime();
            foreach (InstalledPlugin plugin in installed)
            {
                if (!plugin.Enabled || plugin.Error != null || plugin.Manifest == null) continue;
                MenuItem item = CreatePluginMenuItem(plugin, runtime.State(plugin.Id));
                if (item != null) menu.Items.Add(item);
            }
            menu.Visibility = menu.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void RunPluginMenuAction(Func<Task> action)
        {
            if (_pluginMenuBusy || _pluginRuntimeStopping) return;
            _pluginMenuBusy = true;
            try { await action(); }
            catch (Exception error)
            {
                if (!_pluginRuntimeStopping)
                    AppDialog.Show(this, error.Message, "AppShelf — Плагины", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _pluginMenuBusy = false; }
        }

        private void ShowPluginManager(Window owner)
        {
            try
            {
                PluginManagerWindow manager = null;
                manager = new PluginManagerWindow(owner, new PluginPackageStore(ConfigStore.DirectoryPath), GetPluginRuntime(), _config.BackupDirectory,
                    delegate(InstalledPlugin plugin) { return PrepareNetworkPluginEnable(manager, plugin); },
                    delegate(Window dialogOwner, bool indicator, string id) { ShowPluginWidgetPickerFor(dialogOwner, indicator, id); },
                    delegate { return WidgetsInFreeLayout(); });
                manager.ShowDialog();
                if (_pluginWidgets != null) _pluginWidgets.InvalidateStopped();
            }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                AppDialog.Show(owner, "Не удалось открыть менеджер плагинов:\n" + error.Message,
                    "AppShelf — Плагины", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
