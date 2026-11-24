// Package management UI. Stage 2 does not execute plugin code.
using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppShelf
{
    internal sealed class PluginManagerWindow : Window
    {
        private readonly PluginPackageStore store;
        private readonly StackPanel rows = new StackPanel();
        private readonly TextBlock status = new TextBlock();
        private readonly Button install;
        private bool busy;

        internal PluginManagerWindow(Window owner, PluginPackageStore packages)
        {
            store = packages;
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
            TextBlock phase = Label("На этом этапе плагины не запускаются. Включение сохраняет только выбранное состояние.", 12, FluentUi.Secondary);
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
            Refresh();
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

        private void Refresh()
        {
            rows.Children.Clear();
            try
            {
                var plugins = store.List();
                if (plugins.Count == 0)
                {
                    TextBlock empty = Label("Плагины пока не установлены.", 14, FluentUi.Secondary);
                    empty.Margin = new Thickness(0, 20, 0, 0);
                    rows.Children.Add(empty);
                }
                foreach (InstalledPlugin plugin in plugins) AddRow(plugin);
            }
            catch (Exception error) { status.Text = "Не удалось прочитать список: " + error.Message; }
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
            if (manifest != null) body.Children.Add(Label("Возможности: " + String.Join(", ", manifest.Capabilities), 12, FluentUi.Secondary));
            if (plugin.Error != null) body.Children.Add(Label("Ошибка: " + plugin.Error, 12, Brushes.Salmon));
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
            body.Children.Add(actions);
            CheckBox enabled = new CheckBox { Content = plugin.Enabled ? "Включён" : "Отключён",
                IsChecked = plugin.Enabled, IsEnabled = plugin.Error == null, VerticalAlignment = VerticalAlignment.Center,
                Foreground = FluentUi.Foreground, Margin = new Thickness(0, 0, 12, 4), Tag = "PluginEnabled:" + plugin.Id };
            enabled.Click += delegate
            {
                try { store.SetEnabled(plugin.Id, enabled.IsChecked == true); status.Text = "Состояние сохранено."; }
                catch (Exception error) { status.Text = error.Message; }
                Refresh();
            };
            actions.Children.Add(enabled);
            Button update = ButtonFor("Обновить…", false);
            update.IsEnabled = plugin.Error == null; update.Click += delegate { ChoosePackage(plugin.Id); };
            actions.Children.Add(update);
            Button remove = ButtonFor("Удалить…", false);
            remove.Click += delegate
            {
                if (AppDialog.Show(this, "Удалить плагин " + (manifest == null ? plugin.Id : manifest.Name) +
                    "?\nПользовательские данные плагина останутся на диске.", "AppShelf — Плагины",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try { store.Remove(plugin.Id); status.Text = "Плагин удалён. Данные сохранены."; }
                catch (Exception error) { status.Text = error.Message; }
                Refresh();
            };
            actions.Children.Add(remove);
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
                                "\nВозможности: " + String.Join(", ", candidate.Capabilities) +
                                "\n\nПлагин — сторонняя программа с правами вашей учётной записи. " +
                                "Устанавливайте только пакеты из доверенных источников. " +
                                "После установки или обновления плагин будет отключён.\n\nДоверять этому пакету и установить его?",
                                "AppShelf — Доверие к плагину", MessageBoxButton.YesNo,
                                MessageBoxImage.Warning) == MessageBoxResult.Yes;
                        }));
                    }, expectedId);
                });
                status.Text = manifest == null ? "Установка отменена." : "Установлен " + manifest.Name + " " + manifest.Version + ". Плагин отключён.";
            }
            catch (Exception error) { status.Text = "Не удалось установить пакет: " + error.Message; }
            finally { busy = false; install.IsEnabled = true; rows.IsEnabled = true; Refresh(); }
        }
    }

    public sealed partial class MainWindow
    {
        private void ShowPluginManager(Window owner)
        {
            try
            {
                PluginManagerWindow manager = new PluginManagerWindow(owner, new PluginPackageStore(ConfigStore.DirectoryPath));
                manager.ShowDialog();
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
