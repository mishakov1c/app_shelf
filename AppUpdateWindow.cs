// Manual installation and optional background release checks.
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
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
        private DispatcherTimer _automaticUpdateTimer;
        private CancellationTokenSource _automaticUpdateCancel;
        private AppUpdate.AutoState _automaticUpdateState;
        private AppUpdate.AutoBatch _automaticUpdateBatch;
        private bool _automaticUpdateBusy, _automaticUpdateFirst = true, _automaticUpdateStopped;
        private int _automaticPluginGeneration;
        private bool _automaticPluginManagerOpen, _automaticInventoryChanged;
        private Window _automaticUpdateWindow;
        private Action _refreshAutomaticUpdateWindow;
        private string AutomaticUpdateStatePath { get { return Path.Combine(ConfigStore.DirectoryPath, "update-monitor.json"); } }

        private static string AutomaticPolicy(AppConfig c)
        {
            return (c.AutoCheckCoreUpdates != false) + "|" + (c.AutoCheckPluginUpdates != false) + "|" +
                (c.AutoCheckUpdatesAtStartup != false) + "|" + (c.AutoNotifyUpdates != false) + "|" +
                AppUpdate.AutoState.Interval(c.AutoUpdateIntervalHours) + "|" + c.CheckBetaUpdates + "|" + c.AutoCheckPluginPreviews;
        }

        private void EnsureAutomaticUpdateState()
        {
            if (_automaticUpdateState != null) return;
            _automaticUpdateState = _canSave ? AppUpdate.LoadAutoState(AutomaticUpdateStatePath) : new AppUpdate.AutoState();
            _automaticUpdateState.EnsureAnchor(DateTime.UtcNow);
            SaveAutomaticUpdateState();
        }

        private void SaveAutomaticUpdateState()
        {
            if (!_canSave || _automaticUpdateState == null) return;
            try { AppUpdate.SaveAutoState(AutomaticUpdateStatePath, _automaticUpdateState); }
            catch (Exception error) { ConfigStore.Log(error); }
        }

        private void StartAutomaticUpdateMonitor()
        {
            if (_automaticUpdateTimer != null || _automaticUpdateStopped || !_canSave) return;
            EnsureAutomaticUpdateState();
            _automaticUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _automaticUpdateTimer.Tick += async delegate
            {
                if (_automaticUpdateBusy || _automaticUpdateStopped || !_canSave || _automaticPluginManagerOpen ||
                    (_config.AutoCheckCoreUpdates == false && _config.AutoCheckPluginUpdates == false)) return;
                if (_automaticUpdateState.Due(DateTime.UtcNow, _config.AutoUpdateIntervalHours,
                    _config.AutoCheckUpdatesAtStartup != false, _automaticUpdateFirst, _config.AutoNotifyUpdates != false))
                    await RunAutomaticUpdateCheck(false);
            };
            // Delay startup requests and never block initial rendering.
            _automaticUpdateTimer.Start();
        }

        private void StopAutomaticUpdateMonitor()
        {
            _automaticUpdateStopped = true;
            if (_automaticUpdateTimer != null) _automaticUpdateTimer.Stop();
            if (_automaticUpdateCancel != null) _automaticUpdateCancel.Cancel();
        }

        private void RefreshAutomaticUpdateWindow()
        {
            if (_refreshAutomaticUpdateWindow != null) _refreshAutomaticUpdateWindow();
        }

        private void InvalidateAutomaticPluginResults()
        {
            _automaticPluginGeneration++;
            _automaticInventoryChanged = true;
            if (_automaticUpdateBatch != null)
                _automaticUpdateBatch.Available.RemoveAll(delegate(AppUpdate.UpdateNotice n) { return n.Key != "core"; });
            RefreshAutomaticUpdateWindow();
        }

        private void AutomaticUpdatePolicyChanged()
        {
            _automaticUpdateBatch = null;
            _automaticUpdateFirst = true;
            if (_automaticUpdateState != null)
            {
                _automaticUpdateState.LastAttemptTicks = 0;
                _automaticUpdateState.AnchorTicks = DateTime.UtcNow.Ticks;
                _automaticUpdateState.RetryTicks = 0;
                if (_config.AutoNotifyUpdates == false) _automaticUpdateState.SnoozeTicks = 0;
                SaveAutomaticUpdateState();
            }
            RefreshAutomaticUpdateWindow();
        }

        private async Task RunAutomaticUpdateCheck(bool manual)
        {
            if (_automaticUpdateBusy || _automaticUpdateStopped || (!manual && !_canSave)) return;
            EnsureAutomaticUpdateState();
            AppConfig snapshot = _config.Copy();
            string policy = AutomaticPolicy(snapshot);
            bool core = manual || snapshot.AutoCheckCoreUpdates != false;
            bool plugins = manual || snapshot.AutoCheckPluginUpdates != false;
            int inventory = _automaticPluginGeneration;
            CancellationTokenSource cancel = new CancellationTokenSource();
            _automaticUpdateCancel = cancel;
            _automaticUpdateBusy = true;
            _automaticUpdateFirst = false;
            _automaticUpdateState.LastAttemptTicks = DateTime.UtcNow.Ticks;
            SaveAutomaticUpdateState();
            RefreshAutomaticUpdateWindow();
            try
            {
                AppUpdate.AutoBatch batch = await Task.Run(delegate
                {
                    List<AppUpdate.AutoTarget> targets = new List<AppUpdate.AutoTarget>();
                    bool inventoryFailed = false;
                    if (plugins)
                        try
                        {
                            foreach (InstalledPlugin plugin in new PluginPackageStore(ConfigStore.DirectoryPath).List())
                            {
                                PluginManifest m = plugin.Manifest;
                                if (plugin.Error != null || m == null || m.Update == null ||
                                    m.Update.Provider != "github" || !AppUpdate.ValidPluginRepository(m.Update.Repository)) continue;
                                targets.Add(new AppUpdate.AutoTarget { Id = m.Id, Name = m.Name,
                                    Repository = m.Update.Repository, Installed = m.Version });
                            }
                        }
                        catch (Exception error) { ConfigStore.Log(error); inventoryFailed = true; }
                    AppUpdate.AutoBatch result = AppUpdate.CollectAutomatic(AppUpdate.InstalledVersionText(), core,
                        snapshot.CheckBetaUpdates, snapshot.AutoCheckPluginPreviews, targets,
                        AppUpdate.CheckWithDetails, AppUpdate.CheckPluginReleases, cancel.Token);
                    if (inventoryFailed) result.Failed++;
                    return result;
                });
                if (_automaticUpdateStopped || cancel.IsCancellationRequested) return;
                if (policy != AutomaticPolicy(_config))
                {
                    // A channel or policy changed during the request. Do not
                    // announce obsolete results or defer the new policy.
                    _automaticUpdateState.LastAttemptTicks = 0;
                    _automaticUpdateState.RetryTicks = 0;
                    _automaticUpdateFirst = true;
                    SaveAutomaticUpdateState();
                    return;
                }
                _automaticInventoryChanged = inventory != _automaticPluginGeneration || _automaticPluginManagerOpen;
                if (_automaticInventoryChanged)
                {
                    batch.Available.RemoveAll(delegate(AppUpdate.UpdateNotice n) { return n.Key != "core"; });
                    batch.Failed++;
                }
                _automaticUpdateBatch = batch;
                DateTime now = DateTime.UtcNow;
                _automaticUpdateState.Complete(batch, now);
                if (!manual && snapshot.AutoNotifyUpdates != false)
                {
                    List<AppUpdate.UpdateNotice> notices = _automaticUpdateState.Pending(batch, now);
                    if (notices.Count > 0)
                    {
                        string message = String.Join("\n", notices.ConvertAll(delegate(AppUpdate.UpdateNotice n) { return n.Caption; }).ToArray());
                        if (message.Length > 190) message = message.Substring(0, 189) + "…";
                        message += "\nПодробнее: меню AppShelf → Обновления…";
                        if (_trayIcon != null && _trayIcon.Visible && _trayIcon.Icon != null)
                            _trayIcon.ShowBalloonTip(6000, "AppShelf · Доступны обновления", message, Forms.ToolTipIcon.Info);
                        else ShowStatusMessage("Доступны обновления (" + notices.Count + "). Меню → Обновления…", true);
                        _automaticUpdateState.MarkNotified(notices);
                    }
                }
                SaveAutomaticUpdateState();
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                if (!_automaticUpdateStopped)
                {
                    _automaticUpdateBatch = new AppUpdate.AutoBatch { Failed = 1 };
                    _automaticUpdateState.Complete(_automaticUpdateBatch, DateTime.UtcNow);
                    SaveAutomaticUpdateState();
                }
            }
            finally
            {
                _automaticUpdateBusy = false;
                _automaticUpdateCancel = null;
                cancel.Dispose();
                if (!_automaticUpdateStopped) RefreshAutomaticUpdateWindow();
            }
        }

        private void ShowAutomaticUpdateWindow(Window owner)
        {
            if (_automaticUpdateWindow != null) { _automaticUpdateWindow.Activate(); return; }
            EnsureAutomaticUpdateState();
            Window dialog = new Window { Title = "AppShelf — Обновления", Owner = owner ?? this,
                Width = 650, Height = 640, MinWidth = 420, MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
            FluentUi.PrepareWindow(dialog);
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;
            WrapPanel actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
            Button save = ActionButton("Сохранить настройки", true);
            Button check = ActionButton("Проверить сейчас", false);
            Button close = ActionButton("Закрыть", false);
            actions.Children.Add(save); actions.Children.Add(check); actions.Children.Add(close);
            close.Click += delegate { dialog.Close(); };
            StackPanel layout = new StackPanel();
            ScrollViewer scroll = new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FluentUi.StyleSettingsScroll(scroll); root.Children.Add(scroll);
            TextBlock title = Text("Обновления", 23, WhiteBrush); title.FontWeight = FontWeights.Bold;
            layout.Children.Add(title);
            Func<string, bool, string, CheckBox> option = delegate(string label, bool value, string tag)
            {
                CheckBox box = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                    IsChecked = value, Foreground = WhiteBrush, Tag = tag, Margin = new Thickness(0, 8, 0, 3) };
                layout.Children.Add(box); return box;
            };
            CheckBox core = option("Автоматически проверять обновления AppShelf", _config.AutoCheckCoreUpdates != false, "AutoUpdateCore");
            CheckBox plugins = option("Автоматически проверять обновления плагинов", _config.AutoCheckPluginUpdates != false, "AutoUpdatePlugins");
            TextBlock intervalLabel = Text("Периодичность проверки", 12, MutedBrush); intervalLabel.Margin = new Thickness(0, 12, 0, 4);
            layout.Children.Add(intervalLabel);
            ComboBox interval = new ComboBox { Tag = "AutoUpdateInterval", MinHeight = 30 };
            foreach (int hours in new[] { 1, 3, 6, 12, 24 })
            {
                ComboBoxItem item = new ComboBoxItem { Content = hours == 24 ? "Раз в сутки" : hours == 1 ? "Каждый час" : "Каждые " + hours + " ч.", Tag = hours };
                interval.Items.Add(item);
                if (hours == AppUpdate.AutoState.Interval(_config.AutoUpdateIntervalHours)) interval.SelectedItem = item;
            }
            FluentUi.StyleComboBox(interval); layout.Children.Add(interval);
            CheckBox startup = option("Проверять при запуске приложения", _config.AutoCheckUpdatesAtStartup != false, "AutoUpdateStartup");
            CheckBox notify = option("Уведомлять о новых версиях", _config.AutoNotifyUpdates != false, "AutoUpdateNotify");
            CheckBox beta = option("Проверять бета-версии AppShelf", _config.CheckBetaUpdates, "AutoUpdateBeta");
            CheckBox previews = option("Проверять предварительные версии плагинов", _config.AutoCheckPluginPreviews, "AutoUpdatePreviews");
            TextBlock help = Text("Установка только вручную. Первая фоновая проверка через минуту после запуска; повторный запуск не создаёт запросы чаще пяти минут. «Проверить сейчас» проверяет ядро и все установленные плагины по сохранённым каналам.", 12, MutedBrush);
            help.TextWrapping = TextWrapping.Wrap; help.Margin = new Thickness(0, 12, 0, 12);
            layout.Children.Add(help);
            TextBlock status = Text("", 12, MutedBrush); status.TextWrapping = TextWrapping.Wrap; status.Tag = "AutoUpdateStatus";
            layout.Children.Add(status);
            StackPanel results = new StackPanel { Tag = "AutoUpdateResults", Margin = new Thickness(0, 8, 0, 8) };
            layout.Children.Add(results);
            WrapPanel details = new WrapPanel();
            Button coreDetails = ActionButton("Обновления AppShelf…", false);
            Button pluginDetails = ActionButton("Менеджер плагинов…", false);
            Button snooze = ActionButton("Напомнить через 6 часов", false);
            details.Children.Add(coreDetails); details.Children.Add(pluginDetails); details.Children.Add(snooze);
            layout.Children.Add(details);
            coreDetails.Click += delegate { ShowUpdateWindow(dialog); beta.IsChecked = _config.CheckBetaUpdates; };
            pluginDetails.Click += delegate { ShowPluginManager(dialog); };
            snooze.Click += delegate
            {
                _automaticUpdateState.Snooze(DateTime.UtcNow);
                SaveAutomaticUpdateState(); RefreshAutomaticUpdateWindow();
            };
            Action refresh = delegate
            {
                check.IsEnabled = !_automaticUpdateBusy;
                save.IsEnabled = _canSave;
                bool enabled = core.IsChecked == true || plugins.IsChecked == true;
                interval.IsEnabled = startup.IsEnabled = notify.IsEnabled = enabled;
                DateTime? last = AppUpdate.AutoState.Time(_automaticUpdateState.LastSuccessTicks);
                status.Text = "Последняя успешная проверка: " + (last.HasValue ? last.Value.ToLocalTime().ToString("g") : "ещё не было");
                if (_automaticUpdateBusy) status.Text += "\nПроверка выполняется…";
                else if (_automaticInventoryChanged)
                    status.Text += "\nСписок плагинов мог измениться. Для актуального результата нажмите «Проверить сейчас».";
                else if (_automaticUpdateBatch != null)
                    status.Text += _automaticUpdateBatch.Failed > 0 ? "\nЧасть источников недоступна. Следующая фоновая попытка не ранее чем через 30 минут." :
                        _automaticUpdateBatch.Available.Count == 0 ? "\nБолее новых версий в проверенных каналах нет." : "\nДоступны обновления: " + _automaticUpdateBatch.Available.Count;
                DateTime? retry = AppUpdate.AutoState.Time(_automaticUpdateState.RetryTicks);
                if (retry.HasValue && retry.Value > DateTime.UtcNow) status.Text += "\nСледующая попытка после " + retry.Value.ToLocalTime().ToString("g");
                DateTime? later = AppUpdate.AutoState.Time(_automaticUpdateState.SnoozeTicks);
                if (later.HasValue && later.Value > DateTime.UtcNow) status.Text += "\nУведомления отложены до " + later.Value.ToLocalTime().ToString("g");
                results.Children.Clear();
                if (_automaticUpdateBatch != null)
                    foreach (AppUpdate.UpdateNotice notice in _automaticUpdateBatch.Available)
                    {
                        TextBlock line = Text(notice.Caption, 13, WhiteBrush);
                        line.TextWrapping = TextWrapping.Wrap; line.Margin = new Thickness(0, 3, 0, 3);
                        results.Children.Add(line);
                    }
                snooze.IsEnabled = _canSave && enabled && notify.IsChecked == true && !_automaticUpdateBusy &&
                    _automaticUpdateBatch != null && _automaticUpdateBatch.Available.Count > 0;
            };
            RoutedEventHandler changed = delegate { refresh(); };
            core.Checked += changed; core.Unchecked += changed; plugins.Checked += changed; plugins.Unchecked += changed;
            notify.Checked += changed; notify.Unchecked += changed;
            save.Click += delegate
            {
                AppConfig next = _config.Copy();
                next.AutoCheckCoreUpdates = core.IsChecked == true; next.AutoCheckPluginUpdates = plugins.IsChecked == true;
                next.AutoCheckUpdatesAtStartup = startup.IsChecked == true; next.AutoNotifyUpdates = notify.IsChecked == true;
                next.AutoUpdateIntervalHours = (int)((ComboBoxItem)interval.SelectedItem).Tag;
                next.CheckBetaUpdates = beta.IsChecked == true; next.AutoCheckPluginPreviews = previews.IsChecked == true;
                bool changedPolicy = AutomaticPolicy(next) != AutomaticPolicy(_config);
                if (!SaveConfigChanges(next)) return;
                if (changedPolicy) AutomaticUpdatePolicyChanged();
                refresh(); status.Text += "\nНастройки сохранены.";
            };
            check.Click += async delegate { await RunAutomaticUpdateCheck(true); };
            _automaticUpdateWindow = dialog;
            _refreshAutomaticUpdateWindow = refresh;
            refresh();
            try { dialog.ShowDialog(); } // Automatic update center boundary.
            finally { _refreshAutomaticUpdateWindow = null; _automaticUpdateWindow = null; }
        }

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
                AutomaticUpdatePolicyChanged();
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
