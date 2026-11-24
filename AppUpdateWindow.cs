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
                AppUpdate.InstalledVersion().ToString(3), 12, MutedBrush);
            installed.Margin = new Thickness(0, 4, 0, 14);
            top.Children.Add(installed);

            TextBlock help = Text("Источник: github.com/" + AppUpdate.Owner + "/" +
                AppUpdate.Repository + ". Проверяются опубликованные стабильные релизы.",
                12, MutedBrush);
            help.TextWrapping = TextWrapping.Wrap;
            top.Children.Add(help);

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
            };

            check.Click += async delegate
            {
                if (busy) return;
                selected = null;
                setBusy(true);
                state.Text = "Получаю информацию о стабильном релизе GitHub…";
                notes.Text = "";
                try
                {
                    AppUpdate.Release latest = await Task.Run<AppUpdate.Release>(
                        delegate { return AppUpdate.Check(); });
                    if (!dialog.IsVisible) return;
                    if (latest.Version.CompareTo(AppUpdate.InstalledVersion()) <= 0)
                    {
                        state.Text = "Установлена актуальная версия. Последний релиз: " +
                            latest.VersionText + ".";
                        notes.Text = latest.Notes;
                    }
                    else
                    {
                        selected = latest;
                        state.Text = "Доступно обновление до " + latest.VersionText +
                            " (" + (latest.Size / 1024) + " КБ).";
                        notes.Text = "Изменения версии " + latest.VersionText + ":\n\n" +
                            latest.Notes;
                    }
                }
                catch (Exception error)
                {
                    ConfigStore.Log(error);
                    if (dialog.IsVisible)
                    {
                        state.Text = "Не удалось проверить обновления: " + error.Message;
                        notes.Text = "Проверьте доступ к GitHub и наличие опубликованного " +
                            "стабильного Release с AppShelf.exe. Установленная версия не изменена.";
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
                    "Приложение перезапустится. Пользовательские данные не меняются.",
                    "AppShelf — установка обновления", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                setBusy(true);
                state.Text = "Скачивание обновления…";
                try
                {
                    AppUpdate.PreparedUpdate prepared = await Task.Run<AppUpdate.PreparedUpdate>(
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
                        ReminderDocument backupReminders = BackupAutomation.CopyReminders(_reminderDocument);
                        string oldVersion = AppUpdate.InstalledVersion().ToString(3);
                        string oldExecutable = Process.GetCurrentProcess().MainModule.FileName;
                        try
                        {
                            await Task.Run(delegate
                            {
                                BackupAutomation.CreateBeforeUpdate(backupConfig, backupReminders,
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
                finally { if (dialog.IsVisible) setBusy(false); }
            };
            check.IsEnabled = true;
            dialog.ShowDialog();
        }
    }
}
