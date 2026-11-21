// Local reminders for AppShelf. Uses the Windows local clock and does not require cloud services.
// Compatible with the .NET Framework C# compiler shipped with Windows.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppShelf
{
    [DataContract]
    public sealed class ReminderItem
    {
        [DataMember(Name = "id")]
        public string Id;

        [DataMember(Name = "title")]
        public string Title;

        // Local wall clock date and time, e.g. 2026-11-12T19:00:00.
        [DataMember(Name = "dueLocal")]
        public string DueLocal;

        // once, hourly, daily, weekly, monthly, yearly, weekdays, weekends, custom_weekdays
        [DataMember(Name = "repeat")]
        public string Repeat = "once";

        [DataMember(Name = "active")]
        public bool Active = true;

        // Calendar anchors survive clamping to a short month. Absent on legacy records.
        [DataMember(Name = "repeatDay", EmitDefaultValue = false)]
        public int? RepeatDay;

        [DataMember(Name = "repeatMonth", EmitDefaultValue = false)]
        public int? RepeatMonth;

        // Bit 0 = Monday, ... bit 6 = Sunday. Only for custom_weekdays.
        // Optional to preserve compatibility with existing reminders.json.
        [DataMember(Name = "repeatWeekdays", EmitDefaultValue = false)]
        public int? RepeatWeekdays;

        // Interval for hourly repeats, from 1 to 24 hours; absent in older records.
        [DataMember(Name = "repeatHours", EmitDefaultValue = false)]
        public int? RepeatHours;

        // Optional quiet hours for hourly recurrences, measured in local clock hours.
        // They are omitted from legacy reminders.json data.
        [DataMember(Name = "quietHoursEnabled", EmitDefaultValue = false)]
        public bool QuietHoursEnabled;

        [DataMember(Name = "quietStartHour", EmitDefaultValue = false)]
        public int? QuietStartHour;

        [DataMember(Name = "quietEndHour", EmitDefaultValue = false)]
        public int? QuietEndHour;

        // Identifies the recurring reminder that produced this completed/snoozed occurrence.
        // Absent on existing reminders.json records.
        [DataMember(Name = "parentId", EmitDefaultValue = false)]
        public string ParentId;

        // Persists until the user dismisses or snoozes the actual notification.
        // Absent/false in legacy history rows; never replays already completed history.
        [DataMember(Name = "pendingAlert", EmitDefaultValue = false)]
        public bool PendingAlert;

        // Distinguishes an intentional snooze from a stale hourly notification.
        [DataMember(Name = "snoozed", EmitDefaultValue = false)]
        public bool Snoozed;
    }

    [DataContract]
    public sealed class ReminderDocument
    {
        [DataMember(Name = "version")]
        public int Version = 1;

        [DataMember(Name = "reminders")]
        public List<ReminderItem> Reminders = new List<ReminderItem>();
    }

    public static class ReminderStore
    {
        public static readonly string FilePath = Path.Combine(ConfigStore.DirectoryPath, "reminders.json");
        private static readonly string Format = "yyyy-MM-dd'T'HH:mm:ss";

        public static string ToValue(DateTime local)
        {
            return local.ToString(Format, CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string value, out DateTime date)
        {
            return DateTime.TryParseExact(value, Format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
        }

        internal static int WeekdayIndex(DayOfWeek day)
        {
            // DayOfWeek starts at Sunday; stored mask starts at Monday.
            return ((int)day + 6) % 7;
        }

        internal static bool IsWeekdayRepeat(string repeat)
        {
            return repeat == "weekdays" || repeat == "weekends" || repeat == "custom_weekdays";
        }

        internal static int WeekdayMask(string repeat, int? selected)
        {
            if (repeat == "weekdays") return 31; // Monday–Friday
            if (repeat == "weekends") return 96; // Saturday–Sunday
            return repeat == "custom_weekdays" ? selected ?? 0 : 0;
        }

        // The selected date is the lower bound, not necessarily an occurrence day.
        internal static DateTime FirstSelectedWeekday(DateTime start, int mask)
        {
            if (mask <= 0 || mask > 127) throw new ArgumentOutOfRangeException("mask");
            for (int offset = 0; offset < 7; offset++)
            {
                DateTime candidate = start.AddDays(offset);
                if ((mask & (1 << WeekdayIndex(candidate.DayOfWeek))) != 0) return candidate;
            }
            throw new InvalidOperationException("Не удалось найти выбранный день недели.");
        }

        // The interval [start, end) may cross midnight (e.g. 22:00–09:00).
        internal static bool IsQuietHour(ReminderItem item, DateTime time)
        {
            if (item == null || !item.QuietHoursEnabled ||
                !item.QuietStartHour.HasValue || !item.QuietEndHour.HasValue) return false;
            int from = item.QuietStartHour.Value;
            int until = item.QuietEndHour.Value;
            if (from == until) return false; // Rejected by validation; never silence a full day.
            return from < until
                ? time.Hour >= from && time.Hour < until
                : time.Hour >= from || time.Hour < until;
        }

        internal static int HourlyInterval(ReminderItem item)
        {
            return item == null ? 1 : item.RepeatHours.GetValueOrDefault(1);
        }

        internal static DateTime FirstAllowedHourly(ReminderItem item, DateTime candidate)
        {
            // Do not shift the phase: only whole N-hour slots are valid.
            int interval = HourlyInterval(item);
            for (int slot = 0; slot < 24; slot++)
            {
                if (!IsQuietHour(item, candidate)) return candidate;
                if (slot != 23) candidate = candidate.AddHours(interval);
            }
            // A 24-hour cycle has at most 24 distinct hourly slots.
            throw new InvalidOperationException(
                "При выбранном интервале все срабатывания попадают в тихие часы.");
        }

        internal static DateTime NextAllowedHourly(ReminderItem item, DateTime candidate)
        {
            // Used for already saved reminders too. An old unsatisfiable
            // schedule must stay readable and silent, not crash the timer.
            DateTime first = candidate;
            int interval = HourlyInterval(item);
            for (int slot = 0; slot < 24; slot++)
            {
                if (!IsQuietHour(item, candidate)) return candidate;
                if (slot != 23) candidate = candidate.AddHours(interval);
            }
            return first;
        }

        internal static DateTime FirstNonQuietHour(ReminderItem item, DateTime candidate)
        {
            // Snoozes are deliberate one-off notifications, not N-hour slots.
            for (int skipped = 0; skipped < 24 && IsQuietHour(item, candidate); skipped++)
                candidate = candidate.AddHours(1);
            return candidate;
        }

        // The most recent permitted slot, not every missed occurrence after sleep.
        internal static DateTime LatestHourlyOccurrence(ReminderItem item, DateTime now)
        {
            DateTime start;
            if (!TryParse(item.DueLocal, out start) || start > now) return DateTime.MinValue;
            int interval = HourlyInterval(item);
            DateTime candidate = start.AddHours(
                Math.Floor((now - start).TotalHours / interval) * interval);
            for (int skipped = 0; skipped < 24 && candidate >= start &&
                IsQuietHour(item, candidate); skipped++)
                candidate = candidate.AddHours(-interval);
            return candidate >= start ? candidate : DateTime.MinValue;
        }

        internal static string RepeatLabel(ReminderItem item)
        {
            if (item.Repeat == "hourly")
            {
                int hours = HourlyInterval(item);
                string period = hours == 1 ? "каждый час" :
                    String.Format(CultureInfo.CurrentCulture, "каждые {0} ч.", hours);
                return item.QuietHoursEnabled && item.QuietStartHour.HasValue && item.QuietEndHour.HasValue
                    ? String.Format(CultureInfo.CurrentCulture,
                        "{0} · тихие часы {1:D2}:00–{2:D2}:00",
                        period, item.QuietStartHour.Value, item.QuietEndHour.Value)
                    : period;
            }
            if (item.Repeat == "daily") return "ежедневно";
            if (item.Repeat == "weekly") return "еженедельно";
            if (item.Repeat == "monthly") return "ежемесячно";
            if (item.Repeat == "yearly") return "ежегодно";
            if (item.Repeat == "weekdays") return "по будням";
            if (item.Repeat == "weekends") return "по выходным";
            if (item.Repeat == "custom_weekdays")
            {
                string[] labels = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
                List<string> selected = new List<string>();
                int mask = WeekdayMask(item.Repeat, item.RepeatWeekdays);
                int singleDay = -1;
                for (int i = 0; i < labels.Length; i++)
                    if ((mask & (1 << i)) != 0)
                    {
                        selected.Add(labels[i]);
                        singleDay = i;
                    }
                if (selected.Count == 1)
                {
                    string[] oneDay = { "каждый понедельник", "каждый вторник", "каждую среду",
                        "каждый четверг", "каждую пятницу", "каждую субботу", "каждое воскресенье" };
                    return oneDay[singleDay];
                }
                return "по дням недели: " + String.Join(", ", selected.ToArray());
            }
            return "один раз";
        }

        internal static bool IsValid(ReminderItem item)
        {
            DateTime date;
            if (item == null || String.IsNullOrWhiteSpace(item.Id) || String.IsNullOrWhiteSpace(item.Title) ||
                !TryParse(item.DueLocal, out date) ||
                (item.Repeat != "once" && item.Repeat != "hourly" &&
                 item.Repeat != "daily" && item.Repeat != "weekly" &&
                 item.Repeat != "monthly" && item.Repeat != "yearly" &&
                 !IsWeekdayRepeat(item.Repeat))) return false;
            if (item.RepeatHours.HasValue &&
                (item.RepeatHours.Value < 1 || item.RepeatHours.Value > 24)) return false;
            if ((item.QuietStartHour.HasValue &&
                    (item.QuietStartHour.Value < 0 || item.QuietStartHour.Value > 23)) ||
                (item.QuietEndHour.HasValue &&
                    (item.QuietEndHour.Value < 0 || item.QuietEndHour.Value > 23))) return false;
            // Fired hourly alerts become standalone history rows (repeat=once)
            // but must retain their original quiet-hour rules.
            if (item.QuietHoursEnabled &&
                ((item.Repeat != "hourly" &&
                  (item.Repeat != "once" || String.IsNullOrWhiteSpace(item.ParentId))) ||
                 !item.QuietStartHour.HasValue || !item.QuietEndHour.HasValue ||
                 item.QuietStartHour.Value == item.QuietEndHour.Value)) return false;
            if ((item.RepeatDay.HasValue && (item.RepeatDay.Value < 1 || item.RepeatDay.Value > 31)) ||
                (item.RepeatMonth.HasValue && (item.RepeatMonth.Value < 1 || item.RepeatMonth.Value > 12)) ||
                (item.RepeatWeekdays.HasValue && (item.RepeatWeekdays.Value < 1 || item.RepeatWeekdays.Value > 127)))
                return false;
            if (item.Repeat == "custom_weekdays" && !item.RepeatWeekdays.HasValue) return false;
            if (IsWeekdayRepeat(item.Repeat) &&
                (WeekdayMask(item.Repeat, item.RepeatWeekdays) & (1 << WeekdayIndex(date.DayOfWeek))) == 0)
                return false;
            // February 29 is valid as an anchor, even when this occurrence falls in an ordinary year.
            return item.Repeat != "yearly" || (item.RepeatDay ?? date.Day) <=
                DateTime.DaysInMonth(2000, item.RepeatMonth ?? date.Month);
        }

        internal static ReminderItem AfterOccurrence(ReminderItem item, DateTime now)
        {
            if (!IsValid(item)) throw new InvalidDataException("Некорректное напоминание.");
            DateTime date;
            TryParse(item.DueLocal, out date);
            ReminderItem next = new ReminderItem
            {
                Id = item.Id, Title = item.Title, DueLocal = item.DueLocal, Repeat = item.Repeat,
                Active = item.Active, RepeatDay = item.RepeatDay, RepeatMonth = item.RepeatMonth,
                RepeatWeekdays = item.RepeatWeekdays, RepeatHours = item.RepeatHours,
                QuietHoursEnabled = item.QuietHoursEnabled,
                QuietStartHour = item.QuietStartHour, QuietEndHour = item.QuietEndHour,
                ParentId = item.ParentId, PendingAlert = item.PendingAlert,
                // Older saved snoozes have no marker, but active child occurrences
                // only arise from user rescheduling/snoozing.
                Snoozed = item.Snoozed || (item.Repeat == "once" && item.Active &&
                    !String.IsNullOrWhiteSpace(item.ParentId))
            };
            if (date > now || !item.Active) return next;
            if (item.Repeat == "once") next.Active = false;
            else if (item.Repeat == "hourly")
            {
                int interval = HourlyInterval(item);
                double completedPeriods = Math.Floor((now - date).TotalHours / interval) + 1;
                next.DueLocal = ToValue(NextAllowedHourly(item,
                    date.AddHours(completedPeriods * interval)));
            }
            else if (item.Repeat == "daily" || item.Repeat == "weekly")
            {
                int period = item.Repeat == "daily" ? 1 : 7;
                int count = (int)Math.Floor((now - date).TotalDays / period) + 1;
                next.DueLocal = ToValue(date.AddDays((double)count * period));
            }
            else if (IsWeekdayRepeat(item.Repeat))
            {
                int mask = WeekdayMask(item.Repeat, item.RepeatWeekdays);
                DateTime start = now.Date.Add(date.TimeOfDay);
                if (start <= now) start = start.AddDays(1);
                next.DueLocal = ToValue(FirstSelectedWeekday(start, mask));
            }
            else
            {
                int day = item.RepeatDay ?? date.Day;
                next.RepeatDay = day;
                DateTime month;
                if (item.Repeat == "monthly")
                {
                    month = new DateTime(now.Year, now.Month, 1);
                    DateTime candidate = CalendarDate(month, day, date.TimeOfDay);
                    if (candidate <= now) month = month.AddMonths(1);
                }
                else
                {
                    int anchorMonth = item.RepeatMonth ?? date.Month;
                    next.RepeatMonth = anchorMonth;
                    month = new DateTime(now.Year, anchorMonth, 1);
                    DateTime candidate = CalendarDate(month, day, date.TimeOfDay);
                    if (candidate <= now) month = month.AddYears(1);
                }
                next.DueLocal = ToValue(CalendarDate(month, day, date.TimeOfDay));
            }
            return next;
        }

        private static DateTime CalendarDate(DateTime month, int day, TimeSpan time)
        {
            return new DateTime(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month))).Add(time);
        }

        private static ReminderDocument Read(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                ReminderDocument data = (ReminderDocument)new DataContractJsonSerializer(
                    typeof(ReminderDocument)).ReadObject(stream);
                if (data == null || data.Reminders == null)
                    throw new InvalidDataException("Повреждён список напоминаний.");
                foreach (ReminderItem item in data.Reminders)
                {
                    if (!IsValid(item))
                        throw new InvalidDataException("В файле напоминаний есть некорректная запись.");
                }
                return data;
            }
        }

        public static ReminderDocument Load(out string warning, out bool safeToSave)
        {
            warning = null;
            safeToSave = true;
            if (!File.Exists(FilePath)) return new ReminderDocument();
            try { return Read(FilePath); }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                safeToSave = false;
                warning = "Не удалось прочитать reminders.json. Существующий файл не будет перезаписан." +
                          "\nПроверь файл " + FilePath + "\n\n" + error.Message;
                try
                {
                    if (File.Exists(FilePath + ".bak")) return Read(FilePath + ".bak");
                }
                catch (Exception backupError) { ConfigStore.Log(backupError); }
                return new ReminderDocument();
            }
        }

        public static void Save(ReminderDocument document)
        {
            Directory.CreateDirectory(ConfigStore.DirectoryPath);
            string temp = FilePath + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(ReminderDocument)).WriteObject(stream, document);
                    stream.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak", true);
                else File.Move(temp, FilePath);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }
    }

    // Two independent bounded numeric fields; all edits stay in the editor until Save succeeds.
    internal sealed class ReminderTimePicker : Grid
    {
        private readonly TextBox _hours;
        private readonly TextBox _minutes;

        public ReminderTimePicker(DateTime value)
        {
            Name = "ReminderTime";
            MinWidth = 112;
            MinHeight = 36;
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            ColumnDefinitions.Add(new ColumnDefinition());
            _hours = AddPart("ReminderHours", "Часы (00–23)", value.Hour, 23, 0);
            TextBlock separator = new TextBlock { Text = ":", Foreground = FluentUi.Foreground,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(separator, 1); Children.Add(separator);
            _minutes = AddPart("ReminderMinutes", "Минуты (00–59), шаг 1 минута", value.Minute, 59, 2);
        }

        private static bool TryPart(string text, int maximum, out int value)
        {
            value = 0;
            return text != null && text.Length >= 1 && text.Length <= 2 &&
                Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum;
        }

        public bool TryGetTime(out TimeSpan value)
        {
            value = TimeSpan.Zero;
            int hour, minute;
            if (!TryPart(_hours.Text, 23, out hour) || !TryPart(_minutes.Text, 59, out minute)) return false;
            _hours.Text = hour.ToString("D2", CultureInfo.InvariantCulture);
            _minutes.Text = minute.ToString("D2", CultureInfo.InvariantCulture);
            value = new TimeSpan(hour, minute, 0);
            return true;
        }

        private static bool Accepts(TextBox input, string text, int maximum)
        {
            if (text == null) return false;
            string candidate = input.Text.Remove(input.SelectionStart, input.SelectionLength).Insert(input.SelectionStart, text);
            int number;
            return candidate.Length == 0 || TryPart(candidate, maximum, out number);
        }

        private static void Step(TextBox input, int maximum, int delta)
        {
            int number;
            if (!TryPart(input.Text, maximum, out number)) return;
            input.Focus();
            input.Text = Math.Max(0, Math.Min(maximum, number + delta)).ToString("D2", CultureInfo.InvariantCulture);
            input.SelectAll();
        }

        private TextBox AddPart(string name, string label, int initial, int maximum, int column)
        {
            Grid part = new Grid();
            part.ColumnDefinitions.Add(new ColumnDefinition());
            part.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            part.RowDefinitions.Add(new RowDefinition()); part.RowDefinitions.Add(new RowDefinition());
            SetColumn(part, column); Children.Add(part);
            TextBox input = new TextBox { Name = name, Text = initial.ToString("D2", CultureInfo.InvariantCulture),
                MinWidth = 0, MaxLength = 2, Padding = new Thickness(3, 4, 3, 4),
                VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                ToolTip = label + ". Клавиши ↑/↓ изменяют выбранное поле; границы не переносят дату или соседнее поле." };
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            SetRowSpan(input, 2); part.Children.Add(input);
            Button up = Arrow("▴", "Увеличить: " + label, delegate { Step(input, maximum, 1); });
            Button down = Arrow("▾", "Уменьшить: " + label, delegate { Step(input, maximum, -1); });
            up.Name = name + "Up"; down.Name = name + "Down";
            SetColumn(up, 1); SetColumn(down, 1); SetRow(down, 1); part.Children.Add(up); part.Children.Add(down);
            Action update = delegate
            {
                int number; bool valid = TryPart(input.Text, maximum, out number);
                up.IsEnabled = valid && number < maximum; down.IsEnabled = valid && number > 0;
            };
            input.TextChanged += delegate { update(); };
            input.PreviewTextInput += delegate(object sender, TextCompositionEventArgs e) { e.Handled = !Accepts(input, e.Text, maximum); };
            DataObject.AddPastingHandler(input, delegate(object sender, DataObjectPastingEventArgs e)
            {
                if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
                    !Accepts(input, e.DataObject.GetData(DataFormats.UnicodeText) as string, maximum)) e.CancelCommand();
            });
            input.GotKeyboardFocus += delegate { input.SelectAll(); };
            input.LostKeyboardFocus += delegate
            {
                int number;
                if (TryPart(input.Text, maximum, out number)) input.Text = number.ToString("D2", CultureInfo.InvariantCulture);
            };
            input.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Up && e.Key != Key.Down) return;
                Step(input, maximum, e.Key == Key.Up ? 1 : -1); e.Handled = true;
            };
            update();
            return input;
        }

        private static Button Arrow(string text, string label, Action action)
        {
            Button button = new Button { Content = text, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0),
                FontSize = 10, Focusable = false, Background = FluentUi.SurfaceRaised, Foreground = FluentUi.Foreground,
                BorderBrush = FluentUi.BorderColor, ToolTip = label };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border)); border.Name = "Chrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content); template.VisualTree = border;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, FluentUi.SurfaceHover, "Chrome")); template.Triggers.Add(hover);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35)); template.Triggers.Add(disabled);
            button.Template = template; button.Click += delegate { action(); };
            return button;
        }
    }

    public sealed partial class MainWindow
    {
        private ReminderDocument _reminderDocument;
        private bool _remindersCanSave;
        private DispatcherTimer _remindersTimer;
        private Window _remindersWindow;
        private StackPanel _remindersList;
        private Border _remindersChip;
        private TextBlock _remindersChipText;
        private Window _reminderAlertWindow;
        private readonly List<string> _pendingReminderNames = new List<string>();
        // Names are presentation only; IDs prevent collisions between identically named reminders.
        private readonly List<string> _pendingReminderIds = new List<string>();
        private readonly HashSet<string> _openReminderAlertIds = new HashSet<string>(StringComparer.Ordinal);
        private bool _checkingDueReminders;
        private bool _dueReminderSaveFailed;
        private bool _remindersShuttingDown;
        private bool _reminderSelectMode;
        private readonly HashSet<string> _selectedReminderIds = new HashSet<string>(StringComparer.Ordinal);
        private Button _removeSelectedRemindersButton;

        private void InitializeReminders()
        {
            string warning;
            _reminderDocument = ReminderStore.Load(out warning, out _remindersCanSave);
            if (!String.IsNullOrEmpty(warning))
            {
                string display = warning;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    MessageBox.Show(this, display, "AppShelf — напоминания", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }));
            }
        }

        private void CreateRemindersChip()
        {
            _remindersChipText = Text("Нет напоминаний", 12, WhiteBrush);
            // Keep the full reminder name readable even in the detailed bottom panel.
            _remindersChipText.TextWrapping = TextWrapping.Wrap;
            _remindersChipText.MaxWidth = 310;
            _remindersChip = FluentUi.Chip(_remindersChipText, AccentBrush, true);
            _remindersChip.ToolTip = "Открыть список напоминаний";
            _remindersChip.MouseLeftButtonUp += delegate { ToggleBottomModuleWindow("reminders"); };
            UpdateReminderChip();
        }

        private void StartReminderTimer()
        {
            _remindersTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _remindersTimer.Tick += delegate
            {
                CheckDueReminders();
                // Snoozed alerts held during quiet hours must resume once allowed.
                RecoverUnacknowledgedReminders();
            };
            _remindersTimer.Start();
            // Replay alerts that were saved but never acknowledged (e.g. shutdown
            // before the user could see the window), then check the current schedule.
            RecoverUnacknowledgedReminders();
            CheckDueReminders();
        }

        private void StopReminders()
        {
            _remindersShuttingDown = true;
            if (_remindersTimer != null) _remindersTimer.Stop();
            if (_remindersWindow != null) _remindersWindow.Close();
            if (_reminderAlertWindow != null) _reminderAlertWindow.Close();
        }

        private bool SaveReminders(ReminderDocument pending)
        {
            if (!_remindersCanSave)
            {
                MessageBox.Show(this, "Сохранение напоминаний заблокировано из-за ошибки файла reminders.json.",
                    "AppShelf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try
            {
                // Callers apply pending changes to live objects only after this succeeds.
                ReminderStore.Save(pending);
                return true;
            }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                MessageBox.Show(this, "Не удалось сохранить напоминания:\n" + error.Message,
                    "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void UpdateReminderChip()
        {
            if (_remindersChipText == null || _reminderDocument == null) return;
            ReminderItem closest = null;
            DateTime nearest = DateTime.MaxValue;
            foreach (ReminderItem item in _reminderDocument.Reminders)
            {
                DateTime date;
                if (!item.Active || !ReminderStore.TryParse(item.DueLocal, out date)) continue;
                if (date < nearest)
                {
                    closest = item;
                    nearest = date;
                }
            }
            // The button is useful only when it identifies the upcoming reminder.
            // Keep the full name and time; the bottom-panel buttons wrap long text.
            _remindersChipText.Text = closest == null ? "Нет напоминаний" :
                closest.Title + " · " + nearest.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture);
        }

        // Hourly occurrences become one-off rows. New rows carry a snapshot;
        // older rows can inherit the current rule from their recurring parent.
        private ReminderItem QuietRuleForOccurrence(ReminderItem item)
        {
            if (item == null || _reminderDocument == null) return null;
            if (item.QuietHoursEnabled) return item;
            if (item.Repeat != "once" || String.IsNullOrWhiteSpace(item.ParentId)) return null;
            ReminderItem parent = _reminderDocument.Reminders.Find(delegate(ReminderItem candidate)
            {
                return candidate != null && candidate.Id == item.ParentId &&
                    candidate.Repeat == "hourly";
            });
            return parent != null && parent.QuietHoursEnabled ? parent : null;
        }

        private bool SnoozedAlertInQuietHours(ReminderItem item, DateTime now)
        {
            if (item == null || !item.Snoozed || !item.PendingAlert) return false;
            return ReminderStore.IsQuietHour(QuietRuleForOccurrence(item), now);
        }

        // Stale automatic hourly alerts must not reappear after a quiet period.
        // Explicit snoozes are deferred during quiet hours, never acknowledged silently.
        private bool SuppressHourlyAlert(ReminderItem item, DateTime now)
        {
            ReminderItem rule = QuietRuleForOccurrence(item);
            if (rule == null || item == null || !item.PendingAlert) return false;
            if (item.Snoozed) return false;
            if (ReminderStore.IsQuietHour(rule, now)) return true;
            DateTime firedAt;
            if (!ReminderStore.TryParse(item.DueLocal, out firedAt) || firedAt >= now)
                return false;
            if (now - firedAt >= TimeSpan.FromDays(1)) return true;
            for (DateTime moment = firedAt; moment < now; moment = moment.AddHours(1))
                if (ReminderStore.IsQuietHour(rule, moment)) return true;
            return false;
        }

        private void CheckDueReminders()
        {
            if (!_remindersCanSave || _reminderDocument == null || _checkingDueReminders) return;
            _checkingDueReminders = true;
            try
            {
                DateTime now = DateTime.Now;
                ReminderDocument pending = new ReminderDocument
                {
                    Version = _reminderDocument.Version,
                    Reminders = new List<ReminderItem>(_reminderDocument.Reminders)
                };
                List<KeyValuePair<ReminderItem, ReminderItem>> due = new List<KeyValuePair<ReminderItem, ReminderItem>>();
                List<KeyValuePair<ReminderItem, ReminderItem>> skipped = new List<KeyValuePair<ReminderItem, ReminderItem>>();
                List<ReminderItem> newOccurrences = new List<ReminderItem>();
                int originalCount = _reminderDocument.Reminders.Count;
                for (int i = 0; i < originalCount; i++)
                {
                    ReminderItem item = _reminderDocument.Reminders[i];
                    DateTime date;
                    if (!item.Active || !ReminderStore.TryParse(item.DueLocal, out date) || date > now) continue;
                    // A manually snoozed hourly occurrence is now repeat=once,
                    // but it must still respect its parent's quiet period.
                    if (item.Repeat == "once" && !String.IsNullOrWhiteSpace(item.ParentId))
                    {
                        ReminderItem quietRule = QuietRuleForOccurrence(item);
                        if (quietRule != null &&
                            (ReminderStore.IsQuietHour(quietRule, now) ||
                             ReminderStore.IsQuietHour(quietRule, date)))
                        {
                            DateTime from = ReminderStore.IsQuietHour(quietRule, now) ? now : date;
                            DateTime postponedTo = ReminderStore.FirstNonQuietHour(quietRule, from);
                            if (postponedTo > now)
                            {
                                ReminderItem postponed = new ReminderItem
                                {
                                    Id = item.Id, Title = item.Title, ParentId = item.ParentId,
                                    Repeat = "once", Active = true,
                                    DueLocal = ReminderStore.ToValue(postponedTo),
                                    RepeatDay = item.RepeatDay, RepeatMonth = item.RepeatMonth,
                                    RepeatWeekdays = item.RepeatWeekdays, RepeatHours = item.RepeatHours,
                                    QuietHoursEnabled = quietRule.QuietHoursEnabled,
                                    QuietStartHour = quietRule.QuietStartHour,
                                    QuietEndHour = quietRule.QuietEndHour,
                                    PendingAlert = false, Snoozed = item.Snoozed
                                };
                                pending.Reminders[i] = postponed;
                                skipped.Add(new KeyValuePair<ReminderItem, ReminderItem>(item, postponed));
                                continue;
                            }
                        }
                    }
                    // Calculate on a copy; skip missed periods and notify only once.
                    ReminderItem next = ReminderStore.AfterOccurrence(item, now);
                    DateTime firedAt = date;
                    if (item.Repeat == "hourly")
                    {
                        firedAt = ReminderStore.LatestHourlyOccurrence(item, now);
                        // No alerts during quiet hours and no backlog from a quiet
                        // period. A recent hourly slot is allowed after sleep/restart.
                        if (ReminderStore.IsQuietHour(item, now) || firedAt == DateTime.MinValue ||
                            now - firedAt >= TimeSpan.FromHours(1))
                        {
                            pending.Reminders[i] = next;
                            skipped.Add(new KeyValuePair<ReminderItem, ReminderItem>(item, next));
                            continue;
                        }
                    }
                    if (item.Repeat == "once") next.PendingAlert = true;
                    pending.Reminders[i] = next;
                    due.Add(new KeyValuePair<ReminderItem, ReminderItem>(item, next));
                    if (item.Repeat != "once")
                    {
                        // One durable history row per fired occurrence. Future repeats keep
                        // their own schedule and subsequent snoozes reuse this row by ID.
                        ReminderItem occurrence = new ReminderItem
                        {
                            Id = Guid.NewGuid().ToString("N"), ParentId = item.Id,
                            Title = item.Title, DueLocal = ReminderStore.ToValue(firedAt),
                            Repeat = "once", Active = false, PendingAlert = true,
                            QuietHoursEnabled = item.Repeat == "hourly" && item.QuietHoursEnabled,
                            QuietStartHour = item.Repeat == "hourly" ? item.QuietStartHour : null,
                            QuietEndHour = item.Repeat == "hourly" ? item.QuietEndHour : null
                        };
                        pending.Reminders.Add(occurrence);
                        newOccurrences.Add(occurrence);
                    }
                }
                if (due.Count == 0 && skipped.Count == 0) return;

                try { ReminderStore.Save(pending); }
                catch (Exception error)
                {
                    // Leave live reminders overdue. Retry on the next tick, without repeated dialogs.
                    if (!_dueReminderSaveFailed)
                    {
                        _dueReminderSaveFailed = true;
                        ConfigStore.Log(error);
                        MessageBox.Show(this, "Не удалось сохранить напоминания:\n" + error.Message,
                            "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return;
                }
                _dueReminderSaveFailed = false;
                foreach (KeyValuePair<ReminderItem, ReminderItem> change in skipped)
                {
                    change.Key.DueLocal = change.Value.DueLocal;
                    change.Key.QuietHoursEnabled = change.Value.QuietHoursEnabled;
                    change.Key.QuietStartHour = change.Value.QuietStartHour;
                    change.Key.QuietEndHour = change.Value.QuietEndHour;
                    change.Key.Snoozed = change.Value.Snoozed;
                }
                int occurrenceIndex = 0;
                foreach (KeyValuePair<ReminderItem, ReminderItem> change in due)
                {
                    // Preserve references held by an open reminder editor.
                    change.Key.Active = change.Value.Active;
                    change.Key.PendingAlert = change.Key.Repeat == "once"
                        ? true : change.Value.PendingAlert;
                    change.Key.DueLocal = change.Value.DueLocal;
                    change.Key.RepeatDay = change.Value.RepeatDay;
                    change.Key.RepeatMonth = change.Value.RepeatMonth;
                    change.Key.RepeatWeekdays = change.Value.RepeatWeekdays;
                    change.Key.Snoozed = change.Value.Snoozed;
                    ReminderItem alertItem = change.Key.Repeat == "once"
                        ? change.Key : newOccurrences[occurrenceIndex++];
                    _pendingReminderNames.Add(alertItem.Title);
                    _pendingReminderIds.Add(alertItem.Id);
                }
                _reminderDocument.Reminders.AddRange(newOccurrences);
                ShowDueReminderAlert();
                UpdateReminderChip();
                UpdateReminderList();
            }
            finally { _checkingDueReminders = false; }
        }

        // Rehydrate previously committed but not acknowledged notifications.
        // Rescheduled parents are never replayed; only their history occurrences are.
        private void RecoverUnacknowledgedReminders()
        {
            if (!_remindersCanSave || _reminderDocument == null) return;
            DateTime now = DateTime.Now;
            foreach (ReminderItem item in _reminderDocument.Reminders)
            {
                if (!item.PendingAlert || SnoozedAlertInQuietHours(item, now)) continue;
                if (_openReminderAlertIds.Contains(item.Id) || _pendingReminderIds.Contains(item.Id))
                    continue;
                _pendingReminderNames.Add(item.Title);
                _pendingReminderIds.Add(item.Id);
            }
            ShowDueReminderAlert();
        }

        // Closing an alert acknowledges the exact occurrences, not the next repeat.
        // A failed write leaves pendingAlert=true on disk so the next startup retries.
        private void AcknowledgeReminderAlert(IList<string> ids)
        {
            if (!_remindersCanSave || _reminderDocument == null || ids == null) return;
            HashSet<string> selected = new HashSet<string>(ids, StringComparer.Ordinal);
            ReminderDocument pending = new ReminderDocument
            {
                Version = _reminderDocument.Version,
                Reminders = new List<ReminderItem>(_reminderDocument.Reminders)
            };
            List<ReminderItem> changed = new List<ReminderItem>();
            for (int i = 0; i < pending.Reminders.Count; i++)
            {
                ReminderItem item = pending.Reminders[i];
                if (!selected.Contains(item.Id) || !item.PendingAlert) continue;
                ReminderItem copy = new ReminderItem
                {
                    Id = item.Id, Title = item.Title, DueLocal = item.DueLocal,
                    Repeat = item.Repeat, Active = item.Active, RepeatDay = item.RepeatDay,
                    RepeatMonth = item.RepeatMonth, RepeatWeekdays = item.RepeatWeekdays,
                    RepeatHours = item.RepeatHours, ParentId = item.ParentId,
                    QuietHoursEnabled = item.QuietHoursEnabled,
                    QuietStartHour = item.QuietStartHour, QuietEndHour = item.QuietEndHour,
                    PendingAlert = false, Snoozed = item.Snoozed
                };
                pending.Reminders[i] = copy;
                changed.Add(item);
            }
            if (changed.Count == 0) return;
            try { ReminderStore.Save(pending); }
            catch (Exception error) { ConfigStore.Log(error); return; }
            foreach (ReminderItem item in changed) item.PendingAlert = false;
        }

        // A fired recurring item has a separate history occurrence; snooze only changes
        // that occurrence. The original parent remains scheduled for its next period.
        private bool SnoozeReminderOccurrences(IList<string> ids, int minutes)
        {
            if (ids == null || ids.Count == 0 || minutes < 1 || minutes > 10080) return false;
            DateTime when;
            try { when = DateTime.Now.AddMinutes(minutes); }
            catch (ArgumentOutOfRangeException) { return false; }
            ReminderDocument pending = new ReminderDocument
            {
                Version = _reminderDocument.Version,
                Reminders = new List<ReminderItem>(_reminderDocument.Reminders)
            };
            List<KeyValuePair<ReminderItem, ReminderItem>> updates =
                new List<KeyValuePair<ReminderItem, ReminderItem>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                if (!seen.Add(id)) continue;
                int index = pending.Reminders.FindIndex(delegate(ReminderItem item) { return item.Id == id; });
                if (index < 0) return false;
                ReminderItem original = pending.Reminders[index];
                if (original.Repeat != "once" || original.Active) return false;
                ReminderItem quietRule = QuietRuleForOccurrence(original);
                DateTime snoozedUntil = quietRule == null ? when :
                    ReminderStore.FirstNonQuietHour(quietRule, when);
                ReminderItem updated = new ReminderItem
                {
                    Id = original.Id, Title = original.Title, Repeat = original.Repeat,
                    Active = true, DueLocal = ReminderStore.ToValue(snoozedUntil),
                    ParentId = original.ParentId,
                    RepeatDay = original.RepeatDay, RepeatMonth = original.RepeatMonth,
                    RepeatWeekdays = original.RepeatWeekdays, RepeatHours = original.RepeatHours,
                    QuietHoursEnabled = quietRule != null && quietRule.QuietHoursEnabled,
                    QuietStartHour = quietRule == null ? null : quietRule.QuietStartHour,
                    QuietEndHour = quietRule == null ? null : quietRule.QuietEndHour,
                    PendingAlert = false, Snoozed = true
                };
                pending.Reminders[index] = updated;
                updates.Add(new KeyValuePair<ReminderItem, ReminderItem>(original, updated));
            }
            if (!SaveReminders(pending)) return false;
            foreach (KeyValuePair<ReminderItem, ReminderItem> change in updates)
            {
                change.Key.Active = change.Value.Active;
                change.Key.PendingAlert = false;
                change.Key.DueLocal = change.Value.DueLocal;
                change.Key.QuietHoursEnabled = change.Value.QuietHoursEnabled;
                change.Key.QuietStartHour = change.Value.QuietStartHour;
                change.Key.QuietEndHour = change.Value.QuietEndHour;
                change.Key.Snoozed = change.Value.Snoozed;
            }
            UpdateReminderChip();
            UpdateReminderList();
            if (_config.SnoozeLastMinutes != minutes)
            {
                AppConfig settings = _config.Copy();
                settings.SnoozeLastMinutes = minutes;
                SaveConfigChanges(settings);
            }
            return true;
        }

        private static int[] ReminderQuickIntervals(AppConfig settings)
        {
            int[] configured = settings == null ? null : settings.SnoozeQuickMinutes;
            if (configured == null || configured.Length != 3) return new int[] { 10, 30, 60 };
            for (int i = 0; i < configured.Length; i++)
                if (configured[i] < 1 || configured[i] > 10080) return new int[] { 10, 30, 60 };
            return (int[])configured.Clone();
        }

        private static string SnoozeDurationText(int minutes)
        {
            if (minutes % 1440 == 0) return (minutes / 1440) + " дн.";
            if (minutes % 60 == 0) return (minutes / 60) + " ч.";
            return minutes + " мин.";
        }

        private int? ShowCustomSnoozeInterval(Window owner)
        {
            Window dialog = NewReminderWindow("Свой интервал", 410, 255);
            dialog.Owner = owner;
            StackPanel root = new StackPanel { Margin = new Thickness(19) };
            dialog.Content = root;
            root.Children.Add(Text("Отложить на", 16, WhiteBrush));
            TextBox amount = new TextBox { Text = "10", MinHeight = 35,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 9, 0, 8) };
            root.Children.Add(amount);
            ComboBox unit = new ComboBox { MinHeight = 35 };
            unit.Items.Add(new ComboBoxItem { Content = "Минут", Tag = 1 });
            unit.Items.Add(new ComboBoxItem { Content = "Часов", Tag = 60 });
            unit.Items.Add(new ComboBoxItem { Content = "Дней", Tag = 1440 });
            unit.SelectedIndex = 0;
            root.Children.Add(unit);
            TextBlock validation = Text("Максимум 7 дней.", 11, MutedBrush);
            validation.Margin = new Thickness(0, 8, 0, 7);
            root.Children.Add(validation);
            WrapPanel controls = new WrapPanel();
            root.Children.Add(controls);
            int? chosen = null;
            Button ok = ActionButton("Отложить", true);
            ok.IsDefault = true;
            ok.Click += delegate
            {
                int value;
                int multiplier = (int)((ComboBoxItem)unit.SelectedItem).Tag;
                if (!Int32.TryParse(amount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                    value < 1 || value > 10080 / multiplier)
                {
                    validation.Text = "Укажи положительное число: максимум 7 дней.";
                    validation.Foreground = FluentUi.Amber;
                    return;
                }
                chosen = value * multiplier;
                dialog.DialogResult = true;
            };
            controls.Children.Add(ok);
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            controls.Children.Add(cancel);
            return dialog.ShowDialog() == true ? chosen : null;
        }

        private void ConfigureQuickSnoozeIntervals(Window owner)
        {
            Window dialog = NewReminderWindow("Быстрые интервалы", 420, 325);
            dialog.Owner = owner;
            DockPanel layout = new DockPanel { Margin = new Thickness(20) };
            dialog.Content = layout;
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);
            StackPanel panel = new StackPanel();
            ScrollViewer intervalScroll = new ScrollViewer { Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            FluentUi.StyleSettingsScroll(intervalScroll);
            layout.Children.Add(intervalScroll);
            panel.Children.Add(Text("Три кнопки в окне напоминания (в минутах)", 13, WhiteBrush));
            int[] current = ReminderQuickIntervals(_config);
            TextBox[] boxes = new TextBox[3];
            for (int i = 0; i < 3; i++)
            {
                boxes[i] = new TextBox { Text = current[i].ToString(CultureInfo.InvariantCulture),
                    MinHeight = 34, MaxLength = 5, VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 7, 0, 0) };
                panel.Children.Add(boxes[i]);
            }
            TextBlock error = Text("Допустимый интервал: от 1 минуты до 7 дней.", 11, MutedBrush);
            error.Margin = new Thickness(0, 10, 0, 8);
            panel.Children.Add(error);
            Button save = ActionButton("Сохранить", true);
            save.Click += delegate
            {
                int[] values = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    if (!Int32.TryParse(boxes[i].Text, NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) ||
                        values[i] < 1 || values[i] > 10080)
                    {
                        error.Text = "Каждая кнопка: от 1 до 10080 минут.";
                        error.Foreground = FluentUi.Amber;
                        return;
                    }
                }
                AppConfig pending = _config.Copy();
                pending.SnoozeQuickMinutes = values;
                if (SaveConfigChanges(pending)) dialog.DialogResult = true;
            };
            actions.Children.Add(save);
            Button cancel = ActionButton("Отмена", false);
            cancel.Click += delegate { dialog.DialogResult = false; };
            actions.Children.Add(cancel);
            dialog.ShowDialog();
        }

        private void ShowDueReminderAlert()
        {
            if (_reminderAlertWindow != null) return;
            if (_pendingReminderIds.Count == 0) return;
            // Decide what may be displayed only at the point of actual delivery,
            // so queued and recovered alerts cannot bypass quiet hours.
            DateTime now = DateTime.Now;
            List<string> titles = new List<string>();
            List<string> ids = new List<string>();
            List<string> suppressed = new List<string>();
            for (int i = 0; i < _pendingReminderIds.Count; i++)
            {
                string id = _pendingReminderIds[i];
                ReminderItem occurrence = _reminderDocument.Reminders.Find(
                    delegate(ReminderItem item) { return item != null && item.Id == id; });
                // Keep pendingAlert=true for the next permitted delivery window.
                if (SnoozedAlertInQuietHours(occurrence, now)) continue;
                if (SuppressHourlyAlert(occurrence, now)) suppressed.Add(id);
                else
                {
                    ids.Add(id);
                    titles.Add(_pendingReminderNames[i]);
                }
            }
            _pendingReminderNames.Clear();
            _pendingReminderIds.Clear();
            // Acknowledge only the skipped hourly events; regular once alerts
            // continue to be delivered normally.
            if (suppressed.Count > 0) AcknowledgeReminderAlert(suppressed);
            if (ids.Count == 0)
            {
                UpdateReminderList();
                return;
            }
            _openReminderAlertIds.UnionWith(ids);
            NotificationAudio.PlayReminder(_config);
            try
            {
                if (_trayIcon != null)
                    _trayIcon.ShowBalloonTip(8000, "AppShelf · Напоминание",
                        titles.Count == 1 ? titles[0] : "Сработало напоминаний: " + titles.Count,
                        System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception error) { ConfigStore.Log(error); }

            Window dialog = NewReminderWindow("Напоминания", 470, 325);
            _reminderAlertWindow = dialog;
            dialog.Topmost = true;
            dialog.ShowInTaskbar = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            DockPanel layout = new DockPanel { Margin = new Thickness(20) };
            dialog.Content = layout;
            StackPanel actions = new StackPanel();
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);
            DockPanel content = new DockPanel();
            layout.Children.Add(content);
            TextBlock header = Text("Время напоминания", 21, WhiteBrush);
            header.FontWeight = FontWeights.Bold;
            header.Margin = new Thickness(0, 0, 0, 12);
            DockPanel.SetDock(header, Dock.Top);
            content.Children.Add(header);
            ScrollViewer scroll = new ScrollViewer { MaxHeight = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FluentUi.StyleSettingsScroll(scroll);
            TextBlock messages = Text(String.Join("\n", titles.ToArray()), 14, WhiteBrush);
            messages.TextWrapping = TextWrapping.Wrap;
            scroll.Content = messages;
            content.Children.Add(scroll);
            Button done = ActionButton("Готово", true);
            done.Margin = new Thickness(0, 14, 0, 9);
            done.Click += delegate { dialog.Close(); };
            actions.Children.Add(done);
            WrapPanel controls = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            actions.Children.Add(controls);
            bool snoozed = false;
            Action<int> snoozeFor = delegate(int minutes)
            {
                if (snoozed || !_remindersCanSave) return;
                if (!SnoozeReminderOccurrences(ids, minutes)) return;
                snoozed = true;
                dialog.Close();
            };
            int[] quick = ReminderQuickIntervals(_config);
            for (int i = 0; i < quick.Length; i++)
            {
                int minutes = quick[i];
                Button button = ActionButton(SnoozeDurationText(minutes), false);
                button.Margin = new Thickness(0, 0, 5, 5);
                button.ToolTip = "Отложить текущее срабатывание на " + SnoozeDurationText(minutes);
                button.Click += delegate { snoozeFor(minutes); };
                controls.Children.Add(button);
            }
            Button more = ActionButton("Ещё…", false);
            controls.Children.Add(more);
            ContextMenu menu = new ContextMenu();
            int last = _config.SnoozeLastMinutes >= 1 && _config.SnoozeLastMinutes <= 10080
                ? _config.SnoozeLastMinutes : 10;
            MenuItem lastItem = new MenuItem { Header = "Последний: " + SnoozeDurationText(last) };
            lastItem.Click += delegate { snoozeFor(last); };
            menu.Items.Add(lastItem);
            foreach (int minutes in new int[] { 5, 15, 120, 180, 360, 720, 1440 })
            {
                int choice = minutes;
                MenuItem preset = new MenuItem { Header = SnoozeDurationText(choice) };
                preset.Click += delegate { snoozeFor(choice); };
                menu.Items.Add(preset);
            }
            menu.Items.Add(new Separator());
            MenuItem custom = new MenuItem { Header = "Свой интервал…" };
            custom.Click += delegate
            {
                int? chosen = ShowCustomSnoozeInterval(dialog);
                if (chosen.HasValue) snoozeFor(chosen.Value);
            };
            menu.Items.Add(custom);
            MenuItem configure = new MenuItem { Header = "Настроить быстрые кнопки…" };
            configure.Click += delegate { ConfigureQuickSnoozeIntervals(dialog); };
            menu.Items.Add(configure);
            more.ContextMenu = menu;
            more.Click += delegate { menu.PlacementTarget = more; menu.IsOpen = true; };
            dialog.Closed += delegate
            {
                // App shutdown is not user acknowledgement. Keep the alert durable.
                if (!_remindersShuttingDown && !snoozed) AcknowledgeReminderAlert(ids);
                _reminderAlertWindow = null;
                _openReminderAlertIds.Clear();
                UpdateReminderList();
                if (_pendingReminderNames.Count > 0)
                    Dispatcher.BeginInvoke(new Action(ShowDueReminderAlert));
            };
            dialog.Show();
            dialog.Activate();
        }

        private static Window NewReminderWindow(string title, double width, double height)
        {
            Window dialog = new Window
            {
                Title = "AppShelf — " + title, Width = width, Height = height,
                MinWidth = 390, MinHeight = 240, Background = BackgroundBrush,
                Foreground = WhiteBrush, FontFamily = new FontFamily("Segoe UI"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            FluentUi.StyleReminderInputs(dialog);
            return dialog;
        }


        private bool ConfirmReminderAction(Window owner, string message, string buttonText)
        {
            Window dialog = NewReminderWindow("Подтверждение", 440, 255);
            dialog.Owner = owner ?? this;
            dialog.MinWidth = 390;
            DockPanel body = new DockPanel { Margin = new Thickness(21) };
            dialog.Content = body;
            WrapPanel actions = new WrapPanel();
            DockPanel.SetDock(actions, Dock.Bottom);
            body.Children.Add(actions);
            TextBlock prompt = Text(message, 15, WhiteBrush);
            prompt.TextWrapping = TextWrapping.Wrap;
            prompt.Margin = new Thickness(0, 5, 0, 18);
            ScrollViewer promptScroll = new ScrollViewer { Content = prompt,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            FluentUi.StyleSettingsScroll(promptScroll);
            body.Children.Add(promptScroll);
            Button confirm = ActionButton(buttonText, true);
            confirm.Width = 96;
            confirm.Height = 38;
            confirm.Margin = new Thickness(0, 0, 8, 0);
            confirm.Click += delegate { dialog.DialogResult = true; };
            actions.Children.Add(confirm);
            Button cancel = ActionButton("Отмена", false);
            cancel.Width = 96;
            cancel.Height = 38;
            cancel.Margin = new Thickness(0);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            actions.Children.Add(cancel);
            return dialog.ShowDialog() == true;
        }

        private static bool IsCompletedReminder(ReminderItem item)
        {
            return item != null && !item.Active && item.Repeat == "once";
        }

        private bool CanRemoveCompletedReminder(ReminderItem item)
        {
            return IsCompletedReminder(item) && !_openReminderAlertIds.Contains(item.Id) &&
                !_pendingReminderIds.Contains(item.Id);
        }

        private bool RemoveReminderIds(HashSet<string> ids)
        {
            if (ids == null || ids.Count == 0 || !_remindersCanSave) return false;
            // Recheck after modal confirmation: a selected history row may have been
            // snoozed while the confirmation was open.
            ids = new HashSet<string>(ids, StringComparer.Ordinal);
            ids.RemoveWhere(delegate(string id)
            {
                ReminderItem item = _reminderDocument.Reminders.Find(delegate(ReminderItem row) { return row.Id == id; });
                return !CanRemoveCompletedReminder(item);
            });
            ReminderDocument pending = new ReminderDocument
            {
                Version = _reminderDocument.Version,
                Reminders = new List<ReminderItem>(_reminderDocument.Reminders)
            };
            int removed = pending.Reminders.RemoveAll(delegate(ReminderItem item) { return ids.Contains(item.Id); });
            if (removed == 0 || !SaveReminders(pending)) return false;
            _reminderDocument.Reminders.RemoveAll(delegate(ReminderItem item) { return ids.Contains(item.Id); });
            _selectedReminderIds.ExceptWith(ids);
            UpdateReminderChip();
            UpdateReminderList();
            return true;
        }

        private void UpdateRemoveSelectedButton()
        {
            if (_removeSelectedRemindersButton != null)
            {
                _removeSelectedRemindersButton.IsEnabled = _remindersCanSave && _selectedReminderIds.Count > 0;
                _removeSelectedRemindersButton.Content = "Удалить выбранные (" + _selectedReminderIds.Count + ")";
            }
        }

        private void ClearCompletedReminders()
        {
            if (!_remindersCanSave || _remindersWindow == null) return;
            Window dialog = NewReminderWindow("Очистить историю", 465, 290);
            dialog.Owner = _remindersWindow;
            StackPanel layout = new StackPanel { Margin = new Thickness(20) };
            dialog.Content = layout;
            TextBlock hint = Text("Удаляются только выполненные одноразовые срабатывания. Действующие повторяющиеся напоминания сохраняются.", 13, WhiteBrush);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 0, 0, 12);
            layout.Children.Add(hint);
            ComboBox scope = new ComboBox { MinHeight = 36 };
            scope.Items.Add(new ComboBoxItem { Content = "Все выполненные", Tag = 0 });
            scope.Items.Add(new ComboBoxItem { Content = "Выполненные старше 7 дней", Tag = 7 });
            scope.Items.Add(new ComboBoxItem { Content = "Выполненные старше 30 дней", Tag = 30 });
            scope.SelectedIndex = 0;
            layout.Children.Add(scope);
            TextBlock count = Text("", 12, MutedBrush);
            count.Margin = new Thickness(0, 12, 0, 14);
            layout.Children.Add(count);
            WrapPanel actions = new WrapPanel();
            layout.Children.Add(actions);
            Button delete = ActionButton("Очистить", true);
            actions.Children.Add(delete);
            Button cancel = ActionButton("Отмена", false);
            cancel.Click += delegate { dialog.Close(); };
            actions.Children.Add(cancel);
            Func<HashSet<string>> targets = delegate
            {
                int days = (int)((ComboBoxItem)scope.SelectedItem).Tag;
                DateTime cutoff = DateTime.Now.Date.AddDays(-days);
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (ReminderItem item in _reminderDocument.Reminders)
                {
                    DateTime due;
                    if (!CanRemoveCompletedReminder(item) || !ReminderStore.TryParse(item.DueLocal, out due)) continue;
                    if (days == 0 || due < cutoff) ids.Add(item.Id);
                }
                return ids;
            };
            Action refresh = delegate
            {
                int number = targets().Count;
                count.Text = "Будет удалено записей: " + number;
                delete.IsEnabled = number > 0;
            };
            scope.SelectionChanged += delegate { refresh(); };
            delete.Click += delegate
            {
                HashSet<string> ids = targets();
                if (ids.Count == 0) return;
                if (!ConfirmReminderAction(dialog, "Удалить " + ids.Count + " выполненных записей? Отменить удаление после сохранения нельзя.", "Удалить")) return;
                if (RemoveReminderIds(ids)) dialog.Close();
                else refresh();
            };
            refresh();
            dialog.ShowDialog();
        }

        private void ShowRemindersWindow()
        {
            if (_remindersWindow != null)
            {
                if (_remindersWindow.WindowState == WindowState.Minimized)
                    _remindersWindow.WindowState = WindowState.Normal;
                _remindersWindow.Activate();
                return;
            }
            Window dialog = NewReminderWindow("Напоминания", 680, 530);
            // Keep a usable list viewport below the wrapped toolbar at minimum width.
            dialog.MinHeight = 360;
            dialog.Owner = this;
            _remindersWindow = dialog;
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;
            StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            TextBlock title = Text("Напоминания", 23, WhiteBrush);
            title.FontWeight = FontWeights.Bold;
            header.Children.Add(title);
            TextBlock help = Text("Локальные уведомления. AppShelf должен быть запущен, в том числе в трее.", 12, MutedBrush);
            help.TextWrapping = TextWrapping.Wrap;
            header.Children.Add(help);
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
            header.Children.Add(actions);
            Button add = ActionButton("+  Добавить напоминание", true);
            add.IsEnabled = _remindersCanSave;
            add.Click += delegate { EditReminder(null); };
            actions.Children.Add(add);
            Button refresh = ActionButton("Обновить", false);
            refresh.Click += delegate { UpdateReminderList(); };
            actions.Children.Add(refresh);
            Button clear = ActionButton("Очистить историю…", false);
            clear.IsEnabled = _remindersCanSave;
            clear.Click += delegate { ClearCompletedReminders(); };
            actions.Children.Add(clear);
            Button select = ActionButton("Выбрать", false);
            select.Click += delegate
            {
                _reminderSelectMode = !_reminderSelectMode;
                _selectedReminderIds.Clear();
                select.Content = _reminderSelectMode ? "Отмена выбора" : "Выбрать";
                _removeSelectedRemindersButton.Visibility = _reminderSelectMode ? Visibility.Visible : Visibility.Collapsed;
                UpdateRemoveSelectedButton();
                UpdateReminderList();
            };
            actions.Children.Add(select);
            _removeSelectedRemindersButton = ActionButton("Удалить выбранные (0)", false);
            _removeSelectedRemindersButton.Visibility = Visibility.Collapsed;
            _removeSelectedRemindersButton.Click += delegate
            {
                HashSet<string> ids = new HashSet<string>(_selectedReminderIds, StringComparer.Ordinal);
                if (ids.Count == 0) return;
                if (!ConfirmReminderAction(_remindersWindow,
                    "Удалить выбранные записи (" + ids.Count + ")?", "Удалить")) return;
                RemoveReminderIds(ids);
                UpdateRemoveSelectedButton();
            };
            actions.Children.Add(_removeSelectedRemindersButton);
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FluentUi.StyleSettingsScroll(scroll);
            root.Children.Add(scroll);
            _remindersList = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            scroll.Content = _remindersList;
            dialog.Closed += delegate
            {
                _remindersWindow = null;
                _remindersList = null;
                _reminderSelectMode = false;
                _selectedReminderIds.Clear();
                _removeSelectedRemindersButton = null;
            };
            UpdateReminderList();
            dialog.Show();
        }

        private void UpdateReminderList()
        {
            if (_remindersList == null || _reminderDocument == null) return;
            _selectedReminderIds.RemoveWhere(delegate(string id)
            {
                ReminderItem item = _reminderDocument.Reminders.Find(delegate(ReminderItem row) { return row.Id == id; });
                return !CanRemoveCompletedReminder(item);
            });
            _remindersList.Children.Clear();
            UpdateRemoveSelectedButton();
            List<ReminderItem> reminders = new List<ReminderItem>(_reminderDocument.Reminders);
            reminders.Sort(delegate(ReminderItem x, ReminderItem y)
            {
                if (x.Active != y.Active) return x.Active ? -1 : 1;
                return String.CompareOrdinal(x.DueLocal, y.DueLocal);
            });
            if (reminders.Count == 0)
            {
                TextBlock none = Text("Пока нет напоминаний. Нажми «Добавить напоминание».", 14, MutedBrush);
                none.Margin = new Thickness(6, 16, 0, 0);
                _remindersList.Children.Add(none);
            }
            foreach (ReminderItem item in reminders)
            {
                Border card = new Border
                {
                    Background = TileBrush, CornerRadius = new CornerRadius(11),
                    BorderBrush = StrokeBrush, BorderThickness = new Thickness(1),
                    Padding = new Thickness(13), Margin = new Thickness(0, 0, 0, 9)
                };
                _remindersList.Children.Add(card);
                DockPanel body = new DockPanel { LastChildFill = true };
                card.Child = body;
                StackPanel commands = new StackPanel { Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(commands, Dock.Right);
                body.Children.Add(commands);
                Button edit = ActionButton("Изменить", false);
                edit.IsEnabled = _remindersCanSave;
                edit.Margin = new Thickness(8, 0, 3, 0);
                edit.Click += delegate { EditReminder(item); };
                commands.Children.Add(edit);
                Button remove = ActionButton("×", false);
                remove.IsEnabled = _remindersCanSave;
                remove.Margin = new Thickness(2, 0, 0, 0);
                remove.ToolTip = "Удалить напоминание";
                remove.Click += delegate
                {
                    if (!ConfirmReminderAction(_remindersWindow,
                        "Удалить напоминание «" + item.Title + "»?", "Удалить")) return;
                    ReminderDocument pending = new ReminderDocument
                    {
                        Version = _reminderDocument.Version,
                        Reminders = new List<ReminderItem>(_reminderDocument.Reminders)
                    };
                    if (!pending.Reminders.Remove(item) || !SaveReminders(pending)) return;
                    _reminderDocument.Reminders.Remove(item);
                    UpdateReminderChip();
                    UpdateReminderList();
                };
                commands.Children.Add(remove);
                StackPanel info = new StackPanel();
                body.Children.Add(info);
                if (_reminderSelectMode && CanRemoveCompletedReminder(item))
                {
                    CheckBox select = new CheckBox
                    {
                        Content = "Выбрать запись", IsChecked = _selectedReminderIds.Contains(item.Id),
                        Margin = new Thickness(0, 0, 0, 5), Foreground = MutedBrush
                    };
                    select.Checked += delegate { _selectedReminderIds.Add(item.Id); UpdateRemoveSelectedButton(); };
                    select.Unchecked += delegate { _selectedReminderIds.Remove(item.Id); UpdateRemoveSelectedButton(); };
                    info.Children.Add(select);
                }
                TextBlock title = Text(item.Title, 14, item.Active ? WhiteBrush : MutedBrush);
                title.FontWeight = FontWeights.SemiBold;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                info.Children.Add(title);
                DateTime due;
                ReminderStore.TryParse(item.DueLocal, out due);
                string repeat = ReminderStore.RepeatLabel(item);
                string status = item.Active ? "" : " · выполнено";
                TextBlock summary = Text(due.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) +
                    " · " + repeat + status, 11.5, MutedBrush);
                info.Children.Add(summary);
            }
        }

        private void EditReminder(ReminderItem existing)
        {
            if (!_remindersCanSave) return;
            Window owner = _remindersWindow != null ? _remindersWindow : (Window)this;
            Window dialog = NewReminderWindow(existing == null ? "Новое напоминание" : "Изменить напоминание", 440, 360);
            dialog.Owner = owner;
            dialog.MinHeight = 280;
            DockPanel root = new DockPanel { Margin = new Thickness(20) };
            dialog.Content = root;
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom);
            root.Children.Add(actions);
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            FluentUi.StyleSettingsScroll(scroll);
            root.Children.Add(scroll);
            StackPanel panel = new StackPanel();
            scroll.Content = panel;
            panel.Children.Add(Text("Текст напоминания", 12, MutedBrush));
            TextBox title = new TextBox { Name = "ReminderTitle", Margin = new Thickness(0, 5, 0, 12), MinHeight = 36,
                Padding = new Thickness(10, 4, 10, 4), VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 14, Text = existing == null ? "" : existing.Title };
            panel.Children.Add(title);
            Grid schedule = new Grid();
            schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 112 });
            schedule.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            schedule.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.Children.Add(schedule);
            schedule.Children.Add(Text("Дата", 12, MutedBrush));
            DateTime due;
            if (existing == null || !ReminderStore.TryParse(existing.DueLocal, out due))
                due = DateTime.Now.AddMinutes(30);
            DatePicker day = new DatePicker { Name = "ReminderDate", Margin = new Thickness(0, 5, 12, 12),
                Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag),
                SelectedDate = due.Date, SelectedDateFormat = DatePickerFormat.Short, MinHeight = 36 };
            Grid.SetRow(day, 1); schedule.Children.Add(day);
            TextBlock timeLabel = Text("Время (ЧЧ:ММ)", 12, MutedBrush);
            Grid.SetColumn(timeLabel, 1); schedule.Children.Add(timeLabel);
            ReminderTimePicker time = new ReminderTimePicker(due) { Margin = new Thickness(0, 5, 0, 12) };
            Grid.SetRow(time, 1); Grid.SetColumn(time, 1); schedule.Children.Add(time);
            panel.Children.Add(Text("Повторять", 12, MutedBrush));
            ComboBox repeat = new ComboBox { Name = "ReminderRepeat", MinHeight = 36, Margin = new Thickness(0, 5, 0, 0) };
            repeat.Items.Add(new ComboBoxItem { Content = "Один раз", Tag = "once" });
            repeat.Items.Add(new ComboBoxItem { Content = "Каждые N часов", Tag = "hourly" });
            repeat.Items.Add(new ComboBoxItem { Content = "Ежедневно", Tag = "daily" });
            repeat.Items.Add(new ComboBoxItem { Content = "Еженедельно", Tag = "weekly" });
            repeat.Items.Add(new ComboBoxItem { Content = "Ежемесячно", Tag = "monthly" });
            repeat.Items.Add(new ComboBoxItem { Content = "Ежегодно", Tag = "yearly" });
            repeat.Items.Add(new ComboBoxItem { Content = "По будням (Пн–Пт)", Tag = "weekdays" });
            repeat.Items.Add(new ComboBoxItem { Content = "По выходным (Сб–Вс)", Tag = "weekends" });
            repeat.Items.Add(new ComboBoxItem { Content = "По выбранным дням недели", Tag = "custom_weekdays" });
            string repeatMode = existing == null ? "once" : existing.Repeat;
            repeat.SelectedIndex = 0;
            for (int i = 0; i < repeat.Items.Count; i++)
            {
                ComboBoxItem option = repeat.Items[i] as ComboBoxItem;
                if (option != null && (string)option.Tag == repeatMode)
                {
                    repeat.SelectedIndex = i;
                    break;
                }
            }
            panel.Children.Add(repeat);
            StackPanel weekdayPicker = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
            weekdayPicker.Visibility = repeatMode == "custom_weekdays" ? Visibility.Visible : Visibility.Collapsed;
            weekdayPicker.Children.Add(Text("Дни недели", 12, MutedBrush));
            WrapPanel dayChoicesPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            weekdayPicker.Children.Add(dayChoicesPanel);
            string[] dayLabels = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
            string[] dayNames = { "Понедельник", "Вторник", "Среда", "Четверг",
                "Пятница", "Суббота", "Воскресенье" };
            int initialMask = existing != null && existing.Repeat == "custom_weekdays" &&
                existing.RepeatWeekdays.HasValue ? existing.RepeatWeekdays.Value :
                (1 << ReminderStore.WeekdayIndex(due.DayOfWeek));
            CheckBox[] weekdayChoices = new CheckBox[7];
            for (int i = 0; i < weekdayChoices.Length; i++)
            {
                CheckBox choice = new CheckBox
                {
                    Content = dayLabels[i], IsChecked = (initialMask & (1 << i)) != 0,
                    Margin = new Thickness(0, 0, 12, 8), ToolTip = dayNames[i]
                };
                System.Windows.Automation.AutomationProperties.SetName(choice, dayNames[i]);
                weekdayChoices[i] = choice;
                dayChoicesPanel.Children.Add(choice);
            }
            TextBlock weekdayHint = Text("Если дата начала не совпадает с выбранным днём, " +
                "первое срабатывание будет в ближайший выбранный день.", 11, MutedBrush);
            weekdayHint.TextWrapping = TextWrapping.Wrap;
            weekdayPicker.Children.Add(weekdayHint);
            panel.Children.Add(weekdayPicker);

            StackPanel intervalPanel = new StackPanel { Margin = new Thickness(0, 11, 0, 5) };
            intervalPanel.Visibility = repeatMode == "hourly" ? Visibility.Visible : Visibility.Collapsed;
            intervalPanel.Children.Add(Text("Интервал повторения (часов)", 12, MutedBrush));
            ComboBox repeatHours = new ComboBox
            {
                Name = "ReminderRepeatHours", Width = 100, MinHeight = 35,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 5, 0, 0)
            };
            for (int hours = 1; hours <= 24; hours++)
                repeatHours.Items.Add(new ComboBoxItem
                {
                    Content = hours.ToString(CultureInfo.InvariantCulture)
                });
            int currentHours = existing != null && existing.Repeat == "hourly"
                ? ReminderStore.HourlyInterval(existing) : 1;
            repeatHours.SelectedIndex = Math.Max(1, Math.Min(24, currentHours)) - 1;
            intervalPanel.Children.Add(repeatHours);
            panel.Children.Add(intervalPanel);

            StackPanel quietPanel = new StackPanel { Margin = new Thickness(0, 11, 0, 5) };
            quietPanel.Visibility = repeatMode == "hourly" ? Visibility.Visible : Visibility.Collapsed;
            CheckBox quietEnabled = new CheckBox
            {
                Content = "Тихие часы (не показывать уведомления)",
                IsChecked = existing != null && existing.Repeat == "hourly"
                    ? existing.QuietHoursEnabled : true,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 8)
            };
            quietPanel.Children.Add(quietEnabled);
            WrapPanel quietTimeRow = new WrapPanel();
            quietPanel.Children.Add(quietTimeRow);
            quietTimeRow.Children.Add(Text("С", 12, MutedBrush));
            ComboBox quietFrom = new ComboBox { Name = "ReminderQuietStart", Width = 92,
                MinHeight = 35, Margin = new Thickness(8, 0, 12, 0) };
            quietTimeRow.Children.Add(quietFrom);
            quietTimeRow.Children.Add(Text("до", 12, MutedBrush));
            ComboBox quietUntil = new ComboBox { Name = "ReminderQuietEnd", Width = 92,
                MinHeight = 35, Margin = new Thickness(8, 0, 0, 0) };
            quietTimeRow.Children.Add(quietUntil);
            for (int hour = 0; hour < 24; hour++)
            {
                string label = hour.ToString("D2", CultureInfo.InvariantCulture) + ":00";
                quietFrom.Items.Add(new ComboBoxItem { Content = label });
                quietUntil.Items.Add(new ComboBoxItem { Content = label });
            }
            quietFrom.SelectedIndex = existing != null && existing.QuietStartHour.HasValue &&
                existing.QuietStartHour.Value >= 0 && existing.QuietStartHour.Value < 24
                ? existing.QuietStartHour.Value : 22;
            quietUntil.SelectedIndex = existing != null && existing.QuietEndHour.HasValue &&
                existing.QuietEndHour.Value >= 0 && existing.QuietEndHour.Value < 24
                ? existing.QuietEndHour.Value : 9;
            quietTimeRow.IsEnabled = quietEnabled.IsChecked == true;
            quietEnabled.Checked += delegate { quietTimeRow.IsEnabled = true; };
            quietEnabled.Unchecked += delegate { quietTimeRow.IsEnabled = false; };
            TextBlock quietHint = Text("Срабатывания повторяются через выбранное число часов от начального времени. " +
                "Тихие часы пропускаются без изменения интервала и без накопления уведомлений.", 11, MutedBrush);
            quietHint.TextWrapping = TextWrapping.Wrap;
            quietHint.Margin = new Thickness(0, 7, 0, 0);
            quietPanel.Children.Add(quietHint);
            panel.Children.Add(quietPanel);
            repeat.SelectionChanged += delegate
            {
                ComboBoxItem selected = repeat.SelectedItem as ComboBoxItem;
                string value = selected == null ? "once" : (string)selected.Tag;
                weekdayPicker.Visibility = value == "custom_weekdays"
                    ? Visibility.Visible : Visibility.Collapsed;
                intervalPanel.Visibility = value == "hourly"
                    ? Visibility.Visible : Visibility.Collapsed;
                quietPanel.Visibility = value == "hourly"
                    ? Visibility.Visible : Visibility.Collapsed;
            };
            TextBlock validation = Text("", 12, FluentUi.Amber);
            validation.Name = "ReminderValidation"; validation.TextWrapping = TextWrapping.Wrap;
            validation.Visibility = Visibility.Collapsed; validation.Margin = new Thickness(0, 9, 0, 0);
            panel.Children.Add(validation);
            string pendingInvalidDate = null;
            day.SelectedDateChanged += delegate { pendingInvalidDate = null; };
            day.DateValidationError += delegate(object sender, DatePickerDateValidationErrorEventArgs e)
            {
                e.ThrowException = false;
                string entered = e.Text;
                pendingInvalidDate = entered;
                validation.Text = "Укажи существующую дату или выбери её в календаре.";
                validation.Visibility = Visibility.Visible;
                // DatePicker otherwise silently restores its previous valid date after a parse error.
                day.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (pendingInvalidDate != entered || !dialog.IsLoaded) return;
                    TextBox input = day.Template.FindName("PART_TextBox", day) as TextBox;
                    if (input != null) input.Text = entered;
                    pendingInvalidDate = null;
                }));
            };
            bool calendarInputHooked = false;
            day.CalendarOpened += delegate
            {
                if (calendarInputHooked) return;
                System.Windows.Controls.Primitives.Popup popup = day.Template.FindName("PART_Popup", day) as System.Windows.Controls.Primitives.Popup;
                System.Windows.Controls.Calendar calendar = popup == null ? null : popup.Child as System.Windows.Controls.Calendar;
                if (calendar == null) return;
                calendarInputHooked = true;
                Action<DependencyObject> commitCalendarDate = delegate(DependencyObject source)
                {
                    while (source != null && !(source is System.Windows.Controls.Primitives.CalendarDayButton))
                        source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
                    System.Windows.Controls.Primitives.CalendarDayButton cell = source as System.Windows.Controls.Primitives.CalendarDayButton;
                    if (cell == null || cell.IsBlackedOut || !cell.IsEnabled || !(cell.DataContext is DateTime) ||
                        day.SelectedDate != (DateTime)cell.DataContext) return;
                    // Selecting the already selected day does not raise SelectedDateChanged.
                    pendingInvalidDate = null;
                    day.Text = day.SelectedDate.Value.ToString("d", CultureInfo.CurrentCulture);
                };
                calendar.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                    new RoutedEventHandler(delegate(object sender, RoutedEventArgs e) { commitCalendarDate(e.OriginalSource as DependencyObject); }), true);
                calendar.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
                    new MouseButtonEventHandler(delegate(object sender, MouseButtonEventArgs e) { commitCalendarDate(e.OriginalSource as DependencyObject); }), true);
            };
            Button save = ActionButton("Сохранить", true);
            save.IsDefault = true;
            actions.Children.Add(save);
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            actions.Children.Add(cancel);
            cancel.Click += delegate { dialog.Close(); };
            bool saved = false;
            save.Click += delegate
            {
                if (saved) return;
                validation.Visibility = Visibility.Collapsed;
                TimeSpan selectedTime;
                if (String.IsNullOrWhiteSpace(title.Text) || title.Text.Trim().Length > 200)
                {
                    validation.Text = "Укажи текст напоминания: от 1 до 200 символов.";
                    validation.Visibility = Visibility.Visible;
                    return;
                }
                TextBox dateInput = day.Template == null ? null : day.Template.FindName("PART_TextBox", day) as TextBox;
                string enteredDate = pendingInvalidDate ?? (dateInput == null ? day.Text : dateInput.Text);
                DateTime calendarDate;
                if (!DateTime.TryParse(enteredDate, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out calendarDate))
                {
                    validation.Text = "Укажи существующую дату или выбери её в календаре.";
                    validation.Visibility = Visibility.Visible;
                    return;
                }
                if (!time.TryGetTime(out selectedTime))
                {
                    validation.Text = "Укажи часы от 00 до 23 и минуты от 00 до 59.";
                    validation.Visibility = Visibility.Visible;
                    return;
                }
                DateTime chosen = calendarDate.Date.Add(selectedTime);
                if (chosen <= DateTime.Now)
                {
                    validation.Text = "Дата и время должны быть в будущем.";
                    validation.Visibility = Visibility.Visible;
                    return;
                }
                ComboBoxItem mode = repeat.SelectedItem as ComboBoxItem;
                string recurrence = mode == null ? "once" : (string)mode.Tag;
                int selectedWeekdays = 0;
                if (recurrence == "custom_weekdays")
                {
                    for (int i = 0; i < weekdayChoices.Length; i++)
                        if (weekdayChoices[i].IsChecked == true) selectedWeekdays |= 1 << i;
                    if (selectedWeekdays == 0)
                    {
                        validation.Text = "Выбери хотя бы один день недели.";
                        validation.Visibility = Visibility.Visible;
                        return;
                    }
                }
                bool quietIsOn = recurrence == "hourly" && quietEnabled.IsChecked == true;
                if (quietIsOn && quietFrom.SelectedIndex == quietUntil.SelectedIndex)
                {
                    validation.Text = "Начало и конец тихих часов должны различаться.";
                    validation.Visibility = Visibility.Visible;
                    return;
                }
                if (ReminderStore.IsWeekdayRepeat(recurrence))
                {
                    try
                    {
                        chosen = ReminderStore.FirstSelectedWeekday(chosen,
                            ReminderStore.WeekdayMask(recurrence, selectedWeekdays));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        validation.Text = "Следующий выбранный день выходит за допустимый диапазон дат. Выбери более раннюю дату.";
                        validation.Visibility = Visibility.Visible;
                        return;
                    }
                }
                ReminderItem edited = new ReminderItem
                {
                    Id = existing == null ? Guid.NewGuid().ToString("N") : existing.Id,
                    Title = title.Text.Trim(), DueLocal = ReminderStore.ToValue(chosen),
                    Repeat = recurrence, Active = true,
                    RepeatWeekdays = recurrence == "custom_weekdays" ? (int?)selectedWeekdays : null,
                    RepeatHours = recurrence == "hourly" && repeatHours.SelectedIndex > 0
                        ? (int?)(repeatHours.SelectedIndex + 1) : null,
                    QuietHoursEnabled = quietIsOn,
                    QuietStartHour = quietIsOn ? (int?)quietFrom.SelectedIndex : null,
                    QuietEndHour = quietIsOn ? (int?)quietUntil.SelectedIndex : null,
                    ParentId = existing == null ? null : existing.ParentId
                };
                if (recurrence == "hourly" && quietIsOn)
                {
                    try
                    {
                        chosen = ReminderStore.FirstAllowedHourly(edited, chosen);
                        edited.DueLocal = ReminderStore.ToValue(chosen);
                    }
                    catch (InvalidOperationException)
                    {
                        validation.Text = "При этом интервале все срабатывания попадают в тихие часы. " +
                            "Измени время начала, интервал или тихие часы.";
                        validation.Visibility = Visibility.Visible;
                        return;
                    }
                }
                if (recurrence == "monthly" || recurrence == "yearly")
                {
                    DateTime previousDate;
                    bool keepAnchor = existing != null && existing.Repeat == recurrence &&
                        ReminderStore.TryParse(existing.DueLocal, out previousDate) && previousDate.Date == chosen.Date;
                    edited.RepeatDay = keepAnchor ? existing.RepeatDay ?? chosen.Day : chosen.Day;
                    if (recurrence == "yearly") edited.RepeatMonth = keepAnchor ? existing.RepeatMonth ?? chosen.Month : chosen.Month;
                }
                ReminderDocument pending = new ReminderDocument
                {
                    Version = _reminderDocument.Version,
                    Reminders = new List<ReminderItem>(_reminderDocument.Reminders)
                };
                if (existing == null) pending.Reminders.Add(edited);
                else
                {
                    int index = pending.Reminders.IndexOf(existing);
                    if (index < 0) return;
                    pending.Reminders[index] = edited;
                }
                if (!SaveReminders(pending)) return;
                saved = true;
                if (existing == null)
                {
                    _reminderDocument.Reminders.Add(edited);
                }
                else
                {
                    existing.Title = edited.Title;
                    existing.DueLocal = edited.DueLocal;
                    existing.Repeat = edited.Repeat;
                    existing.Active = edited.Active;
                    existing.RepeatDay = edited.RepeatDay;
                    existing.RepeatMonth = edited.RepeatMonth;
                    existing.RepeatWeekdays = edited.RepeatWeekdays;
                    existing.RepeatHours = edited.RepeatHours;
                    existing.QuietHoursEnabled = edited.QuietHoursEnabled;
                    existing.QuietStartHour = edited.QuietStartHour;
                    existing.QuietEndHour = edited.QuietEndHour;
                }
                UpdateReminderChip();
                UpdateReminderList();
                dialog.Close();
            };
            dialog.Loaded += delegate { title.Focus(); title.CaretIndex = title.Text.Length; };
            dialog.ShowDialog();
        }
    }
}
