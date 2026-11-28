// Manual update flow using the official AppShelf GitHub Releases feed.
// Background checks are a separate decision.
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Text.RegularExpressions;

namespace AppShelf
{
    // Render a deliberately small Markdown subset using native WPF elements.
    // Remote notes are text, never HTML/XAML or executable content.
    internal static class ReleaseNotesView
    {
        private static readonly Regex InlinePattern = new Regex(
            @"\x60([^\x60\r\n]+)\x60|\*\*([^*\r\n]+)\*\*|__([^_\r\n]+)__|\*([^*\r\n]+)\*|_([^_\r\n]+)_|\[([^\]\r\n]+)\]\(([^)\s]+)\)",
            RegexOptions.CultureInvariant);
        private static readonly Regex HeadingPattern = new Regex(@"^(#{1,6})\s+(.+)$");
        private static readonly Regex ListPattern = new Regex(@"^(\s*)([-+*]|\d{1,9}[.)])\s+(.+)$");

        internal static bool SafeLink(string value, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                !String.IsNullOrWhiteSpace(uri.Host) && String.IsNullOrEmpty(uri.UserInfo);
        }

        private static void AddInlines(TextBlock block, string value, Brush accent)
        {
            MatchCollection matches = InlinePattern.Matches(value);
            if (matches.Count == 0)
            {
                block.Text = value;
                return;
            }
            int previous = 0;
            foreach (Match match in matches)
            {
                if (match.Index > previous)
                    block.Inlines.Add(new Run(value.Substring(previous, match.Index - previous)));
                if (match.Groups[1].Success)
                    block.Inlines.Add(new Run(match.Groups[1].Value) {
                        FontFamily = new FontFamily("Consolas"), Background = FluentUi.Brush("#222E43") });
                else if (match.Groups[2].Success || match.Groups[3].Success)
                    block.Inlines.Add(new Bold(new Run(match.Groups[2].Success ?
                        match.Groups[2].Value : match.Groups[3].Value)));
                else if (match.Groups[4].Success || match.Groups[5].Success)
                    block.Inlines.Add(new Italic(new Run(match.Groups[4].Success ?
                        match.Groups[4].Value : match.Groups[5].Value)));
                else
                {
                    Uri uri;
                    if (SafeLink(match.Groups[7].Value, out uri))
                    {
                        Hyperlink link = new Hyperlink(new Run(match.Groups[6].Value)) {
                            NavigateUri = uri, Foreground = accent, ToolTip = uri.AbsoluteUri };
                        link.RequestNavigate += delegate(object sender,
                            System.Windows.Navigation.RequestNavigateEventArgs e)
                        {
                            e.Handled = true;
                            Uri safe;
                            if (!SafeLink(e.Uri.AbsoluteUri, out safe)) return;
                            try { Process.Start(new ProcessStartInfo(safe.AbsoluteUri) { UseShellExecute = true }); }
                            catch (Exception error) { ConfigStore.Log(error); }
                        };
                        block.Inlines.Add(link);
                    }
                    else block.Inlines.Add(new Run(match.Value));
                }
                previous = match.Index + match.Length;
            }
            if (previous < value.Length) block.Inlines.Add(new Run(value.Substring(previous)));
        }

