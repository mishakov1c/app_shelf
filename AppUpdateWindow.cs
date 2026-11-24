// Manual update flow using the official AppShelf GitHub Releases feed.
// Background checks are a separate decision.
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppShelf
{
    public sealed partial class MainWindow
    {
        private void ShowUpdateWindow(Window owner)
        {
            Window dialog = new Window
            {
                Title = "AppShelf — обновления", Owner = owner ?? this,
                Width = 660, Height = 530, MinWidth = 510, MinHeight = 430,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            DockPanel root = new DockPanel { Margin = new Thickness(20) };
            dialog.Content = root;
            StackPanel top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            TextBlock heading = Text("Обновление AppShelf", 23, WhiteBrush);
            heading.FontWeight = FontWeights.Bold;
            top.Children.Add(heading);
            TextBlock installed = Text("Установлена версия " +
                AppUpdate.InstalledVersionText(), 12, MutedBrush);
            installed.Margin = new Thickness(0, 4, 0, 14);
            top.Children.Add(installed);

            TextBlock help = Text("Источник: github.com/" + AppUpdate.Owner + "/" +
                AppUpdate.Repository + ". Проверяются опубликованные релизы.",
                12, MutedBrush);
            help.TextWrapping = TextWrapping.Wrap;
            top.Children.Add(help);

            CheckBox beta = new CheckBox
            {
                Content = "Проверять бета-версии", Foreground = WhiteBrush,
                IsChecked = _config.CheckBetaUpdates, IsEnabled = _canSave,
                Margin = new Thickness(0, 8, 0, 2)
            };
            top.Children.Add(beta);
            TextBlock betaHelp = Text("Бета-версии могут работать нестабильно. Стабильные обновления остаются доступными.",
                12, MutedBrush);
            betaHelp.TextWrapping = TextWrapping.Wrap;
            betaHelp.Margin = new Thickness(0, 0, 0, 8);
            top.Children.Add(betaHelp);
            ComboBox versions = new ComboBox { Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8), MinHeight = 28 };
            top.Children.Add(versions);

            TextBlock state = Text("Нажми «Проверить», чтобы узнать о новой версии.",
                12, MutedBrush);
            state.TextWrapping = TextWrapping.Wrap;
            state.Margin = new Thickness(0, 0, 0, 10);
            top.Children.Add(state);

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            root.Children.Add(actions);
            Button check = ActionButton("Проверить", false);
            Button install = ActionButton("Скачать и установить", true);
            Button close = ActionButton("Закрыть", false);
            install.IsEnabled = false;
            actions.Children.Add(check);
            actions.Children.Add(install);
            actions.Children.Add(close);
            close.Click += delegate { dialog.Close(); };

            ScrollViewer notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            FluentUi.StyleSettingsScroll(notesScroll);
            root.Children.Add(notesScroll);
            TextBlock notes = Text("Здесь появится список изменений новой версии.",
                12, WhiteBrush);
            notes.TextWrapping = TextWrapping.Wrap;
            notesScroll.Content = notes;

            bool busy = false;
            bool installing = false;
            dialog.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (busy && !installing) e.Cancel = true;
            };
            AppUpdate.Release selected = null;
            Action<bool> setBusy = delegate(bool value)
            {
                busy = value;
                check.IsEnabled = !busy;
                install.IsEnabled = !busy && selected != null;
                close.IsEnabled = !busy;
                beta.IsEnabled = !busy && _canSave;
                versions.IsEnabled = !busy;
            };

            Action<AppUpdate.Release> showRelease = delegate(AppUpdate.Release latest)
            {
                selected = latest;
                if (latest == null) { install.IsEnabled = false; return; }
                state.Text = (latest.IsBeta ? "Доступна бета-версия " : "Доступно обновление до ") + latest.VersionText +
                    " (" + (latest.Size / 1024) + " КБ).";
                notes.Text = "Изменения версии " + latest.VersionText + ":\n\n" + latest.Notes;
                install.IsEnabled = !busy;
            };
            versions.SelectionChanged += delegate
            {
                ComboBoxItem item = versions.SelectedItem as ComboBoxItem;
                if (item != null) showRelease(item.Tag as AppUpdate.Release);
            };
            Action clearVersions = delegate
            {
                versions.Items.Clear();
                versions.Visibility = Visibility.Collapsed;
                selected = null;
            };

            bool syncingBeta = false;
            RoutedEventHandler changeChannel = delegate
            {
                if (syncingBeta || busy) return;
                AppConfig pending = _config.Copy();
                pending.CheckBetaUpdates = beta.IsChecked == true;
                if (!SaveConfigChanges(pending))
                {
                    syncingBeta = true;
                    try { beta.IsChecked = _config.CheckBetaUpdates; }
                    finally { syncingBeta = false; }
                    return;
                }
                clearVersions();
                setBusy(false);
                state.Text = "Канал обновлений изменён. Нажми «Проверить».";
                notes.Text = "";
            };
            beta.Checked += changeChannel;
            beta.Unchecked += changeChannel;

            check.Click += async delegate
            {
                if (busy) return;
                clearVersions();
                setBusy(true);
                bool includeBeta = _config.CheckBetaUpdates;
                state.Text = includeBeta ? "Проверяю стабильные и бета-релизы GitHub…" :
                    "Проверяю стабильный релиз GitHub…";
                notes.Text = "";
                try
                {
                    AppUpdate.Release[] releases = await Task.Run<AppUpdate.Release[]>(
                        delegate { return AppUpdate.CheckAvailable(includeBeta); });
                    if (!dialog.IsVisible) return;
                    string installedVersion = AppUpdate.InstalledVersionText();
                    foreach (AppUpdate.Release latest in releases)
                    {
                        if (AppUpdate.CompareReleaseVersions(latest.VersionText, installedVersion) <= 0)
                            continue;
                        versions.Items.Add(new ComboBoxItem
                        {
                            Content = latest.VersionText + (latest.IsBeta ? " — бета" : " — стабильная"),
                            Tag = latest
                        });
                    }
                    if (versions.Items.Count == 0)
                    {
                        state.Text = "Нет более новой версии в выбранном канале. Последний релиз: " +
                            releases[0].VersionText + ".";
                        notes.Text = releases[0].Notes;
                    }
                    else
                    {
                        versions.Visibility = versions.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                        versions.SelectedIndex = 0;
                    }
                }
                catch (Exception error)
                {
                    ConfigStore.Log(error);
                    if (dialog.IsVisible)
                    {
                        state.Text = "Не удалось проверить обновления: " + error.Message;
                        notes.Text = "Проверьте доступ к GitHub и наличие опубликованного " +
                            "Release с AppShelf.exe. Установленная версия не изменена.";
                    }
                }
                finally { if (dialog.IsVisible) setBusy(false); }
            };

            install.Click += async delegate
            {
                AppUpdate.Release release = selected;
                if (busy || release == null) return;
                if (MessageBox.Show(dialog,
                    "Скачать AppShelf " + release.VersionText + " и установить после " +
                    "закрытия текущей версии?\n\n" +
                    (release.IsBeta ? "Это бета-версия: возможны ошибки и нестабильная работа.\n\n" : "") +
                    "Приложение перезапустится. Пользовательские данные не меняются.",
                    "AppShelf — установка обновления", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                setBusy(true);
                state.Text = "Скачивание обновления…";
                AppUpdate.PreparedUpdate prepared = null;
                try
                {
                    prepared = await Task.Run<AppUpdate.PreparedUpdate>(
                        delegate
                        {
                            return AppUpdate.Download(release, delegate(long received, long total)
                            {
                                dialog.Dispatcher.BeginInvoke(new Action(delegate
                                {
                                    if (dialog.IsVisible)
                                        state.Text = "Скачивание обновления: " +
                                            (received * 100 / total) + "%";
                                }));
                            });
                        });
                    if (!dialog.IsVisible) return;
                    // A verified download alone is not enough: save the existing data
                    // and executable before the external helper replaces AppShelf.exe.
                    bool backupReady = false;
                    while (!backupReady)
                    {
                        if (!EnsureBackupDirectory(dialog))
                        {
                            if (MessageBox.Show(dialog,
                                "Папка резервных копий не выбрана. Установить обновление без копии данных и EXE?",
                                "AppShelf — резервная копия", MessageBoxButton.YesNo,
                                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                                return;
                            break; // Explicit user approval to proceed without a backup.
                        }
                        state.Text = "Создаю и проверяю резервную копию перед обновлением…";
                        AppConfig backupConfig = _config.Copy();
                        string oldVersion = AppUpdate.InstalledVersionText();
                        string oldExecutable = Process.GetCurrentProcess().MainModule.FileName;
                        try
                        {
                            await Task.Run(delegate
                            {
                                BackupAutomation.CreateBeforeUpdate(backupConfig,
                                    oldExecutable, oldVersion, release.VersionText);
                            });
                            backupReady = true;
                        }
                        catch (Exception backupError)
                        {
                            ConfigStore.Log(backupError);
                            MessageBoxResult answer = MessageBox.Show(dialog,
                                "Не удалось сохранить резервную копию:\n" +
                                backupError.Message + "\n\nДа — повторить.\nНет — установить без копии.\nОтмена — отказаться от установки.",
                                "AppShelf — резервная копия", MessageBoxButton.YesNoCancel,
                                MessageBoxImage.Warning);
                            if (answer == MessageBoxResult.Yes) continue;
                            if (answer != MessageBoxResult.No) return;
                            break; // The skip is a deliberate, explicitly confirmed choice.
                        }
                    }
                    state.Text = backupReady
                        ? "Копия сохранена. Запускаю установщик…"
                        : "Установка без резервной копии по твоему подтверждению…";
                    AppUpdate.StartInstaller(prepared);
                    // The staged helper waits for this process to exit before replacing EXE.
                    installing = true;
                    dialog.Close();
                    Application.Current.Shutdown();
                }
                catch (Exception error)
                {
                    ConfigStore.Log(error);
                    if (dialog.IsVisible)
                    {
                        state.Text = "Обновление не установлено: " + error.Message;
                        notes.Text = "Текущий EXE не изменён. Можно повторить попытку.";
                    }
                }
                finally
                {
                    if (!installing) AppUpdate.Discard(prepared);
                    if (dialog.IsVisible) setBusy(false);
                }
            };
            check.IsEnabled = true;
            dialog.ShowDialog();
        }
    }
}