        internal static void Render(StackPanel panel, string source, Brush foreground,
            Brush accent, bool markdown)
        {
            panel.Children.Clear();
            if (String.IsNullOrEmpty(source)) return;
            if (!markdown)
            {
                panel.Children.Add(new TextBlock { Text = source, FontSize = 12,
                    Foreground = foreground, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top });
                return;
            }

            bool fenced = false, gap = false;
            foreach (string line in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(new string((char)96, 3), StringComparison.Ordinal))
                {
                    fenced = !fenced;
                    gap = true;
                    continue;
                }
                if (trimmed.Length == 0 && !fenced) { gap = true; continue; }
                Match heading = HeadingPattern.Match(trimmed);
                Match list = ListPattern.Match(line);
                TextBlock block = new TextBlock { Foreground = foreground,
                    FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, gap && panel.Children.Count > 0 ? 7 : 0, 0, 4) };
                gap = false;
                if (fenced)
                {
                    block.FontFamily = new FontFamily("Consolas");
                    block.Background = FluentUi.Brush("#222E43");
                    block.Text = line;
                    panel.Children.Add(block);
                }
                else if (heading.Success)
                {
                    block.FontSize = heading.Groups[1].Length <= 2 ? 16 : 14;
                    block.FontWeight = FontWeights.SemiBold;
                    block.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 9, 0, 5);
                    AddInlines(block, heading.Groups[2].Value, accent);
                    panel.Children.Add(block);
                }
                else if (list.Success)
                {
                    Grid row = new Grid { Margin = new Thickness(
                        Math.Min(48, list.Groups[1].Length * 5), block.Margin.Top, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    string marker = list.Groups[2].Value;
                    TextBlock bullet = new TextBlock { Text = marker.Length == 1 ? "•" : marker,
                        FontSize = 12, Foreground = foreground, Margin = new Thickness(0, 0, 8, 0) };
                    row.Children.Add(bullet);
                    block.Margin = new Thickness(0);
                    Grid.SetColumn(block, 1);
                    AddInlines(block, list.Groups[3].Value, accent);
                    row.Children.Add(block);
                    panel.Children.Add(row);
                }
                else
                {
                    AddInlines(block, trimmed, accent);
                    panel.Children.Add(block);
                }
            }
        }
    }

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
            TextBlock lastCheck = Text("", 12, MutedBrush);
            lastCheck.TextWrapping = TextWrapping.Wrap;
            lastCheck.Margin = new Thickness(0, 0, 0, 8);
            top.Children.Add(lastCheck);

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
            StackPanel notes = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            notesScroll.VerticalContentAlignment = VerticalAlignment.Top;
            notesScroll.Content = notes;
            Action<string> plainNotes = delegate(string value)
            {
                ReleaseNotesView.Render(notes, value, WhiteBrush, AccentBrush, false);
                notesScroll.ScrollToHome();
            };
            Action<string> releaseNotes = delegate(string value)
            {
                ReleaseNotesView.Render(notes, value, WhiteBrush, AccentBrush, true);
                notesScroll.ScrollToHome();
            };
            plainNotes("Здесь появится список изменений новой версии.");

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
                releaseNotes("### Изменения версии " + latest.VersionText + "\n\n" + latest.Notes);
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
                lastCheck.Text = "";
                state.Text = "Канал обновлений изменён. Нажми «Проверить».";
                plainNotes("");
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
                plainNotes("");
                lastCheck.Text = "";
                try
                {
                    AppUpdate.CheckResult result = await Task.Run<AppUpdate.CheckResult>(
                        delegate { return AppUpdate.CheckWithDetails(includeBeta); });
                    if (!dialog.IsVisible) return;
                    lastCheck.Text = result.CheckedUtc.HasValue
                        ? "Последняя успешная проверка: " +
                            result.CheckedUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") +
                            (result.FromCache ? " (из кэша)" : "")
                        : "Успешных проверок этого канала пока нет.";
                    if (result.RetryUtc.HasValue)
                    {
                        state.Text = "GitHub временно ограничил запросы. Следующая проверка доступна после " +
                            result.RetryUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + ".";
                        plainNotes(result.Releases == null
                            ? "Нет сохранённого результата проверки. Установка недоступна."
                            : "По предыдущей проверке последний релиз: " +
                                result.Releases[0].VersionText +
                                ". Для установки нужна новая проверка.");
                        return;
                    }
                    if (result.Error != null)
                    {
                        state.Text = "Не удалось проверить обновления: " + result.Error;
                        plainNotes(result.Releases == null
                            ? "Установка недоступна до успешной проверки."
                            : "По предыдущей проверке последний релиз: " +
                                result.Releases[0].VersionText +
                                ". Для установки нужна новая проверка.");
                        return;
                    }
                    AppUpdate.Release[] releases = result.Releases;
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
                        releaseNotes(releases[0].Notes);
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
                        plainNotes("Проверьте доступ к GitHub и наличие опубликованного " +
                            "Release с AppShelf.exe. Установленная версия не изменена.");
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
                    // Persistent user archives are optional; the installer still
                    // retains its independent staged rollback executable.
                    bool backupRequested = BackupAutomation.BeforeUpdateEnabled(_config);
                    bool backupReady = false;
                    while (backupRequested && !backupReady)
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
                        : (backupRequested ? "Установка без резервной копии по твоему подтверждению…"
                            : "Резервирование перед обновлением выключено. Запускаю установщик…");
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
                        plainNotes("Текущий EXE не изменён. Можно повторить попытку.");
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
