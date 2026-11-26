// AppShelf - native WPF launcher for Windows.
// Compatible with C# 5 / .NET Framework 4.8 and .NET 8 WPF.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Security.Principal;
using System.Globalization;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AppShelf
{
    [DataContract]
    public sealed class AppItem
    {
        // Transient link for staged edits; not part of the DataContract.
        private AppItem _original;

        internal AppItem Copy()
        {
            AppItem copy = (AppItem)MemberwiseClone();
            copy._original = this;
            return copy;
        }

        internal AppItem ApplySavedCopy()
        {
            if (_original == null) return this;
            _original.Path = Path;
            _original.Name = Name;
            _original.RunAsAdmin = RunAsAdmin;
            _original.Group = Group;
            _original.SourcePath = SourcePath;
            _original.Kind = Kind;
            return _original;
        }

        [DataMember(Name = "path")]
        public string Path;

        [DataMember(Name = "name")]
        public string Name;

        [DataMember(Name = "runAsAdmin", EmitDefaultValue = false)]
        public bool RunAsAdmin;

        [DataMember(Name = "group", EmitDefaultValue = false)]
        public string Group;

        [DataMember(Name = "sourcePath", EmitDefaultValue = false)]
        public string SourcePath;

        // Optional marker: old apps.json entries remain unchanged.
        [DataMember(Name = "kind", EmitDefaultValue = false)]
        public string Kind;
    }

    // Only placement metadata is persisted: no snapshots, commands or trust flags.
    [DataContract]
    public sealed class PluginWidgetPlacementConfig
    {
        [DataMember(Name = "pluginId")] public string PluginId;
        [DataMember(Name = "widgetId")] public string WidgetId;
        [DataMember(Name = "version")] public string Version;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "size")] public string Size;
        [DataMember(Name = "row")] public int Row;
        [DataMember(Name = "column")] public int Column;
        internal PluginWidgetPlacementConfig Copy() { return (PluginWidgetPlacementConfig)MemberwiseClone(); }
    }

    [DataContract]
    public sealed class PluginWidgetGroupConfig
    {
        [DataMember(Name = "number")] public int Number;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "zone")] public string Zone;
        [DataMember(Name = "order")] public int Order;
        [DataMember(Name = "collapsed", EmitDefaultValue = false)] public bool Collapsed;
        [DataMember(Name = "widgets")] public List<PluginWidgetPlacementConfig> Widgets;
        internal PluginWidgetGroupConfig Copy()
        {
            PluginWidgetGroupConfig copy = (PluginWidgetGroupConfig)MemberwiseClone();
            copy.Widgets = Widgets == null ? null : Widgets.ConvertAll(delegate(PluginWidgetPlacementConfig p) { return p == null ? null : p.Copy(); });
            return copy;
        }
        private static bool Valid(string text, int max)
        {
            return !String.IsNullOrWhiteSpace(text) && text.Length <= max && text.IndexOf('\0') < 0;
        }
        private static bool Match(string text, string pattern)
        {
            return text != null && System.Text.RegularExpressions.Regex.IsMatch(text, pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        private static bool ValidPlacementIdentity(PluginWidgetPlacementConfig p)
        {
            return p != null && Valid(p.PluginId, 100) &&
                Match(p.PluginId, @"\A[a-z][a-z0-9-]{0,31}(?:\.[a-z][a-z0-9-]{0,31}){1,3}\z") &&
                Match(p.WidgetId, @"\A[a-z][a-z0-9_.-]{0,63}\z") &&
                Match(p.Version, @"\A(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z") &&
                Valid(p.Title, 100);
        }

        internal static void Validate(List<PluginWidgetGroupConfig> groups, List<PluginWidgetPlacementConfig> indicators = null)
        {
            HashSet<string> indicatorKeys = new HashSet<string>(StringComparer.Ordinal);
            if (indicators != null)
            {
                if (indicators.Count > 6) throw new InvalidDataException("Слишком много индикаторов плагинов.");
                foreach (PluginWidgetPlacementConfig p in indicators)
                {
                    if (!ValidPlacementIdentity(p) || p.Size != "indicator" || p.Row != 0 || p.Column != 0 ||
                        !indicatorKeys.Add(p.PluginId + "\n" + p.WidgetId))
                        throw new InvalidDataException("Некорректный индикатор плагина.");
                }
            }
            if (groups == null) return;
            if (groups.Count > 32) throw new InvalidDataException("Слишком много групп виджетов.");
            HashSet<int> numbers = new HashSet<int>();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PluginWidgetGroupConfig group in groups)
            {
                if (group == null || group.Number <= 0 || group.Number > 1000000000 || !numbers.Add(group.Number) ||
                    !Valid(group.Title, 100) || !Match(group.Zone, @"\A(top|middle|bottom)-(left|center|right)\z") ||
                    group.Order < 0 || group.Widgets == null || group.Widgets.Count < 1 || group.Widgets.Count > 4)
                    throw new InvalidDataException("Некорректная группа виджетов.");
                bool[,] cells = new bool[2, 2];
                foreach (PluginWidgetPlacementConfig p in group.Widgets)
                {
                    if (!ValidPlacementIdentity(p) || !keys.Add(p.PluginId + "\n" + p.WidgetId) ||
                        keys.Count > 32 || indicatorKeys.Contains(p.PluginId + "\n" + p.WidgetId) ||
                        (p.Size != "full" && p.Size != "half-horizontal" && p.Size != "half-vertical" && p.Size != "quarter"))
                        throw new InvalidDataException("Некорректное размещение виджета.");
                    int rows = p.Size == "full" || p.Size == "half-vertical" ? 2 : 1;
                    int columns = p.Size == "full" || p.Size == "half-horizontal" ? 2 : 1;
                    if (p.Row < 0 || p.Row > 2 - rows || p.Column < 0 || p.Column > 2 - columns)
                        throw new InvalidDataException("Виджет выходит за границы группы.");
                    for (int row = p.Row; row < p.Row + rows; row++)
                        for (int col = p.Column; col < p.Column + columns; col++)
                        {
                            if (cells[row, col]) throw new InvalidDataException("Виджеты пересекаются.");
                            cells[row, col] = true;
                        }
                }
            }
        }
    }


    // Free canvas coordinates are measured in grid cells, not physical pixels.
    // The same geometry is used for application and plugin widget cards.
    [DataContract]
    public sealed class FreeCanvasRect
    {
        [DataMember(Name = "x")] public int X;
        [DataMember(Name = "y")] public int Y;
        [DataMember(Name = "width")] public int Width;
        [DataMember(Name = "height")] public int Height;
        [DataMember(Name = "manualHeight", EmitDefaultValue = false)] public int ManualHeight;

        public FreeCanvasRect(int x, int y, int width, int height)
        { X = x; Y = y; Width = width; Height = height; }

        public FreeCanvasRect Copy()
        { return new FreeCanvasRect(X, Y, Width, Height) { ManualHeight = ManualHeight }; }
    }

    public static class FreeCanvasLayout
    {
        // Bound persisted coordinates and arithmetic even for malformed profiles.
        public const int MaxCells = 4096;

        public static bool IsValid(FreeCanvasRect rect)
        {
            return rect != null && rect.X >= 0 && rect.Y >= 0 &&
                rect.Width > 0 && rect.Height > 0 && rect.ManualHeight >= 0 && rect.ManualHeight <= rect.Height &&
                rect.Width <= MaxCells && rect.Height <= MaxCells &&
                rect.X <= MaxCells - rect.Width && rect.Y <= MaxCells - rect.Height;
        }

        public static bool Intersects(FreeCanvasRect a, FreeCanvasRect b)
        {
            return IsValid(a) && IsValid(b) &&
                a.X < b.X + b.Width && b.X < a.X + a.Width &&
                a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
        }

        public static bool CanPlace(FreeCanvasRect candidate, IEnumerable<FreeCanvasRect> occupied)
        {
            if (!IsValid(candidate)) return false;
            if (occupied != null)
                foreach (FreeCanvasRect other in occupied)
                    if (Intersects(candidate, other)) return false;
            return true;
        }

        // Return the earliest free position, top to bottom, then left to right.
        // It is sufficient to inspect y=0 and existing bottom edges: a free
        // rectangle can be moved upwards until it touches one of those edges.
        // Jump across intersecting rectangles instead of scanning every cell.
        // Never mutate caller-owned positions (including on a failed placement).
        public static FreeCanvasRect FindFirstFree(int width, int height, int columns,
            IEnumerable<FreeCanvasRect> occupied)
        {
            if (columns <= 0 || columns > MaxCells || width <= 0 || width > columns ||
                height <= 0 || height > MaxCells) return null;
            List<FreeCanvasRect> cards = new List<FreeCanvasRect>();
            SortedSet<int> rows = new SortedSet<int>();
            rows.Add(0);
            if (occupied != null)
                foreach (FreeCanvasRect rect in occupied)
                {
                    if (!IsValid(rect)) continue;
                    cards.Add(rect);
                    if (rect.Y + rect.Height <= MaxCells - height)
                        rows.Add(rect.Y + rect.Height);
                }

            foreach (int y in rows)
            {
                int x = 0;
                while (x <= columns - width)
                {
                    FreeCanvasRect candidate = new FreeCanvasRect(x, y, width, height);
                    int next = x;
                    foreach (FreeCanvasRect other in cards)
                        if (Intersects(candidate, other))
                            next = Math.Max(next, other.X + other.Width);
                    if (next == x) return candidate;
                    x = next;
                }
            }
            return null;
        }

        public static Dictionary<string, FreeCanvasRect> SelectPositions(Dictionary<string, FreeCanvasRect> source, bool widgets)
        {
            Dictionary<string, FreeCanvasRect> result = new Dictionary<string, FreeCanvasRect>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
                foreach (KeyValuePair<string, FreeCanvasRect> item in source)
                    if (item.Key.StartsWith(widgets ? "widget:" : "app:", StringComparison.OrdinalIgnoreCase) && IsValid(item.Value))
                        result[item.Key] = item.Value.Copy();
            return result;
        }

        public static Dictionary<string, FreeCanvasRect> MergePositions(
            Dictionary<string, FreeCanvasRect> apps, int appStep,
            Dictionary<string, FreeCanvasRect> widgets, int widgetStep, int targetStep)
        {
            Dictionary<string, FreeCanvasRect> result = SelectPositions(apps, false);
            foreach (string key in new List<string>(result.Keys))
                result[key] = ConvertStep(result[key], appStep, targetStep);
            foreach (KeyValuePair<string, FreeCanvasRect> item in SelectPositions(widgets, true))
                result[item.Key] = ConvertStep(item.Value, widgetStep, targetStep);
            return result.Count == 0 ? null : result;
        }

        // Produce one atomic layout, never mutate input positions. Alignment
        // keeps the original row when possible; otherwise moves down to avoid a collision.
        public static Dictionary<string, FreeCanvasRect> Arrange(
            Dictionary<string, FreeCanvasRect> source, string command, int columns)
        {
            if (source == null || columns <= 0 || columns > MaxCells ||
                (command != "vertical" && command != "rows" && command != "compact" &&
                 command != "left" && command != "right")) return null;
            List<KeyValuePair<string, FreeCanvasRect>> ordered = new List<KeyValuePair<string, FreeCanvasRect>>(source);
            ordered.RemoveAll(delegate(KeyValuePair<string, FreeCanvasRect> item) { return !IsValid(item.Value); });
            ordered.Sort(delegate(KeyValuePair<string, FreeCanvasRect> a, KeyValuePair<string, FreeCanvasRect> b) {
                int compare = a.Value.Y.CompareTo(b.Value.Y);
                if (compare == 0) compare = a.Value.X.CompareTo(b.Value.X);
                return compare != 0 ? compare : StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
            });
            foreach (KeyValuePair<string, FreeCanvasRect> item in ordered) columns = Math.Max(columns, item.Value.Width);
            List<FreeCanvasRect> occupied = new List<FreeCanvasRect>();
            Dictionary<string, FreeCanvasRect> result = new Dictionary<string, FreeCanvasRect>(StringComparer.OrdinalIgnoreCase);
            int x = 0, y = 0, rowHeight = 0;
            foreach (KeyValuePair<string, FreeCanvasRect> item in ordered)
            {
                FreeCanvasRect old = item.Value, rect;
                if (command == "compact")
                    rect = FindFirstFree(old.Width, old.Height, columns, occupied);
                else if (command == "left" || command == "right")
                {
                    int aligned = command == "left" ? 0 : columns - old.Width;
                    int top = old.Y;
                    while (true)
                    {
                        rect = new FreeCanvasRect(aligned, top, old.Width, old.Height);
                        if (!IsValid(rect)) return null;
                        int next = top;
                        foreach (FreeCanvasRect other in occupied)
                            if (Intersects(rect, other)) next = Math.Max(next, other.Y + other.Height);
                        if (next == top) break;
                        top = next;
                    }
                }
                else
                {
                    if (command == "vertical" || x + old.Width > columns)
                    { x = 0; y += rowHeight; rowHeight = 0; }
                    rect = new FreeCanvasRect(x, y, old.Width, old.Height);
                    x += old.Width; rowHeight = Math.Max(rowHeight, old.Height);
                }
                if (!CanPlace(rect, occupied)) return null;
                rect.ManualHeight = old.ManualHeight;
                occupied.Add(rect);
                result.Add(item.Key, rect);
            }
            return result;
        }

        public static string NormalizeViewportMode(string mode)
        { return mode == "scroll" || mode == "scale" || mode == "collapse" ? mode : "adaptive"; }

        public static int NormalizeStep(int? value)
        { return value == 10 || value == 20 || value == 30 || value == 40 ? value.Value : 20; }

        public static FreeCanvasRect ConvertStep(FreeCanvasRect rect, int oldStep, int newStep)
        {
            if (!IsValid(rect)) return null;
            oldStep = NormalizeStep(oldStep); newStep = NormalizeStep(newStep);
            if (oldStep == newStep) return rect.Copy();
            int width = Math.Max(1, Snap((double)rect.Width * oldStep, newStep));
            int height = Math.Max(1, Snap((double)rect.Height * oldStep, newStep));
            return new FreeCanvasRect(Math.Min(MaxCells - width, Snap((double)rect.X * oldStep, newStep)),
                Math.Min(MaxCells - height, Snap((double)rect.Y * oldStep, newStep)), width, height)
            { ManualHeight = Math.Min(height, Snap((double)rect.ManualHeight * oldStep, newStep)) };
        }

        public static int Snap(double dip, int step)
        {
            if (step <= 0 || Double.IsNaN(dip) || Double.IsInfinity(dip))
                throw new ArgumentOutOfRangeException(step <= 0 ? "step" : "dip");
            return (int)Math.Min(MaxCells, Math.Max(0, Math.Round(dip / step,
                MidpointRounding.AwayFromZero)));
        }
    }

    // Shared board: both kinds of group are measured and packed by the same
    // layout pass. Saved geometry is never rewritten by a measure/arrange pass.
    internal sealed class FreeCanvasBoard : Panel
    {
        internal const int Step = 20;
        internal int CellStep { get; private set; }
        private int InitialColumns { get { return (int)Math.Ceiling(800.0 / CellStep); } }
        private int MinimumColumns { get { return (int)Math.Ceiling(200.0 / CellStep); } }
        private const double Gap = 8;
        private sealed class Entry
        {
            internal string Key;
            internal Border Card;
            internal System.Windows.Controls.Primitives.Thumb Grip;
            internal Button Compact;
            internal bool TemporarilyExpanded;
            internal Rect DisplayRect, CompactRect;
            internal int Columns;
            internal FreeCanvasRect Preferred, Effective;
        }
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Border preview;
        private bool showGrid;
        private FreeCanvasRect previewRect;
        private string dragKey;
        private Vector dragOffset;
        private double viewportWidth = Double.PositiveInfinity;
        private string viewportMode = "scroll";
        private string activeMode = "scroll";
        private double displayScale = 1;
        internal bool IsTemporaryLayout { get { return activeMode == "adaptive" || activeMode == "collapse" || displayScale < 0.999; } }
        internal double DisplayScale { get { return displayScale; } }
        internal Func<string, FreeCanvasRect, bool> CommitPlacement;
        internal Func<bool> CanEdit;

        internal FreeCanvasBoard() : this(Step) { }

        internal FreeCanvasBoard(int step)
        {
            CellStep = FreeCanvasLayout.NormalizeStep(step);
            Background = Brushes.Transparent;
            AllowDrop = true;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            ClipToBounds = true;
            preview = new Border { Visibility = Visibility.Collapsed, IsHitTestVisible = false,
                BorderBrush = new SolidColorBrush(Color.FromRgb(106, 176, 255)),
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(45, 106, 176, 255)) };
            SetZIndex(preview, 20);
            Children.Add(preview);
        }

        internal void SetViewport(double width, string mode)
        {
            if (Double.IsNaN(width) || width <= 0) return;
            mode = FreeCanvasLayout.NormalizeViewportMode(mode);
            if (viewportMode == mode && (viewportWidth == width || Math.Abs(viewportWidth - width) < 0.5)) return;
            viewportWidth = width; viewportMode = mode;
            InvalidateMeasure();
        }

        internal void AddCard(string key, Border card, int columns, FreeCanvasRect saved)
        {
            card.Width = Double.NaN;
            card.Height = Double.NaN;
            card.Margin = new Thickness(0);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.VerticalAlignment = VerticalAlignment.Stretch;
            Entry entry = new Entry { Key = key, Card = card,
                Columns = Math.Max(MinimumColumns, Math.Min(InitialColumns, columns)),
                Preferred = FreeCanvasLayout.IsValid(saved) ? saved.Copy() : null };
            entries.Add(entry);
            Children.Add(card);
            // Resize handles are siblings, not wrappers: existing tile/widget
            // ownership, context menus and drop targets stay intact.
            System.Windows.Controls.Primitives.Thumb grip = new System.Windows.Controls.Primitives.Thumb
            {
                Cursor = Cursors.SizeNWSE, ToolTip = "Изменить размер группы",
                Background = Brushes.Transparent, Focusable = false
            };
            FrameworkElementFactory hit = new FrameworkElementFactory(typeof(Border));
            hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            FrameworkElementFactory mark = new FrameworkElementFactory(typeof(TextBlock));
            mark.SetValue(TextBlock.TextProperty, "◢");
            mark.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 168, 193)));
            mark.SetValue(TextBlock.FontSizeProperty, 12.0);
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
            ControlTemplate template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
            hit.AppendChild(mark);
            template.VisualTree = hit;
            grip.Template = template;
            entry.Grip = grip;
            SetZIndex(grip, 10);
            Children.Add(grip);
            string title = System.Windows.Automation.AutomationProperties.GetName(card);
            if (String.IsNullOrWhiteSpace(title)) title = key;
            Button compact = new Button { Content = title + "  ▸", Tag = title,
                MinHeight = 34, Padding = new Thickness(8, 4, 8, 4),
                Background = FluentUi.Surface, Foreground = FluentUi.Foreground,
                BorderBrush = FluentUi.BorderColor, HorizontalContentAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Hidden, ToolTip = "Временно раскрыть группу" };
            entry.Compact = compact;
            compact.Click += delegate { ToggleTemporaryGroup(key); };
            Children.Add(compact);
            FreeCanvasRect initial = null, candidate = null;
            Point start = new Point();
            grip.DragStarted += delegate
            {
                if (entry.Effective == null || IsTemporaryLayout || (CanEdit != null && !CanEdit())) return;
                initial = entry.Effective.Copy(); candidate = initial.Copy();
                start = Mouse.GetPosition(this);
                ShowGrid(true);
            };
            grip.DragDelta += delegate
            {
                if (initial == null) return;
                // Thumb can report a displacement from its unchanged starting
                // geometry on every event. Never accumulate those values.
                candidate = ResizeFromPointer(key, initial, start, Mouse.GetPosition(this));
                ShowPreview(candidate);
            };
            grip.DragCompleted += delegate(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
            {
                FreeCanvasRect result = candidate;
                bool changed = initial != null && result != null &&
                    (result.Width != initial.Width || result.Height != initial.Height);
                initial = candidate = null; ShowGrid(false);
                if (!e.Canceled && changed && CommitPlacement != null &&
                    (CanEdit == null || CanEdit())) CommitPlacement(key, result);
            };
        }

        private int WidthFor(Entry entry)
        {
            return Math.Min(FreeCanvasLayout.MaxCells, entry.Preferred == null ? entry.Columns :
                Math.Max(MinimumColumns, entry.Preferred.Width));
        }

        private int HeightFor(Entry entry, int width, int manual)
        {
            entry.Card.Measure(new Size(width * CellStep - Gap, Double.PositiveInfinity));
            return (int)Math.Min(FreeCanvasLayout.MaxCells, Math.Max(Math.Max(2, manual),
                Math.Ceiling((entry.Card.DesiredSize.Height + Gap) / CellStep)));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            List<FreeCanvasRect> occupied = new List<FreeCanvasRect>();
            int columns = 0, rows = 0;
            foreach (Entry entry in entries)
            {
                int width = WidthFor(entry), manual = entry.Preferred == null ? 0 : entry.Preferred.ManualHeight;
                int height = HeightFor(entry, width, manual);
                entry.Grip.Measure(new Size(18, 18));
                entry.Effective = null;
                if (entry.Preferred == null) continue;
                FreeCanvasRect candidate = new FreeCanvasRect(entry.Preferred.X, entry.Preferred.Y, width, height) { ManualHeight = manual };
                if (FreeCanvasLayout.CanPlace(candidate, occupied))
                {
                    entry.Effective = candidate;
                    occupied.Add(candidate);
                }
            }
            foreach (Entry entry in entries)
            {
                if (entry.Effective != null) continue;
                int width = WidthFor(entry), manual = entry.Preferred == null ? 0 : entry.Preferred.ManualHeight;
                int height = HeightFor(entry, width, manual);
                entry.Effective = FreeCanvasLayout.FindFirstFree(width, height, Math.Max(InitialColumns, width), occupied);
                if (entry.Effective != null)
                {
                    entry.Effective.ManualHeight = manual;
                    occupied.Add(entry.Effective);
                }
            }
            foreach (FreeCanvasRect rect in occupied)
            {
                columns = Math.Max(columns, rect.X + rect.Width);
                rows = Math.Max(rows, rect.Y + rect.Height);
            }
            double contentWidth = columns * CellStep;
            double naturalWidth = Math.Min(FreeCanvasLayout.MaxCells, Math.Max(InitialColumns, columns) + 4) * CellStep;
            double naturalHeight = Math.Min(FreeCanvasLayout.MaxCells, Math.Max((int)Math.Ceiling(400.0 / CellStep), rows) + 4) * CellStep;
            bool finite = !Double.IsInfinity(viewportWidth);
            bool overflow = finite && contentWidth > viewportWidth + 0.5;
            string next = overflow ? viewportMode : "scroll";
            if (activeMode == "collapse" && next != "collapse")
                foreach (Entry entry in entries) entry.TemporarilyExpanded = false;
            activeMode = next;
            displayScale = next == "scale" ? Math.Max(0.75, Math.Min(1, viewportWidth / Math.Max(1, contentWidth))) : 1;
            if (finite && viewportMode != "scroll")
                naturalWidth = Math.Max(viewportWidth, contentWidth);
            if (activeMode == "adaptive" || activeMode == "collapse")
                return MeasureTemporary(Math.Max(1, viewportWidth));
            foreach (Entry entry in entries)
            {
                entry.Card.Visibility = Visibility.Visible;
                entry.Compact.Visibility = Visibility.Hidden;
                entry.Grip.Visibility = IsTemporaryLayout ? Visibility.Hidden : Visibility.Visible;
            }
            return new Size(naturalWidth * displayScale, naturalHeight * displayScale);
        }

        private Size MeasureTemporary(double width)
        {
            // Sort by the original visual order, not by creation order.
            List<Entry> ordered = new List<Entry>(entries);
            ordered.Sort(delegate(Entry a, Entry b) {
                if (a.Effective == null) return b.Effective == null ? 0 : 1;
                if (b.Effective == null) return -1;
                int y = a.Effective.Y.CompareTo(b.Effective.Y);
                return y != 0 ? y : a.Effective.X.CompareTo(b.Effective.X);
            });
            double top = 0, cardWidth = Math.Max(1, width - Gap);
            foreach (Entry entry in ordered)
            {
                bool collapse = activeMode == "collapse";
                entry.Grip.Visibility = Visibility.Hidden;
                entry.Compact.Visibility = collapse ? Visibility.Visible : Visibility.Hidden;
                entry.Card.Visibility = !collapse || entry.TemporarilyExpanded ? Visibility.Visible : Visibility.Hidden;
                entry.CompactRect = new Rect(0, 0, 0, 0);
                entry.DisplayRect = new Rect(0, 0, 0, 0);
                if (collapse)
                {
                    entry.Compact.Content = (string)entry.Compact.Tag + (entry.TemporarilyExpanded ? "  ▾" : "  ▸");
                    entry.Compact.Measure(new Size(cardWidth, Double.PositiveInfinity));
                    double height = Math.Max(34, entry.Compact.DesiredSize.Height);
                    entry.CompactRect = new Rect(0, top, cardWidth, height);
                    top += height + Gap;
                }
                if (entry.Card.Visibility == Visibility.Visible)
                {
                    entry.Card.Measure(new Size(cardWidth, Double.PositiveInfinity));
                    double height = Math.Max(1, entry.Card.DesiredSize.Height);
                    entry.DisplayRect = new Rect(0, top, cardWidth, height);
                    top += height + Gap;
                }
            }
            return new Size(width, Math.Max(1, top));
        }

        internal bool ToggleTemporaryGroup(string key)
        {
            Entry entry = Find(key);
            if (activeMode != "collapse" || entry == null) return false;
            entry.TemporarilyExpanded = !entry.TemporarilyExpanded;
            InvalidateMeasure();
            return true;
        }

        private Rect PixelRect(FreeCanvasRect rect)
        {
            return rect == null ? new Rect(0, 0, 0, 0) :
                new Rect(rect.X * CellStep, rect.Y * CellStep, rect.Width * CellStep - Gap, rect.Height * CellStep - Gap);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            bool stacked = activeMode == "adaptive" || activeMode == "collapse";
            foreach (Entry entry in entries)
            {
                Rect rect = PixelRect(entry.Effective);
                // Scale only the visual tree; the saved geometry stays in unscaled cells.
                entry.Card.RenderTransform = displayScale < 0.999
                    ? (Transform)new ScaleTransform(displayScale, displayScale) : Transform.Identity;
                entry.Card.Arrange(stacked ? entry.DisplayRect : new Rect(rect.X * displayScale,
                    rect.Y * displayScale, rect.Width, rect.Height));
                entry.Compact.Arrange(stacked ? entry.CompactRect : new Rect(0, 0, 0, 0));
                entry.Grip.Arrange(IsTemporaryLayout || entry.Effective == null ? new Rect(0, 0, 0, 0) :
                    new Rect(rect.Right - 18, rect.Bottom - 18, 18, 18));
            }
            Rect target = IsTemporaryLayout ? new Rect(0, 0, 0, 0) : PixelRect(previewRect);
            preview.Measure(target.Size);
            preview.Arrange(target);
            return finalSize;
        }

        private Entry Find(string key)
        { return entries.Find(delegate(Entry item) { return String.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase); }); }

        private bool CanPlace(Entry entry, FreeCanvasRect rect)
        {
            List<FreeCanvasRect> occupied = new List<FreeCanvasRect>();
            foreach (Entry other in entries)
                if (other != entry && other.Effective != null) occupied.Add(other.Effective);
            return FreeCanvasLayout.CanPlace(rect, occupied);
        }

        internal void BeginGroupDrag(string key, Point point)
        {
            Entry entry = Find(key);
            if (IsTemporaryLayout || entry == null || entry.Effective == null) return;
            dragKey = key;
            dragOffset = point - new Point(entry.Effective.X * CellStep, entry.Effective.Y * CellStep);
            ShowGrid(true);
        }

        internal void EndGroupDrag()
        { dragKey = null; dragOffset = new Vector(); ShowGrid(false); }

        internal FreeCanvasRect MoveCandidate(string key, Point point)
        {
            Entry entry = Find(key);
            if (IsTemporaryLayout || entry == null || entry.Effective == null || point.X < 0 || point.Y < 0 ||
                point.X > ActualWidth || point.Y > ActualHeight) return null;
            if (String.Equals(key, dragKey, StringComparison.OrdinalIgnoreCase)) point -= dragOffset;
            FreeCanvasRect rect = new FreeCanvasRect(FreeCanvasLayout.Snap(point.X, CellStep),
                FreeCanvasLayout.Snap(point.Y, CellStep), entry.Effective.Width, entry.Effective.Height)
            { ManualHeight = entry.Effective.ManualHeight };
            return CanPlace(entry, rect) ? rect : null;
        }

        internal FreeCanvasRect ResizeFromPointer(string key, FreeCanvasRect initial, Point start, Point current)
        {
            if (!FreeCanvasLayout.IsValid(initial)) return null;
            Vector delta = current - start;
            return ResizeCandidate(key, initial.Width * CellStep + delta.X, initial.Height * CellStep + delta.Y);
        }

        internal FreeCanvasRect ResizeCandidate(string key, double widthDip, double heightDip)
        {
            Entry entry = Find(key);
            if (IsTemporaryLayout || entry == null || entry.Effective == null) return null;
            int width = Math.Max(MinimumColumns, FreeCanvasLayout.Snap(widthDip, CellStep));
            int manual = Math.Max(2, FreeCanvasLayout.Snap(heightDip, CellStep));
            // Measure the content at the proposed width, then restore the
            // current measure immediately; rejected previews cannot reflow cards.
            int height = HeightFor(entry, width, manual);
            HeightFor(entry, entry.Effective.Width, entry.Effective.ManualHeight);
            FreeCanvasRect rect = new FreeCanvasRect(entry.Effective.X, entry.Effective.Y, width, height)
            { ManualHeight = manual };
            return CanPlace(entry, rect) ? rect : null;
        }

        internal Dictionary<string, FreeCanvasRect> Capture()
        {
            Dictionary<string, FreeCanvasRect> result = new Dictionary<string, FreeCanvasRect>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry entry in entries)
                if (entry.Effective != null) result[entry.Key] = entry.Effective.Copy();
            return result;
        }

        internal void SetPreferred(string key, FreeCanvasRect rect)
        {
            Entry entry = Find(key);
            if (entry != null) entry.Preferred = FreeCanvasLayout.IsValid(rect) ? rect.Copy() : null;
            InvalidateMeasure();
        }

        internal void ShowPreview(FreeCanvasRect rect)
        {
            previewRect = rect;
            preview.Visibility = rect == null ? Visibility.Collapsed : Visibility.Visible;
            InvalidateArrange();
        }

        internal void ShowGrid(bool visible)
        {
            if (!visible) ShowPreview(null);
            if (showGrid == visible) return;
            showGrid = visible;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (!showGrid) return;
            Pen pen = new Pen(new SolidColorBrush(Color.FromArgb(45, 160, 180, 210)), 1);
            for (int x = CellStep; x < ActualWidth; x += CellStep)
                dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
            for (int y = CellStep; y < ActualHeight; y += CellStep)
                dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
        }
    }

    [DataContract]
    public sealed class AppConfig
    {
        internal AppConfig Copy()
        {
            AppConfig copy = (AppConfig)MemberwiseClone();
            copy.Groups = new List<string>(Groups);
            copy.GroupAlignmentOverrides = GroupAlignmentOverrides == null ? null :
                new Dictionary<string, string>(GroupAlignmentOverrides, StringComparer.OrdinalIgnoreCase);
            copy.GroupZoneOverrides = GroupZoneOverrides == null ? null :
                new Dictionary<string, string>(GroupZoneOverrides, StringComparer.OrdinalIgnoreCase);
            if (CanvasPositions != null)
            {
                copy.CanvasPositions = new Dictionary<string, FreeCanvasRect>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, FreeCanvasRect> item in CanvasPositions)
                    if (FreeCanvasLayout.IsValid(item.Value)) copy.CanvasPositions[item.Key] = item.Value.Copy();
            }
            copy.WidgetGroups = WidgetGroups == null ? null : WidgetGroups.ConvertAll(delegate(PluginWidgetGroupConfig g) { return g == null ? null : g.Copy(); });
            copy.WidgetIndicators = WidgetIndicators == null ? null : WidgetIndicators.ConvertAll(delegate(PluginWidgetPlacementConfig p) { return p == null ? null : p.Copy(); });
            copy.SnoozeQuickMinutes = SnoozeQuickMinutes == null ? null : (int[])SnoozeQuickMinutes.Clone();
            copy.BottomPanelFavorites = BottomPanelFavorites == null ? null : new List<string>(BottomPanelFavorites);
            copy.BottomPanelCategories = BottomPanelCategories == null ? null : new List<string>(BottomPanelCategories);
            copy.Items = Items.ConvertAll(delegate(AppItem item) { return item == null ? null : item.Copy(); });
            return copy;
        }

        internal void ApplySavedItemCopies()
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i] != null) Items[i] = Items[i].ApplySavedCopy();
        }

        [DataMember(Name = "version")]
        public int Version = 5;

        [DataMember(Name = "alwaysOnTop")]
        public bool AlwaysOnTop;

        [DataMember(Name = "compact", EmitDefaultValue = false)]
        public bool Compact;

        // Optional layout choice; legacy compact Boolean remains readable.
        [DataMember(Name = "tileDisplayMode", EmitDefaultValue = false)]
        public string TileDisplayMode;

        // Optional 75–125% tile scale; 100% preserves existing layouts.
        [DataMember(Name = "tileScalePercent", EmitDefaultValue = false)]
        public int? TileScalePercent = FluentUi.DefaultTileScalePercent;

        [DataMember(Name = "groupingEnabled", EmitDefaultValue = false)]
        public bool GroupingEnabled;

        // Optional group layout; missing fields retain the original full-width,
        // vertically stacked appearance of existing apps.json profiles.
        [DataMember(Name = "groupWidthMode", EmitDefaultValue = false)]
        public string GroupWidthMode;

        [DataMember(Name = "groupLayoutMode", EmitDefaultValue = false)]
        public string GroupLayoutMode;

        [DataMember(Name = "canvasPositions", EmitDefaultValue = false)]
        public Dictionary<string, FreeCanvasRect> CanvasPositions;

        [DataMember(Name = "canvasGridStep", EmitDefaultValue = false)]
        public int? CanvasGridStep;

        [DataMember(Name = "canvasViewportMode", EmitDefaultValue = false)]
        public string CanvasViewportMode;

        [DataMember(Name = "canvasInitialized", EmitDefaultValue = false)]
        public bool CanvasInitialized;

        [DataMember(Name = "groupAlignment", EmitDefaultValue = false)]
        public string GroupAlignment;

        // Only non-default overrides are stored, keyed by the group's name.
        [DataMember(Name = "groupAlignmentOverrides", EmitDefaultValue = false)]
        public Dictionary<string, string> GroupAlignmentOverrides;

        // Free layout uses a 3x3 zone board; missing fields mean top-left.
        [DataMember(Name = "groupDefaultZone", EmitDefaultValue = false)]
        public string GroupDefaultZone;

        [DataMember(Name = "groupZoneOverrides", EmitDefaultValue = false)]
        public Dictionary<string, string> GroupZoneOverrides;

        [DataMember(Name = "groups")]
        public List<string> Groups = new List<string>();

        [DataMember(Name = "widgetGroups", EmitDefaultValue = false)]
        public List<PluginWidgetGroupConfig> WidgetGroups;

        [DataMember(Name = "widgetIndicators", EmitDefaultValue = false)]
        public List<PluginWidgetPlacementConfig> WidgetIndicators;

        [DataMember(Name = "showNetwork")]
        public bool ShowNetwork = true;

        // Retain the legacy targets for rollback; migrated profiles no longer run built-in probes.
        [DataMember(Name = "networkPluginMigrated", EmitDefaultValue = false)]
        public bool NetworkPluginMigrated;

        // Two fixed slots, never extra background requests. Null means original
        // ICMP+HTTPS targets 1.1.1.1 and ya.ru, compatible with older apps.json.
        [DataMember(Name = "networkCheck1Mode", EmitDefaultValue = false)]
        public string NetworkCheck1Mode;
        [DataMember(Name = "networkCheck1Target", EmitDefaultValue = false)]
        public string NetworkCheck1Target;
        [DataMember(Name = "networkCheck2Mode", EmitDefaultValue = false)]
        public string NetworkCheck2Mode;
        [DataMember(Name = "networkCheck2Target", EmitDefaultValue = false)]
        public string NetworkCheck2Target;

        // Optional in legacy apps.json. Null maps to the original detailed panel.
        [DataMember(Name = "bottomPanelMode", EmitDefaultValue = false)]
        public string BottomPanelMode;

        // IDs in display order; disabled modules are not rendered.
        [DataMember(Name = "bottomPanelFavorites", EmitDefaultValue = false)]
        public List<string> BottomPanelFavorites;

        // Only used by categories mode. Null preserves all three in old configs.
        [DataMember(Name = "bottomPanelCategories", EmitDefaultValue = false)]
        public List<string> BottomPanelCategories;

        // Opacity of the footer backdrop only; missing legacy values mean fully opaque.
        // Child buttons, icons and text are never faded.
        [DataMember(Name = "bottomPanelOpacity", EmitDefaultValue = false)]
        public int? BottomPanelOpacity = 100;

        // Optional UI preference; older apps.json files load without conversion.
        [DataMember(Name = "settingsLastSection", EmitDefaultValue = false)]
        public string SettingsLastSection;

        // Main window content background only; independent of the dark control theme.
        [DataMember(Name = "mainBackgroundPreset", EmitDefaultValue = false)]
        public string MainBackgroundPreset = "blue-arcs";

        // Nullable to keep 2.12.0/earlier JSON compatible; default matches existing cards.
        [DataMember(Name = "groupOpacity", EmitDefaultValue = false)]
        public int? GroupOpacity = FluentUi.DefaultGroupOpacity;

        [DataMember(Name = "showWeather")]
        public bool ShowWeather = true;

        [DataMember(Name = "weatherCity", EmitDefaultValue = false)]
        public string WeatherCity = "";

        // Free, keyless providers: metno and openmeteo.
        [DataMember(Name = "weatherProvider", EmitDefaultValue = false)]
        public string WeatherProvider = "metno";

        // Legacy preference: old releases used this flag for minimizing.
        // When closeToTray is absent, keep that user's previous preference.
        [DataMember(Name = "minimizeToTray")]
        public bool MinimizeToTray = true;

        [DataMember(Name = "closeToTray", EmitDefaultValue = false)]
        public bool? CloseToTray;

        public bool CloseToTrayEnabled { get { return CloseToTray ?? MinimizeToTray; } }

        // Missing in older apps.json: stable releases only.
        [DataMember(Name = "checkBetaUpdates", EmitDefaultValue = false)]
        public bool CheckBetaUpdates;

        // Auto-backup remains idle until the user chooses an external folder.
        [DataMember(Name = "backupDirectory", EmitDefaultValue = false)]
        public string BackupDirectory;

        [DataMember(Name = "automaticBackupsEnabled", EmitDefaultValue = false)]
        public bool? AutomaticBackupsEnabled = true;

        // Only 1 or 7 days, 3/7/14 archives and 512/1024/2048/4096 MiB are supported.
        [DataMember(Name = "automaticBackupDays", EmitDefaultValue = false)]
        public int AutomaticBackupDays = 1;

        [DataMember(Name = "automaticBackupKeep", EmitDefaultValue = false)]
        public int AutomaticBackupKeep = 7;

        [DataMember(Name = "automaticBackupQuotaMb", EmitDefaultValue = false)]
        public int AutomaticBackupQuotaMb = 1024;

        [DataMember(Name = "backupExecutable", EmitDefaultValue = false)]
        public bool? BackupExecutable = true;

        [DataMember(Name = "backupPluginPackages", EmitDefaultValue = false)]
        public bool? BackupPluginPackages = true;

        [DataMember(Name = "backupReminders", EmitDefaultValue = false)]
        public bool? BackupReminders;
        [DataMember(Name = "backupCollections", EmitDefaultValue = false)]
        public bool? BackupCollections;
        [DataMember(Name = "backupCovers", EmitDefaultValue = false)]
        public bool? BackupCovers;
        [DataMember(Name = "backupBackgrounds", EmitDefaultValue = false)]
        public bool? BackupBackgrounds;
        [DataMember(Name = "backupWidgetLayout", EmitDefaultValue = false)]
        public bool? BackupWidgetLayout;
        [DataMember(Name = "backupPluginData", EmitDefaultValue = false)]
        public bool? BackupPluginData;

        // Portable archives use an explicit marker so empty widget layouts can
        // be distinguished from backups made by releases without plugins.
        [DataMember(Name = "pluginSettingsIncluded", EmitDefaultValue = false)]
        public bool PluginSettingsIncluded;
        [DataMember(Name = "archiveComponents", EmitDefaultValue = false)]
        public int? ArchiveComponents;

        // Nullable fields retain legacy defaults when absent from older apps.json.
        [DataMember(Name = "soundsEnabled", EmitDefaultValue = false)]
        public bool? SoundsEnabled = true;

        [DataMember(Name = "pomodoroSoundEnabled", EmitDefaultValue = false)]
        public bool? PomodoroSoundEnabled = true;

        [DataMember(Name = "remindersSoundEnabled", EmitDefaultValue = false)]
        public bool? RemindersSoundEnabled = false;

        [DataMember(Name = "snoozeQuickMinutes", EmitDefaultValue = false)]
        public int[] SnoozeQuickMinutes = new int[] { 10, 30, 60 };

        [DataMember(Name = "snoozeLastMinutes", EmitDefaultValue = false)]
        public int SnoozeLastMinutes = 10;

        [DataMember(Name = "showPomodoro", EmitDefaultValue = false)]
        public bool ShowPomodoro = true;

        // Nullable flags retain enabled defaults when loading apps.json from 2.8.0.
        [DataMember(Name = "showCountdownTimer", EmitDefaultValue = false)]
        public bool? ShowCountdownTimer = true;

        // Optional module; null maps to enabled when upgrading older apps.json.
        [DataMember(Name = "showCollections", EmitDefaultValue = false)]
        public bool? ShowCollections = true;

        [DataMember(Name = "countdownSoundEnabled", EmitDefaultValue = false)]
        public bool? CountdownSoundEnabled = true;

        [DataMember(Name = "countdownDurationSeconds", EmitDefaultValue = false)]
        public int CountdownDurationSeconds = 300;

        [DataMember(Name = "pomodoroWorkMinutes", EmitDefaultValue = false)]
        public int PomodoroWorkMinutes = 25;

        [DataMember(Name = "pomodoroShortMinutes", EmitDefaultValue = false)]
        public int PomodoroShortMinutes = 5;

        [DataMember(Name = "pomodoroLongMinutes", EmitDefaultValue = false)]
        public int PomodoroLongMinutes = 15;

        [DataMember(Name = "rdpKeepAwake", EmitDefaultValue = false)]
        public bool RdpKeepAwake;

        // Logical WPF window coordinates in DIPs, not physical pixels.
        // Separately tracked from tile positions; omitted in older configs.
        [DataMember(Name = "windowPlacementSaved", EmitDefaultValue = false)]
        public bool WindowPlacementSaved;

        [DataMember(Name = "windowLeft", EmitDefaultValue = false)]
        public double WindowLeft;

        [DataMember(Name = "windowTop", EmitDefaultValue = false)]
        public double WindowTop;

        [DataMember(Name = "windowWidth", EmitDefaultValue = false)]
        public double WindowWidth;

        [DataMember(Name = "windowHeight", EmitDefaultValue = false)]
        public double WindowHeight;

        [DataMember(Name = "windowMaximized", EmitDefaultValue = false)]
        public bool WindowMaximized;

        [DataMember(Name = "items")]
        public List<AppItem> Items = new List<AppItem>();
    }

    [DataContract]
    internal sealed class PluginSettingsDocument
    {
        [DataMember(Name = "version", IsRequired = true)]
        public int Version = 1;
        [DataMember(Name = "canvasPositions", EmitDefaultValue = false)] public Dictionary<string, FreeCanvasRect> CanvasPositions;
        [DataMember(Name = "canvasGridStep", EmitDefaultValue = false)] public int? CanvasGridStep;
        [DataMember(Name = "canvasViewportMode", EmitDefaultValue = false)] public string CanvasViewportMode;
        [DataMember(Name = "canvasInitialized", EmitDefaultValue = false)] public bool CanvasInitialized;
        [DataMember(Name = "widgetGroups", EmitDefaultValue = false)]
        public List<PluginWidgetGroupConfig> WidgetGroups;
        [DataMember(Name = "widgetIndicators", EmitDefaultValue = false)]
        public List<PluginWidgetPlacementConfig> WidgetIndicators;
        [DataMember(Name = "backupExecutable", EmitDefaultValue = false)]
        public bool? BackupExecutable;
        [DataMember(Name = "backupPluginPackages", EmitDefaultValue = false)]
        public bool? BackupPluginPackages;
        [DataMember(Name = "backupReminders", EmitDefaultValue = false)]
        public bool? BackupReminders;
        [DataMember(Name = "backupCollections", EmitDefaultValue = false)]
        public bool? BackupCollections;
        [DataMember(Name = "backupCovers", EmitDefaultValue = false)]
        public bool? BackupCovers;
        [DataMember(Name = "backupBackgrounds", EmitDefaultValue = false)]
        public bool? BackupBackgrounds;
        [DataMember(Name = "backupWidgetLayout", EmitDefaultValue = false)]
        public bool? BackupWidgetLayout;
        [DataMember(Name = "backupPluginData", EmitDefaultValue = false)]
        public bool? BackupPluginData;

        internal static PluginSettingsDocument Capture(AppConfig c)
        {
            return new PluginSettingsDocument {
                CanvasPositions = c.CanvasPositions, CanvasGridStep = c.CanvasGridStep, CanvasViewportMode = c.CanvasViewportMode,
                CanvasInitialized = c.CanvasInitialized,
                WidgetGroups = c.WidgetGroups, WidgetIndicators = c.WidgetIndicators,
                BackupExecutable = c.BackupExecutable, BackupPluginPackages = c.BackupPluginPackages,
                BackupReminders = c.BackupReminders, BackupCollections = c.BackupCollections,
                BackupCovers = c.BackupCovers, BackupBackgrounds = c.BackupBackgrounds,
                BackupWidgetLayout = c.BackupWidgetLayout, BackupPluginData = c.BackupPluginData
            };
        }

        internal void Apply(AppConfig c)
        {
            if (Version != 1) throw new InvalidDataException("Неподдерживаемая версия настроек плагинов.");
            PluginWidgetGroupConfig.Validate(WidgetGroups, WidgetIndicators);
            c.CanvasPositions = FreeCanvasLayout.MergePositions(CanvasPositions, FreeCanvasLayout.NormalizeStep(CanvasGridStep),
                CanvasPositions, FreeCanvasLayout.NormalizeStep(CanvasGridStep), FreeCanvasLayout.NormalizeStep(CanvasGridStep));
            c.CanvasGridStep = CanvasGridStep; c.CanvasViewportMode = CanvasViewportMode;
            c.CanvasInitialized = CanvasInitialized || c.CanvasPositions != null;
            c.WidgetGroups = WidgetGroups; c.WidgetIndicators = WidgetIndicators;
            c.BackupExecutable = BackupExecutable; c.BackupPluginPackages = BackupPluginPackages;
            c.BackupReminders = BackupReminders; c.BackupCollections = BackupCollections;
            c.BackupCovers = BackupCovers; c.BackupBackgrounds = BackupBackgrounds;
            c.BackupWidgetLayout = BackupWidgetLayout; c.BackupPluginData = BackupPluginData;
        }

        internal static void Clear(AppConfig c)
        {
            new PluginSettingsDocument().Apply(c);
            c.PluginSettingsIncluded = false;
            c.ArchiveComponents = null;
        }
    }

    public static class ConfigStore
    {
        public static readonly string DirectoryPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf");
        public static readonly string FilePath = System.IO.Path.Combine(DirectoryPath, "apps.json");
        internal const string PluginSettingsName = "plugins-settings.json";
        internal static readonly string PluginSettingsPath = System.IO.Path.Combine(DirectoryPath, PluginSettingsName);
        public static readonly string LogPath = System.IO.Path.Combine(DirectoryPath, "error.log");
        public static readonly string ShortcutsDirectory = System.IO.Path.Combine(DirectoryPath, "Shortcuts");

        public static bool IsShortcut(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".lnk" || ext == ".url";
        }

        public static bool IsOwnedShortcut(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string expected = System.IO.Path.GetFullPath(ShortcutsDirectory).TrimEnd('\\') + "\\";
                return System.IO.Path.GetFullPath(path).StartsWith(expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string CopyShortcut(string source)
        {
            Directory.CreateDirectory(ShortcutsDirectory);
            string name = Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(source).ToLowerInvariant();
            string target = System.IO.Path.Combine(ShortcutsDirectory, name);
            File.Copy(source, target, false);
            return target;
        }

        // Legacy versions stored references to .lnk/.url on the Desktop.
        // Make private copies once so users can safely delete those Desktop shortcuts.
        public static int MigrateShortcuts(AppConfig config, List<string> createdShortcuts)
        {
            int count = 0;
            foreach (AppItem item in config.Items)
            {
                if (item == null || !IsShortcut(item.Path) || IsOwnedShortcut(item.Path) || !File.Exists(item.Path))
                    continue;
                string original = item.Path;
                item.Path = CopyShortcut(original);
                createdShortcuts.Add(item.Path);
                item.SourcePath = original;
                count++;
            }
            return count;
        }

        private static DataContractJsonSerializer NewSerializer()
        {
            return new DataContractJsonSerializer(typeof(AppConfig));
        }

        private static AppConfig Read(string path)
        {
            using (FileStream input = File.OpenRead(path))
            {
                AppConfig data = (AppConfig)NewSerializer().ReadObject(input);
                if (data == null) throw new InvalidDataException("Пустой файл настроек.");
                if (data.Items == null) data.Items = new List<AppItem>();
                if (data.Groups == null) data.Groups = new List<string>();
                PluginWidgetGroupConfig.Validate(data.WidgetGroups, data.WidgetIndicators);
                if (data.Version < 3)
                {
                    // Features introduced in 1.2 should be enabled for existing users.
                    data.ShowNetwork = true;
                    data.ShowWeather = true;
                    data.MinimizeToTray = true;
                    data.Version = 3;
                }
                if (data.Version < 4)
                {
                    data.ShowPomodoro = true;
                    data.Version = 4;
                }
                if (data.Version < 5) data.Version = 5;
                data.MainBackgroundPreset = FluentUi.NormalizeMainBackground(data.MainBackgroundPreset);
                data.GroupOpacity = FluentUi.NormalizeGroupOpacity(data.GroupOpacity);
                data.BottomPanelOpacity = MainWindow.NormalizeBottomPanelOpacity(data.BottomPanelOpacity);
                data.TileScalePercent = FluentUi.NormalizeTileScale(data.TileScalePercent);
                // Ignore damaged, non-finite or unreasonable geometry values.
                if (data.WindowPlacementSaved &&
                    (!IsValidGeometry(data.WindowLeft, data.WindowTop,
                        data.WindowWidth, data.WindowHeight)))
                    data.WindowPlacementSaved = false;
                // Also migrates the removed "yandex" provider without changing the JSON schema.
                if (data.WeatherProvider != "openmeteo" && data.WeatherProvider != "metno")
                    data.WeatherProvider = "metno";
                // Preserve old settings on load without rewriting the source file.
                if (data.TileDisplayMode != "compact" && data.TileDisplayMode != "normal" &&
                    data.TileDisplayMode != "labeled")
                    data.TileDisplayMode = data.Compact ? "compact" : "labeled";
                if (!data.SoundsEnabled.HasValue) data.SoundsEnabled = true;
                if (!data.PomodoroSoundEnabled.HasValue) data.PomodoroSoundEnabled = true;
                if (!data.RemindersSoundEnabled.HasValue) data.RemindersSoundEnabled = false;
                if (!data.ShowCountdownTimer.HasValue) data.ShowCountdownTimer = true;
                if (!data.ShowCollections.HasValue) data.ShowCollections = true;
                if (!data.CountdownSoundEnabled.HasValue) data.CountdownSoundEnabled = true;
                if (data.CountdownDurationSeconds < 1 || data.CountdownDurationSeconds > 86399)
                    data.CountdownDurationSeconds = 300;
                if (data.PomodoroWorkMinutes < 1 || data.PomodoroWorkMinutes > 240)
                    data.PomodoroWorkMinutes = 25;
                if (data.PomodoroShortMinutes < 1 || data.PomodoroShortMinutes > 120)
                    data.PomodoroShortMinutes = 5;
                if (data.PomodoroLongMinutes < 1 || data.PomodoroLongMinutes > 120)
                    data.PomodoroLongMinutes = 15;
                return data;
            }
        }

        private static bool IsFinite(double number)
        {
            return !Double.IsNaN(number) && !Double.IsInfinity(number);
        }

        public static bool IsValidGeometry(double left, double top, double width, double height)
        {
            // Allow negative coordinates for monitors to the left/above the primary.
            return IsFinite(left) && IsFinite(top) && IsFinite(width) && IsFinite(height) &&
                Math.Abs(left) < 100000 && Math.Abs(top) < 100000 &&
                width >= 300 && width <= 20000 && height >= 250 && height <= 20000;
        }

        private static byte[] EncodePluginSettings(AppConfig config)
        {
            PluginWidgetGroupConfig.Validate(config.WidgetGroups, config.WidgetIndicators);
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(PluginSettingsDocument)).WriteObject(stream, PluginSettingsDocument.Capture(config));
                return stream.ToArray();
            }
        }

        private static void WritePluginSettings(byte[] bytes)
        {
            Directory.CreateDirectory(DirectoryPath);
            string temp = PluginSettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                if (File.Exists(PluginSettingsPath)) File.Replace(temp, PluginSettingsPath, PluginSettingsPath + ".bak", true);
                else File.Move(temp, PluginSettingsPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static AppConfig Load(out string warning, out bool safeToSave)
        {
            AppConfig config = LoadShared(out warning, out safeToSave);
            if (!safeToSave) return config;
            try
            {
                if (File.Exists(PluginSettingsPath))
                {
                    using (FileStream input = File.OpenRead(PluginSettingsPath))
                    {
                        if (input.Length > 16L * 1024 * 1024) throw new InvalidDataException("Слишком большой файл настроек плагинов.");
                        PluginSettingsDocument extension = (PluginSettingsDocument)new DataContractJsonSerializer(typeof(PluginSettingsDocument)).ReadObject(input);
                        if (extension == null) throw new InvalidDataException("Пустой файл настроек плагинов.");
                        extension.Apply(config);
                    }
                }
                else if (File.Exists(PluginSettingsPath + ".bak"))
                    throw new InvalidDataException("Основной файл настроек плагинов отсутствует. Сохраните резервную копию и восстановите файл.");
                else if (File.Exists(FilePath) && (config.WidgetGroups != null || config.WidgetIndicators != null ||
                    config.BackupExecutable.HasValue || config.BackupPluginPackages.HasValue))
                {
                    // Migrate early development builds without rewriting their original apps.json.
                    WritePluginSettings(EncodePluginSettings(config));
                }
            }
            catch (Exception ex)
            {
                Log(ex); safeToSave = false;
                warning = "Не удалось прочитать или перенести настройки плагинов. Сохранение отключено.\n" +
                    PluginSettingsPath + "\n" + ex.Message;
            }
            return config;
        }

        private static AppConfig LoadShared(out string warning, out bool safeToSave)
        {
            warning = null;
            safeToSave = true;
            string backupPath = FilePath + ".bak";
            if (!File.Exists(FilePath))
            {
                if (!File.Exists(backupPath)) return new AppConfig();
                safeToSave = false;
                try
                {
                    AppConfig backup = Read(backupPath);
                    warning = "Основной apps.json отсутствует. Загружена резервная копия; сохранение отключено.\n\n" +
                        "Скопируй (не перемещай) файл " + backupPath + " в " + FilePath +
                        " и перезапусти AppShelf. Резервная копия не изменена.";
                    return backup;
                }
                catch (Exception backupEx)
                {
                    Log(backupEx);
                    warning = "Основной apps.json отсутствует, резервная копия не читается:\n" + backupPath +
                        "\n\n" + backupEx.Message + "\n\nСохранение отключено. Пустой список не является восстановленными данными.";
                    return new AppConfig();
                }
            }
            try
            {
                return Read(FilePath);
            }
            catch (Exception ex)
            {
                Log(ex);
                if (File.Exists(backupPath))
                {
                    try
                    {
                        AppConfig backup = Read(backupPath);
                        // Do not replace a damaged primary file automatically.
                        safeToSave = false;
                        warning = "Основной файл настроек повреждён. Загружена резервная копия. " +
                                  "Во избежание потери данных сохранение отключено.\n\n" +
                                  "Файлы: " + FilePath + " и " + backupPath +
                                  "\n\nДля восстановления закрой AppShelf, переименуй повреждённый apps.json, " +
                                  "затем скопируй (не перемещай) apps.json.bak в apps.json и перезапусти программу. " +
                                  "Сохрани резервную копию и повреждённый файл до проверки данных.";
                        return backup;
                    }
                    catch (Exception backupEx)
                    {
                        Log(backupEx);
                    }
                }
                safeToSave = false;
                warning = "Не удалось прочитать настройки. Файл не перезаписан:\n" +
                          FilePath + "\n\n" + ex.Message + "\n\nРезервная копия отсутствует или также не читается: " +
                          backupPath + "\nСохранение отключено. Пустой список не является восстановленными данными.";
                return new AppConfig();
            }
        }

        public static void Save(AppConfig config)
        {
            Save(config, true);
        }

        internal static List<string> RetiredShortcutCandidates()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { AddShortcutReferences(FilePath + ".bak", paths, false); }
            catch (Exception ex) { Log(ex); }
            return new List<string>(paths);
        }

        private static void AddShortcutReferences(string filename, HashSet<string> paths, bool includeSources)
        {
            if (!File.Exists(filename)) return;
            foreach (AppItem item in Read(filename).Items)
            {
                if (item == null) continue;
                if (IsOwnedShortcut(item.Path)) paths.Add(System.IO.Path.GetFullPath(item.Path));
                if (includeSources && IsOwnedShortcut(item.SourcePath)) paths.Add(System.IO.Path.GetFullPath(item.SourcePath));
            }
        }

        internal static void CleanupRetiredShortcuts(IEnumerable<string> candidates)
        {
            HashSet<string> retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Unknown/unreadable state must never authorize deletion.
                if (!File.Exists(FilePath)) return;
                AddShortcutReferences(FilePath, retained, true);
                AddShortcutReferences(FilePath + ".bak", retained, true);
            }
            catch (Exception ex) { Log(ex); return; }
            foreach (string path in candidates)
                if (IsOwnedShortcut(path) && IsShortcut(path) && !retained.Contains(System.IO.Path.GetFullPath(path)))
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { Log(ex); }
                }
        }

        internal static void Save(AppConfig config, bool cleanupShortcuts)
        {
            PluginWidgetGroupConfig.Validate(config.WidgetGroups, config.WidgetIndicators);
            List<string> retired = cleanupShortcuts ? RetiredShortcutCandidates() : null;
            System.IO.Directory.CreateDirectory(DirectoryPath);
            config.Version = 5;
            string tempPath = FilePath + ".tmp";
            byte[] extension = EncodePluginSettings(config);
            byte[] oldExtension = File.Exists(PluginSettingsPath) ? File.ReadAllBytes(PluginSettingsPath) : null;
            bool changedExtension = oldExtension == null || !System.Linq.Enumerable.SequenceEqual(oldExtension, extension);
            bool extensionWritten = false;
            AppConfig shared = config.Copy();
            PluginSettingsDocument.Clear(shared);
            try
            {
                using (FileStream output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    NewSerializer().WriteObject(output, shared);
                    output.Flush(true);
                }
                if (changedExtension)
                {
                    WritePluginSettings(extension);
                    extensionWritten = true;
                }
                if (File.Exists(FilePath))
                {
                    File.Replace(tempPath, FilePath, FilePath + ".bak", true);
                }
                else
                {
                    File.Move(tempPath, FilePath);
                }
            }
            catch
            {
                if (extensionWritten)
                {
                    try
                    {
                        if (oldExtension == null) File.Delete(PluginSettingsPath);
                        else WritePluginSettings(oldExtension);
                    }
                    catch (Exception error) { Log(error); }
                }
                throw;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
            if (cleanupShortcuts && retired.Count != 0) CleanupRetiredShortcuts(retired);
        }

        public static void Log(Exception error)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(LogPath, "\r\n[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                    error.ToString() + "\r\n");
            }
            catch { }
        }
    }

    public static class ShellIcon
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes,
            ref SHFILEINFO info, uint cbFileInfo, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
        private static extern uint ExtractIconEx(string file, int index,
            out IntPtr large, out IntPtr small, uint count);

        // SHGetFileInfo always overlays a shortcut arrow on .lnk files even
        // without SHGFI_LINKOVERLAY. Read the icon source of the link instead.
        public static ImageSource GetWithoutShortcutOverlay(string shortcutPath)
        {
            if (!String.Equals(System.IO.Path.GetExtension(shortcutPath),
                ".lnk", StringComparison.OrdinalIgnoreCase))
                return Get(shortcutPath);
            object shell = null, shortcut = null;
            try
            {
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return Get(shortcutPath);
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { shortcutPath });
                Type linkType = shortcut.GetType();
                string location = Convert.ToString(linkType.InvokeMember("IconLocation",
                    BindingFlags.GetProperty, null, shortcut, null)) ?? "";
                ImageSource extracted = FromIconLocation(location);
                if (extracted != null) return extracted;
                string target = Convert.ToString(linkType.InvokeMember("TargetPath",
                    BindingFlags.GetProperty, null, shortcut, null)) ?? "";
                target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
                if (File.Exists(target) || Directory.Exists(target))
                    return Get(target);
            }
            catch (Exception ex) { ConfigStore.Log(ex); }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
            // Keep a recognizable icon when a virtual/system link cannot be resolved.
            return Get(shortcutPath);
        }

        private static ImageSource FromIconLocation(string location)
        {
            if (String.IsNullOrWhiteSpace(location)) return null;
            string file = location.Trim();
            int index = 0, comma = file.LastIndexOf(',');
            if (comma >= 0)
            {
                int value;
                if (Int32.TryParse(file.Substring(comma + 1).Trim(), out value))
                {
                    index = value;
                    file = file.Substring(0, comma);
                }
            }
            file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
            if (!File.Exists(file)) return null;
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try
            {
                if (ExtractIconEx(file, index, out large, out small, 1) == 0)
                    return null;
                IntPtr chosen = large != IntPtr.Zero ? large : small;
                if (chosen == IntPtr.Zero) return null;
                BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(chosen,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                return icon;
            }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero && small != large) DestroyIcon(small);
            }
        }

        public static ImageSource Get(string path)
        {
            SHFILEINFO info = new SHFILEINFO();
            // SHGFI_ICON | SHGFI_LARGEICON. Shell resolves the icon stored in .lnk files.
            IntPtr result = SHGetFileInfo(path, 0, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 0x00000100);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
    }

    public sealed class ShortcutTarget
    {
        public string Path;
        public string Arguments;
        public string WorkingDirectory;
    }

    public static class Launcher
    {
        private static string Extension(string path)
        {
            return System.IO.Path.GetExtension(path).ToLowerInvariant();
        }

        private static string WorkingFolder(string file)
        {
            string dir = System.IO.Path.GetDirectoryName(file);
            return dir == null ? Environment.CurrentDirectory : dir;
        }

        private static ShortcutTarget ResolveShortcut(string file)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new NotSupportedException("Windows Script Host недоступен; невозможно определить цель ярлыка.");

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { file });
                Type shortcutType = shortcut.GetType();
                object path = shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty,
                    null, shortcut, null);
                object args = shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty,
                    null, shortcut, null);
                object folder = shortcutType.InvokeMember("WorkingDirectory", BindingFlags.GetProperty,
                    null, shortcut, null);
                return new ShortcutTarget
                {
                    Path = Environment.ExpandEnvironmentVariables(Convert.ToString(path) ?? ""),
                    Arguments = Convert.ToString(args) ?? "",
                    WorkingDirectory = Environment.ExpandEnvironmentVariables(Convert.ToString(folder) ?? "")
                };
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        public static void Start(AppItem item, bool elevated)
        {
            string path = item.Path;
            if (!File.Exists(path) && !System.IO.Directory.Exists(path))
                throw new FileNotFoundException("Ярлык или программа больше не существует: " + path);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.UseShellExecute = true;
            psi.WorkingDirectory = System.IO.Directory.Exists(path) ? path : WorkingFolder(path);

            if (!elevated)
            {
                psi.FileName = path;
            }
            else
            {
                if (System.IO.Directory.Exists(path) || Extension(path) == ".url")
                    throw new NotSupportedException("Для папок и интернет-ссылок запуск от администратора не поддерживается.");

                ShortcutTarget resolved = new ShortcutTarget { Path = path, Arguments = "", WorkingDirectory = "" };
                if (Extension(path) == ".lnk") resolved = ResolveShortcut(path);

                if (String.IsNullOrWhiteSpace(resolved.Path))
                    throw new NotSupportedException("Это системный или виртуальный ярлык. Windows не предоставляет путь к EXE для запуска с правами администратора.");

                string target = Environment.ExpandEnvironmentVariables(resolved.Path);
                string targetExt = Extension(target);
                string arguments = resolved.Arguments ?? "";
                string workDir = !String.IsNullOrWhiteSpace(resolved.WorkingDirectory)
                    ? resolved.WorkingDirectory : WorkingFolder(target);

                if (targetExt == ".bat" || targetExt == ".cmd")
                {
                    // cmd.exe runs elevated; scripts with paths containing double quotes are not supported.
                    if (target.IndexOf('"') >= 0) throw new NotSupportedException("Недопустимые кавычки в пути скрипта.");
                    string cmd = Environment.GetEnvironmentVariable("ComSpec");
                    if (String.IsNullOrEmpty(cmd)) cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
                    arguments = "/d /s /c \"\"" + target + "\" " + arguments + "\"";
                    target = cmd;
                }
                else if (targetExt == ".msc")
                {
                    arguments = "\"" + target + "\" " + arguments;
                    target = System.IO.Path.Combine(Environment.SystemDirectory, "mmc.exe");
                }
                else if (targetExt != ".exe" && targetExt != ".com")
                {
                    throw new NotSupportedException(
                        "Запуск от администратора поддерживается для EXE, COM, BAT, CMD, MSC " +
                        "и обычных ярлыков на эти файлы. Цель: " + target);
                }

                psi.FileName = target;
                psi.Arguments = arguments;
                psi.WorkingDirectory = System.IO.Directory.Exists(workDir) ? workDir : WorkingFolder(path);
                psi.Verb = "runas"; // Standard Windows UAC prompt. No elevation bypass.
            }

            Process process = Process.Start(psi);
            if (process != null) process.Dispose();
        }
    }

    [DataContract]
    public sealed class GeoSearchResponse
    {
        [DataMember(Name = "results")]
        public List<GeoPlace> Results;
    }

    [DataContract]
    public sealed class GeoPlace
    {
        [DataMember(Name = "name")]
        public string Name;
        [DataMember(Name = "country")]
        public string Country;
        [DataMember(Name = "latitude")]
        public double? Latitude;
        [DataMember(Name = "longitude")]
        public double? Longitude;
        [DataMember(Name = "timezone")]
        public string TimeZone;
    }

    [DataContract]
    public sealed class ForecastResponse
    {
        [DataMember(Name = "current")]
        public CurrentWeather Current;
        [DataMember(Name = "daily")]
        public DailyWeather Daily;
    }

    [DataContract]
    public sealed class CurrentWeather
    {
        [DataMember(Name = "temperature_2m")]
        public double? Temperature;
        [DataMember(Name = "apparent_temperature")]
        public double? ApparentTemperature;
        [DataMember(Name = "weather_code")]
        public int? Code;
    }

    [DataContract]
    public sealed class DailyWeather
    {
        [DataMember(Name = "time")]
        public List<string> Dates;
        [DataMember(Name = "weather_code")]
        public List<int?> Codes;
        [DataMember(Name = "temperature_2m_max")]
        public List<double?> Max;
        [DataMember(Name = "temperature_2m_min")]
        public List<double?> Min;
        [DataMember(Name = "apparent_temperature_max")]
        public List<double?> ApparentMax;
        [DataMember(Name = "apparent_temperature_min")]
        public List<double?> ApparentMin;
        [DataMember(Name = "precipitation_probability_max")]
        public List<int?> PrecipitationProbability;
    }

    [DataContract]
    public sealed class WeatherDay
    {
        [DataMember(Name = "date")] public DateTime Date;
        [DataMember(Name = "condition")] public string Condition;
        [DataMember(Name = "min")] public double? Min;
        [DataMember(Name = "max")] public double? Max;
        [DataMember(Name = "apparentMin")] public double? ApparentMin;
        [DataMember(Name = "apparentMax")] public double? ApparentMax;
        [DataMember(Name = "precipitationProbability")] public int? PrecipitationProbability;
    }

    [DataContract]
    public sealed class WeatherSnapshot
    {
        [DataMember(Name = "city")] public string City;
        [DataMember(Name = "source")] public string Source;
        [DataMember(Name = "temperature")] public double Temperature;
        [DataMember(Name = "apparentTemperature")] public double? ApparentTemperature;
        [DataMember(Name = "dailyRangeNote")] public string DailyRangeNote;
        [DataMember(Name = "condition")] public string Condition;
        [DataMember(Name = "updatedAt")] public DateTime UpdatedAt;
        [DataMember(Name = "days")] public List<WeatherDay> Days = new List<WeatherDay>();

        public static string TemperatureText(double? value)
        {
            return value.HasValue ? value.Value.ToString("0", CultureInfo.CurrentCulture) + " °C" : "н/д";
        }

        public string Summary()
        {
            return City + ": " + TemperatureText(Temperature) +
                " · ощущается как " + TemperatureText(ApparentTemperature) +
                " · " + Condition;
        }
    }

    // Compatibility layer for the old .NET Framework compiler used by Build-AppShelf.cmd.
    // Do not weaken certificate validation or enable obsolete TLS protocols.
    public static class NetworkTls
    {
        private static bool _fallbackUsed;
        private static readonly object Sync = new object();

        public static void Configure()
        {
            if (Environment.Version.Major != 4) return; // .NET 8 uses OS defaults itself.
            // Old-targeted Framework EXEs otherwise can inherit TLS 1.0 era defaults.
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
            AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)0; // SystemDefault
        }

        public static bool RetryWithTls12()
        {
            if (Environment.Version.Major != 4) return false;
            lock (Sync)
            {
                if (_fallbackUsed) return false;
                _fallbackUsed = true;
                // Fallback for Windows/.NET Framework that cannot negotiate with SystemDefault.
                // TLS 1.2 is supported by Open-Meteo; no SSL3/TLS 1.0 and no certificate bypass.
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                return true;
            }
        }
    }

    public static partial class WeatherClient
    {
        private const int WeatherTimeoutMilliseconds = 9000;
        private const int MaxWeatherJsonBytes = 2 * 1024 * 1024;

        private static async Task<T> ReadJson<T>(string url, string stage, bool allowTlsRetry = true)
        {
            using (CancellationTokenSource timeout = new CancellationTokenSource(WeatherTimeoutMilliseconds))
            {
                CancellationToken token = timeout.Token;
                Task<T> work = Task.Run(delegate { return ReadJsonWorker<T>(url, stage, allowTlsRetry, token); });
                try
                {
                    if (await Task.WhenAny(work, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false) != work)
                    {
                        // Observe a late failure after Abort, without holding up the Dispatcher.
                        GC.KeepAlive(work.ContinueWith(delegate(Task<T> failed) { GC.KeepAlive(failed.Exception); },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default));
                        throw new TimeoutException(stage + ": превышено время ожидания (9 с).");
                    }
                    try { return await work.ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested)
                            throw new TimeoutException(stage + ": превышено время ожидания (9 с).", ex);
                        throw;
                    }
                }
                finally { timeout.Cancel(); }
            }
        }

        private static T ParseMetCachedJson<T>(byte[] body, string stage)
        {
            if (body == null || body.Length == 0 || body.Length > MaxWeatherJsonBytes)
                throw new InvalidDataException(stage + ": повреждён кеш HTTP-ответа.");
            using (MemoryStream cached = new MemoryStream(body, false))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(cached);
        }

        private static T ReadJsonWorker<T>(string url, string stage, bool allowTlsRetry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            Version version = typeof(WeatherClient).Assembly.GetName().Version;
            string appVersion = version == null ? "unknown" :
                version.Major + "." + version.Minor + "." + version.Build;
            // Public Issues URL provides a project contact without exposing a personal email.
            request.UserAgent = "AppShelf/" + appVersion +
                " (+https://github.com/mishakov1c/app_shelf/issues)";
            request.Accept = "application/json";
            bool metNorway = String.Equals(request.RequestUri.Host, "api.met.no",
                StringComparison.OrdinalIgnoreCase);
            MetHttpCacheEntry cachedMet = metNorway ? MetHttpResponseCache.Get(url) : null;
            if (cachedMet != null)
            {
                token.ThrowIfCancellationRequested();
                // Never send a request while Expires (or the three-hour minimum)
                // prevents repeating the same locationforecast query.
                if (DateTime.UtcNow.Ticks < cachedMet.ExpiresUtcTicks)
                    return ParseMetCachedJson<T>(cachedMet.Body, stage);
                DateTimeOffset modified;
                if (!String.IsNullOrWhiteSpace(cachedMet.LastModified) &&
                    DateTimeOffset.TryParse(cachedMet.LastModified, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out modified) &&
                    modified.UtcDateTime <= DateTime.UtcNow)
                    request.IfModifiedSince = modified.UtcDateTime;
            }
            if (metNorway)
            {
                // MET Norway requires redirect and gzip/deflate support. The framework
                // advertises both encodings and decodes the response before JSON parsing.
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 5;
                request.AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            else
            {
                // Open-Meteo's deflate responses caused a .NET Framework decompression
                // error ("Длина блока не соответствует дополнению"). Preserve the
                // working uncompressed request for both its forecast and geocoding.
                request.AutomaticDecompression = DecompressionMethods.None;
                request.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
            }
            request.Timeout = WeatherTimeoutMilliseconds;
            request.ReadWriteTimeout = WeatherTimeoutMilliseconds;
            bool retryTls = false;
            using (token.Register(request.Abort))
            {
                try
                {
                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    {
                        // Do not trust a final HTTP response after redirection.
                        if (metNorway && (response.ResponseUri == null ||
                            response.ResponseUri.Scheme != Uri.UriSchemeHttps))
                            throw new InvalidDataException(stage + ": ответ получен не по HTTPS.");
                        HttpWebResponse metResponse = metNorway ? response as HttpWebResponse : null;
                        if (metResponse != null && metResponse.StatusCode == HttpStatusCode.NotModified)
                        {
                            if (cachedMet == null)
                                throw new InvalidDataException(stage + ": 304 без сохранённого ответа.");
                            T unchanged = ParseMetCachedJson<T>(cachedMet.Body, stage);
                            token.ThrowIfCancellationRequested();
                            MetHttpResponseCache.Save(url, cachedMet.Body,
                                metResponse.Headers[HttpResponseHeader.LastModified],
                                metResponse.Headers[HttpResponseHeader.Expires], cachedMet);
                            return unchanged;
                        }
                        if (stream == null) throw new InvalidDataException(stage + ": пустой ответ сервера.");
                        if (response.ContentLength > MaxWeatherJsonBytes)
                            throw new InvalidDataException(stage + ": JSON превышает лимит 2 МиБ.");
                        using (MemoryStream json = new MemoryStream())
                        {
                            byte[] buffer = new byte[8192];
                            int count;
                            try
                            {
                                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (json.Length + count > MaxWeatherJsonBytes)
                                        throw new InvalidDataException(stage + ": JSON превышает лимит 2 МиБ.");
                                    json.Write(buffer, 0, count);
                                }
                            }
                            catch (IOException error)
                            {
                                SocketException socket = error.InnerException as SocketException;
                                if (socket == null ||
                                    (socket.SocketErrorCode != SocketError.ConnectionReset &&
                                     socket.SocketErrorCode != SocketError.ConnectionAborted &&
                                     socket.SocketErrorCode != SocketError.NetworkReset))
                                    throw;
                                throw new WebException(stage + ": соединение прервано при получении JSON.",
                                    error, WebExceptionStatus.ReceiveFailure, null);
                            }
                            token.ThrowIfCancellationRequested();
                            // Content-Length is measured on the HTTP wire. For compressed
                            // MET responses it cannot be compared to decompressed JSON.
                            if (!metNorway && response.ContentLength >= 0 &&
                                json.Length < response.ContentLength)
                                throw new WebException(stage + ": соединение закрыто до получения полного JSON.",
                                    WebExceptionStatus.ConnectionClosed);
                            json.Position = 0;
                            T result = (T)new DataContractJsonSerializer(typeof(T)).ReadObject(json);
                            token.ThrowIfCancellationRequested();
                            if (metNorway)
                                MetHttpResponseCache.Save(url, json.ToArray(),
                                    response.Headers[HttpResponseHeader.LastModified],
                                    response.Headers[HttpResponseHeader.Expires], null);
                            return result;
                        }
                    }
                }
                catch (WebException ex)
                {
                    if (ex.Status == WebExceptionStatus.Timeout)
                    {
                        if (ex.Response != null) ex.Response.Dispose();
                        throw new TimeoutException(stage + ": превышено время ожидания (9 с).", ex);
                    }
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    // HttpWebRequest exposes 304 as ProtocolError on .NET Framework.
                    if (metNorway && response != null &&
                        response.StatusCode == HttpStatusCode.NotModified && cachedMet != null)
                    {
                        using (response)
                        {
                            if (response.ResponseUri == null ||
                                response.ResponseUri.Scheme != Uri.UriSchemeHttps)
                                throw new InvalidDataException(stage + ": ответ 304 получен не по HTTPS.");
                            T unchanged = ParseMetCachedJson<T>(cachedMet.Body, stage);
                            token.ThrowIfCancellationRequested();
                            MetHttpResponseCache.Save(url, cachedMet.Body,
                                response.Headers[HttpResponseHeader.LastModified],
                                response.Headers[HttpResponseHeader.Expires], cachedMet);
                            return unchanged;
                        }
                    }
                    retryTls = allowTlsRetry && ex.Status == WebExceptionStatus.SecureChannelFailure &&
                        NetworkTls.RetryWithTls12();
                    string detail = response == null ? ex.Message : "HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
                    // RefreshWeather classifies this error after the response has been disposed.
                    if (response != null) ex.Data["AppShelf.HttpStatusCode"] = (int)response.StatusCode;
                    if (response != null) response.Dispose();
                    if (!retryTls) throw new InvalidOperationException(stage + ": " + detail, ex);
                }
            }
            if (retryTls) return ReadJsonWorker<T>(url, stage, false, token);
            throw new InvalidOperationException(stage + ": неизвестная ошибка TLS.");
        }

        public static bool IsKemerovo(string city)
        {
            string normalized = city.Trim().ToLowerInvariant().Replace('ё', 'е');
            return normalized == "кемерово" || normalized == "kemerovo" || normalized == "кемерово, россия" ||
                normalized == "kemerovo, russia";
        }

        public static async Task<WeatherSnapshot> GetForecast(string city, string provider)
        {
            string title;
            string timeZoneId = null;
            double latitude, longitude;
            if (IsKemerovo(city))
            {
                // Verified UNGEGN city coordinates; avoids a second API host for Kemerovo.
                title = "Кемерово";
                latitude = 55.333333;
                longitude = 86.083333;
            }
            else
            {
                string query = Uri.EscapeDataString(city.Trim());
                GeoSearchResponse result = await ReadJson<GeoSearchResponse>(
                    "https://geocoding-api.open-meteo.com/v1/search?name=" + query +
                    "&count=5&language=ru&format=json", "Поиск города");
                if (result == null || result.Results == null || result.Results.Count == 0)
                    throw new InvalidOperationException("Город не найден: " + city);
                GeoPlace place = result.Results[0];
                if (place == null || !place.Latitude.HasValue || !place.Longitude.HasValue ||
                    Double.IsNaN(place.Latitude.Value) || Double.IsNaN(place.Longitude.Value) ||
                    Math.Abs(place.Latitude.Value) > 90 || Math.Abs(place.Longitude.Value) > 180)
                    throw new InvalidDataException("Поиск города: отсутствуют корректные координаты.");
                title = place.Name;
                latitude = place.Latitude.Value;
                longitude = place.Longitude.Value;
                timeZoneId = place.TimeZone;
            }
            string lat = latitude.ToString("R", CultureInfo.InvariantCulture);
            string lon = longitude.ToString("R", CultureInfo.InvariantCulture);
            if (provider != "openmeteo")
                return await GetMetNorwayForecast(title, lat, lon, IsKemerovo(city), timeZoneId);
            // One HTTPS request provides current conditions plus exactly seven local calendar days.
            ForecastResponse forecast = await ReadJson<ForecastResponse>(
                "https://api.open-meteo.com/v1/forecast?latitude=" + lat + "&longitude=" + lon +
                "&current=temperature_2m,apparent_temperature,weather_code" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min," +
                "apparent_temperature_max,apparent_temperature_min,precipitation_probability_max" +
                "&timezone=auto&forecast_days=7", "Прогноз погоды");
            if (forecast == null || forecast.Current == null || !forecast.Current.Temperature.HasValue || forecast.Daily == null)
                throw new InvalidDataException("Прогноз погоды: сервер вернул неполные данные.");
            DailyWeather daily = forecast.Daily;
            if (daily.Dates == null)
                throw new InvalidDataException("Прогноз погоды: отсутствуют ежедневные показатели.");
            int count = daily.Dates.Count;
            if (count > 7) count = 7;
            if (count == 0)
                throw new InvalidDataException("Прогноз погоды: неполный прогноз на неделю.");

            WeatherSnapshot snapshot = new WeatherSnapshot
            {
                City = title,
                Source = "Open-Meteo",
                Temperature = forecast.Current.Temperature.Value,
                ApparentTemperature = forecast.Current.ApparentTemperature,
                Condition = Describe(forecast.Current.Code),
                UpdatedAt = DateTime.Now
            };
            for (int i = 0; i < count; i++)
            {
                DateTime day;
                if (!DateTime.TryParseExact(daily.Dates[i], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out day))
                    throw new InvalidDataException("Прогноз погоды: неверная дата " + daily.Dates[i]);
                snapshot.Days.Add(new WeatherDay
                {
                    Date = day,
                    Condition = Describe(DailyValue(daily.Codes, i)),
                    Min = DailyValue(daily.Min, i), Max = DailyValue(daily.Max, i),
                    ApparentMin = DailyValue(daily.ApparentMin, i), ApparentMax = DailyValue(daily.ApparentMax, i),
                    PrecipitationProbability = daily.PrecipitationProbability != null &&
                        daily.PrecipitationProbability.Count > i ? daily.PrecipitationProbability[i] : null
                });
            }
            return snapshot;
        }

        private static T? DailyValue<T>(List<T?> values, int index) where T : struct
        {
            return values != null && index < values.Count ? values[index] : null;
        }

        private static string Describe(int? code)
        {
            if (!code.HasValue) return "н/д";
            if (code == 0) return "ясно";
            if (code == 1 || code == 2) return "малооблачно";
            if (code == 3) return "пасмурно";
            if (code == 45 || code == 48) return "туман";
            if (code == 51 || code == 53 || code == 55 || code == 56 || code == 57) return "морось";
            if (code == 61 || code == 63 || code == 65 || code == 66 || code == 67 || code == 80 || code == 81 || code == 82) return "дождь";
            if (code == 71 || code == 73 || code == 75 || code == 77 || code == 85 || code == 86) return "снег";
            if (code >= 95) return "гроза";
            return "переменная погода";
        }
    }

    // Lightweight, modeless 7-day forecast. It only displays cached data fetched by MainWindow.
    public sealed class WeatherForecastWindow : Window
    {
        private readonly StackPanel _details;
        private readonly TextBlock _location;
        private readonly TextBlock _source;
        private readonly TextBlock _credits;
        private static readonly Brush ForegroundBrush = FluentUi.Foreground;
        private static readonly Brush Muted = FluentUi.Secondary;
        private static readonly Brush Surface = FluentUi.Surface;
        private static readonly Brush Stripe = FluentUi.SurfaceRaised;

        private static TextBlock Label(string text, double size, Brush brush)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        }

        private static void AddCreditLink(TextBlock target, string label, string url)
        {
            Hyperlink link = new Hyperlink(new Run(label));
            link.Foreground = FluentUi.Accent;
            link.NavigateUri = new Uri(url);
            link.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { ConfigStore.Log(ex); }
            };
            target.Inlines.Add(link);
        }

        public WeatherForecastWindow(Action refresh, Action changeCity)
        {
            Title = "AppShelf — погода на неделю";
            Width = 760;
            Height = 690;
            // Leave room for all five forecast columns and the vertical scrollbar.
            MinWidth = 700;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FluentUi.PrepareWindow(this);

            DockPanel layout = new DockPanel();
            Content = layout;
            StackPanel header = new StackPanel { Margin = new Thickness(22, 18, 22, 13) };
            Border headerCard = new Border { Background = FluentUi.HeaderGradient(),
                BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(16) };
            headerCard.Child = header;
            DockPanel.SetDock(headerCard, Dock.Top);
            layout.Children.Add(headerCard);
            TextBlock title = Label("Прогноз на 7 дней", 23, ForegroundBrush);
            title.FontWeight = FontWeights.Bold;
            header.Children.Add(title);
            _location = Label("", 12, Muted);
            _location.Margin = new Thickness(0, 4, 0, 0);
            header.Children.Add(_location);

            StackPanel bottom = new StackPanel { Margin = new Thickness(22, 8, 20, 18) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            layout.Children.Add(bottom);
            _source = Label("Источник погоды", 11, Muted);
            bottom.Children.Add(_source);
            _credits = Label("", 11, Muted);
            _credits.Margin = new Thickness(0, 5, 0, 0);
            bottom.Children.Add(_credits);
            WrapPanel controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 9, 0, 0) };
            bottom.Children.Add(controls);
            Button update = new Button { Content = "Обновить", MinHeight = 30,
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 9, 0) };
            update.Click += delegate { refresh(); };
            controls.Children.Add(update);
            Button settings = new Button { Content = "Изменить город", MinHeight = 30,
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 9, 0) };
            settings.Click += delegate { changeCity(); };
            controls.Children.Add(settings);
            Button close = new Button { Content = "Закрыть", MinHeight = 30,
                Padding = new Thickness(12, 5, 12, 5) };
            close.Click += delegate { Close(); };
            controls.Children.Add(close);

            ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(16, 2, 16, 8) };
            FluentUi.StyleSettingsScroll(scroller);
            layout.Children.Add(scroller);
            _details = new StackPanel();
            scroller.Content = _details;
        }

        private static void AddCell(Grid row, int index, string value, Brush brush, bool bold)
        {
            TextBlock cell = Label(value, 12, brush);
            cell.Margin = new Thickness(5, 2, 5, 2);
            if (bold) cell.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(cell, index);
            row.Children.Add(cell);
        }

        public void UpdateForecast(WeatherSnapshot snapshot, string city, string error, bool pending, bool saved = false)
        {
            _details.Children.Clear();
            _credits.Inlines.Clear();
            _credits.Visibility = snapshot == null ? Visibility.Collapsed : Visibility.Visible;
            if (snapshot != null)
            {
                bool met = String.Equals(snapshot.Source, "MET Norway", StringComparison.OrdinalIgnoreCase);
                bool openMeteo = String.Equals(snapshot.Source, "Open-Meteo", StringComparison.OrdinalIgnoreCase);
                if (met || openMeteo)
                {
                    _credits.Inlines.Add(new Run("Погодные данные: "));
                    AddCreditLink(_credits, snapshot.Source,
                        met ? "https://api.met.no/" : "https://open-meteo.com/");
                    _credits.Inlines.Add(new Run(" · "));
                    AddCreditLink(_credits, "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/");
                    if (met)
                    {
                        _credits.Inlines.Add(new Run(" / "));
                        AddCreditLink(_credits, "NLOD 2.0", "https://api.met.no/doc/License");
                    }
                    _credits.Inlines.Add(new Run(". AppShelf форматирует и агрегирует прогноз."));
                }
            }
            _source.Text = snapshot == null ? "Источник: выбранный в настройках" :
                "Источник: " + (snapshot.Source ?? "неизвестен") +
                " · вероятность осадков отображается, только если доступна в ответе" +
                (String.IsNullOrEmpty(snapshot.DailyRangeNote) ? "" : "\n" + snapshot.DailyRangeNote) +
                (saved ? "\nСохранённые данные" + (String.IsNullOrEmpty(error) ? "" : " · ошибка обновления: " + error) : "");
            _location.Text = snapshot != null ? snapshot.City + (saved ? " · сохранено " : " · обновлено ") +
                snapshot.UpdatedAt.ToString(saved ? "dd.MM HH:mm" : "HH:mm") : city;
            if (snapshot == null)
            {
                TextBlock info = Label(pending ? "Получаем прогноз…" :
                    (String.IsNullOrWhiteSpace(error) ? "Прогноз ещё не загружен." : "Ошибка: " + error),
                    14, ForegroundBrush);
                info.Margin = new Thickness(8, 18, 8, 12);
                _details.Children.Add(info);
                return;
            }

            TextBlock current = Label((saved ? "На момент обновления: " : "Сейчас: ") + WeatherSnapshot.TemperatureText(snapshot.Temperature) +
                " · ощущается как " + WeatherSnapshot.TemperatureText(snapshot.ApparentTemperature) +
                " · " + snapshot.Condition, 14, ForegroundBrush);
            current.FontWeight = FontWeights.SemiBold;
            current.Margin = new Thickness(7, 9, 4, 15);
            _details.Children.Add(current);

            for (int i = 0; i < snapshot.Days.Count; i++)
            {
                WeatherDay day = snapshot.Days[i];
                Border area = new Border { Background = i % 2 == 0 ? Surface : Stripe,
                    BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(11, 13, 7, 13) };
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                // A partial response can start tomorrow, or refer to a city on another calendar day.
                string dayLabel = day.Date.ToString("ddd, dd.MM", new CultureInfo("ru-RU"));
                AddCell(row, 0, dayLabel, ForegroundBrush, true);
                AddCell(row, 1, day.Condition, ForegroundBrush, false);
                AddCell(row, 2, WeatherSnapshot.TemperatureText(day.Min) + "…" +
                    WeatherSnapshot.TemperatureText(day.Max), ForegroundBrush, true);
                AddCell(row, 3, "ощущ. " + WeatherSnapshot.TemperatureText(day.ApparentMin) +
                    "…" + WeatherSnapshot.TemperatureText(day.ApparentMax), Muted, false);
                AddCell(row, 4, day.PrecipitationProbability.HasValue ?
                    "осадки " + day.PrecipitationProbability.Value + "%" : "осадки —", Muted, false);
                area.Child = row;
                _details.Children.Add(area);
            }
        }
    }

    // PACKAGE11_ENGINE_BEGIN
    // Pure countdown state: explicit UTC input keeps time calculations testable
    // and prevents DispatcherTimer delays from accumulating drift.
    public sealed class CountdownTimerEngine
    {
        private DateTime _deadlineUtc;
        private TimeSpan _remaining;
        public int DurationSeconds { get; private set; }
        public bool Running { get; private set; }
        public bool Paused { get; private set; }
        public bool Completed { get; private set; }

        public CountdownTimerEngine(int durationSeconds)
        {
            Reset(durationSeconds);
        }

        public void Reset(int durationSeconds)
        {
            if (durationSeconds < 1 || durationSeconds > 86399)
                throw new ArgumentOutOfRangeException("durationSeconds");
            DurationSeconds = durationSeconds;
            _remaining = TimeSpan.FromSeconds(durationSeconds);
            Running = false;
            Paused = false;
            Completed = false;
        }

        public TimeSpan RemainingAt(DateTime utcNow)
        {
            if (!Running) return _remaining;
            TimeSpan left = _deadlineUtc - utcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        public void Start(DateTime utcNow)
        {
            if (Running) return;
            if (Completed) Reset(DurationSeconds);
            _deadlineUtc = utcNow + _remaining;
            Running = true;
            Paused = false;
        }

        // True exactly once on the transition to completion.
        public bool Advance(DateTime utcNow)
        {
            if (!Running || utcNow < _deadlineUtc) return false;
            _remaining = TimeSpan.Zero;
            Running = false;
            Paused = false;
            Completed = true;
            return true;
        }

        public void Pause(DateTime utcNow)
        {
            if (!Running) return;
            _remaining = RemainingAt(utcNow);
            Running = false;
            Paused = true;
        }
    }
    // PACKAGE11_ENGINE_END

    public sealed partial class MainWindow : Window
    {
        // Ordinary dialogs in all MainWindow partials use the dark WPF dialog.
        // Keeping this call shape preserves existing test-source interceptors.
        // Program.Main and global startup/rendering failures remain native.
        private static class MessageBox
        {
            internal static MessageBoxResult Show(Window owner, string message, string title,
                MessageBoxButton buttons, MessageBoxImage image)
            {
                return AppDialog.Show(owner, message, title, buttons, image);
            }
        }

        private static readonly Brush BackgroundBrush = FluentUi.Background;
        private static readonly Brush HeaderBrush = FluentUi.Surface;
        private static readonly Brush TileBrush = FluentUi.SurfaceRaised;
        private static readonly Brush HoverBrush = FluentUi.SurfaceHover;
        private static readonly Brush StrokeBrush = FluentUi.BorderColor;
        private static readonly Brush WhiteBrush = FluentUi.Foreground;
        private static readonly Brush MutedBrush = FluentUi.Secondary;
        private static readonly Brush AccentBrush = FluentUi.Accent;

        private AppConfig _config;
        private bool _canSave;
        private DispatcherTimer _windowPlacementSaveTimer;
        private bool _windowPlacementTrackingReady;
        private bool _lastNonMinimizedWasMaximized;
        private StackPanel _tileHost;
        private ScrollViewer _tilesScroller;
        private readonly List<GroupLayoutCard> _groupLayoutCards = new List<GroupLayoutCard>();
        private Grid _freeLayoutGrid;
        private FreeCanvasBoard _canvasBoard;
        private readonly Dictionary<string, StackPanel> _freeZoneHosts =
            new Dictionary<string, StackPanel>(StringComparer.Ordinal);
        private readonly List<Border> _freeZoneCells = new List<Border>();
        private readonly List<TextBlock> _freeZoneHints = new List<TextBlock>();
        private bool _updatingGroupWidths;
        private Grid _mainContentGrid;
        private DockPanel _mainBackgroundHost;
        private Border _footerBorder;
        private TextBlock _empty;
        private TextBlock _status;
        private DispatcherTimer _statusMessageTimer;
        private string _defaultStatusText = "";
        private bool _statusMessageVisible;
        private TextBlock _pingInternational;
        private TextBlock _pingRussian;
        private TextBlock _route;
        private TextBlock _weather;
        private TextBlock _pomodoro;
        private TextBlock _rdpStatus;
        private Border _monitorBorder;
        private WrapPanel _bottomFullMonitors;
        private StackPanel _bottomPanelLayout;
        private DockPanel _bottomCompactRow;
        private Button _bottomExpandButton;
        private WrapPanel _bottomCompactItems;
        private System.Windows.Controls.Primitives.UniformGrid _bottomCategoryTabs;
        private readonly Dictionary<string, Button> _bottomCategoryButtons =
            new Dictionary<string, Button>(StringComparer.Ordinal);
        private WrapPanel _bottomCategoryDetails;
        private string _bottomOpenCategory;
        private bool _bottomCompactExpanded;
        private Border _pingInternationalChip;
        private Border _pingRussianChip;
        private Border _routeChip;
        private Border _weatherChip;
        private Border _pomodoroChip;
        private Border _countdownChip;
        private TextBlock _countdownText;
        private Border _rdpChip;
        private DispatcherTimer _pomodoroTimer;
        private DispatcherTimer _countdownTimer;
        private CountdownTimerEngine _countdownEngine;
        private Window _countdownWindow;
        private TextBlock _countdownClock;
        private TextBlock _countdownStage;
        private Button _countdownToggle;
        private TextBox _countdownHours;
        private TextBox _countdownMinutes;
        private TextBox _countdownSeconds;
        private DispatcherTimer _rdpTimer;
        private bool _pomodoroRunning;
        private int _pomodoroPhase; // 0 = work, 1 = short break, 2 = long break
        private int _completedPomodoros;
        private TimeSpan _pomodoroRemaining;
        private DateTime _pomodoroDeadlineUtc;
        private Window _pomodoroWindow;
        private TextBlock _pomodoroClock;
        private TextBlock _pomodoroStage;
        private Button _pomodoroToggle;
        private HappDiagnosticsWindow _happWindow;
        private MenuItem _groupMenuItem;
        private MenuItem _newGroupMenuItem;
        private MenuItem _collectionsMenuItem;
        private MenuItem _tileModeCompactMenuItem;
        private MenuItem _tileModeNormalMenuItem;
        private MenuItem _tileModeLabeledMenuItem;
        private MenuItem _pinMenuItem;
        private TextBox _search;
        private DispatcherTimer _networkTimer;
        private DispatcherTimer _weatherTimer;
        private bool _networkPending;
        private bool _weatherPending;
        private WeatherSnapshot _lastWeather;
        private bool _lastWeatherIsCached;
        private DateTime _lastWeatherSavedUtc;
        private DispatcherTimer _weatherCacheExpiryTimer;
        private string _lastWeatherCity;
        private string _lastWeatherError;
        private string _lastWeatherProvider;
        // Expected HTTPS certificate/handshake failures get one short diagnostic
        // per city, provider and reason during a session, not a stack on every poll.
        private readonly HashSet<string> _loggedWeatherTlsFailures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _weatherSettingsCity;
        private string _weatherSettingsProvider;
        private bool _weatherSettingsVisible;
        private long _weatherSettingsRevision;
        // Request timestamps only, not a weather cache. Failed/stale attempts count too.
        private readonly Dictionary<string, DateTime> _metWeatherRequests =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private WeatherForecastWindow _forecastWindow;
        private bool _startupHidePending;
        private bool _explicitExitRequested;
        private bool _sessionEnding;
        private Forms.NotifyIcon _trayIcon;
        private Drawing.Icon _trayDrawingIcon;
        private HwndSource _hwndSource;
        private bool _registeredHotkey;
        private const int HotKeyId = 0x4711;
        private const int WmHotkey = 0x0312;
        private readonly Dictionary<string, ImageSource> _iconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _faviconRequested =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Internal drags use a string path, not the AppItem object: OLE drag data must
        // remain serializable, and imported files must never be mistaken for tiles.
        private const string TileDragFormat = "AppShelf.InternalTilePath";
        private const string GroupDragFormat = "AppShelf.InternalGroupName";
        private AppItem _dragCandidate;
        private Point _dragStart;
        private bool _dragInProgress;
        private string _groupDragCandidate;
        private Point _groupDragStart;
        private bool _groupDragInProgress;
        private Border _highlightedDropZone;

        private sealed class GroupLayoutCard
        {
            public readonly Border Card;
            public readonly string Group;
            public readonly int TileCount;
            public readonly int Position;
            public readonly TextBlock Heading;
            public GroupLayoutCard(Border card, string group, int tileCount, int position,
                TextBlock heading)
            {
                Card = card;
                Group = group;
                TileCount = tileCount;
                Position = position;
                Heading = heading;
            }
        }

        private sealed class FreeGroupDropTarget
        {
            public readonly string Zone;
            public readonly Border Surface;
            public FreeGroupDropTarget(string zone, Border surface)
            {
                Zone = zone;
                Surface = surface;
            }
        }

        private sealed class GroupDropTarget
        {
            public readonly string Group;
            public readonly Border Zone;

            public GroupDropTarget(string group, Border zone)
            {
                Group = group;
                Zone = zone;
            }
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetBestInterface(uint destination, out uint interfaceIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

        private static Brush Solid(string color)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static TextBlock Text(string value, double size, Brush color)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = color,
                VerticalAlignment = VerticalAlignment.Center };
        }

        private static Button ActionButton(string label, bool accent)
        {
            return new Button
            {
                Content = label, MinHeight = 33, Padding = new Thickness(11, 6, 11, 6),
                Margin = new Thickness(0, 0, 9, 6), Background = accent ? AccentBrush : TileBrush,
                Foreground = accent ? BackgroundBrush : WhiteBrush,
                BorderBrush = accent ? AccentBrush : StrokeBrush, BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
        }

        public MainWindow() : this(false) { }

        public MainWindow(bool startInTray)
        {
            Title = "AppShelf — быстрый запуск";
            try
            {
                // Use the same approved AppShelf emblem as the EXE and tray icon.
                Icon = FluentUi.BrandImageSource() ??
                    ShellIcon.Get(Process.GetCurrentProcess().MainModule.FileName);
            }
            catch (Exception ex) { ConfigStore.Log(ex); }
            Width = 850;
            Height = 580;
            MinWidth = 510;
            MinHeight = 390;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FluentUi.PrepareWindow(this);
            Background = BackgroundBrush;
            FontFamily = new FontFamily("Segoe UI");
            Foreground = WhiteBrush;

            string warning;
            _config = ConfigStore.Load(out warning, out _canSave);
            Topmost = _config.AlwaysOnTop;
            RestoreWindowPlacement();
            _pomodoroRemaining = TimeSpan.FromMinutes(_config.PomodoroWorkMinutes);
            _countdownEngine = new CountdownTimerEngine(
                _config.CountdownDurationSeconds >= 1 && _config.CountdownDurationSeconds <= 86399
                    ? _config.CountdownDurationSeconds : 300);
            if (_canSave)
            {
                List<string> createdShortcuts = new List<string>();
                try
                {
                    AppConfig migrated = _config.Copy();
                    int changed = ConfigStore.MigrateShortcuts(migrated, createdShortcuts);
                    if (changed != 0)
                    {
                        ConfigStore.Save(migrated);
                        migrated.ApplySavedItemCopies();
                        _config = migrated;
                    }
                }
                catch (Exception ex)
                {
                    ConfigStore.Log(ex);
                    warning = (warning ?? "") + "\nНе удалось перенести часть старых ярлыков: " + ex.Message;
                }
                finally { DeleteUnusedShortcutCopies(createdShortcuts); }
            }
            InitializeReminders();
            BuildLayout();
            CreateTray();
            RenderTiles();
            // Persist position/size after the user stops moving or resizing the main window.
            _windowPlacementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _windowPlacementSaveTimer.Tick += delegate
            {
                _windowPlacementSaveTimer.Stop();
                SaveWindowPlacement();
            };
            LocationChanged += OnWindowGeometryChanged;
            SizeChanged += OnWindowGeometryChanged;
            // Wait for the first Loaded event so fast HTTP failures are always visible.
            Loaded += delegate
            {
                _windowPlacementTrackingReady = true;
                if (_startupHidePending)
                {
                    _startupHidePending = false;
                    if (_trayIcon != null && _trayIcon.Visible && _trayIcon.Icon != null)
                        Hide();
                    else
                    {
                        // Never strand a background process if Windows refused the tray icon.
                        ShowInTaskbar = true;
                        ShowActivated = true;
                        WindowState = _lastNonMinimizedWasMaximized
                            ? WindowState.Maximized : WindowState.Normal;
                        Activate();
                    }
                }
                if (_networkTimer == null) StartMonitors();
                StartAutomaticBackupMonitor();
                StartPluginRuntime();
            };
            SourceInitialized += OnWindowSourceInitialized;
            StateChanged += OnWindowStateChanged;
            Closing += OnWindowClosing;
            Closed += OnClosed;
            if (Application.Current != null)
                Application.Current.SessionEnding += OnApplicationSessionEnding;
            if (startInTray)
            {
                // No visible taskbar button or active window on Windows logon.
                // Wait for Loaded before hiding so background monitors still initialize.
                _startupHidePending = true;
                ShowActivated = false;
                ShowInTaskbar = false;
                WindowState = WindowState.Minimized;
            }
            if (!String.IsNullOrWhiteSpace(warning))
            {
                string message = warning;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    MessageBox.Show(this, message, "AppShelf — настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
            }
        }

        // Short activity messages return to the current app count and shortcuts
        // after five seconds. Important persistent warnings stay until another action.
        private void ShowStatusMessage(string message, bool persistent = false)
        {
            if (_status == null) return;
            if (_statusMessageTimer != null) _statusMessageTimer.Stop();
            _statusMessageVisible = true;
            _status.Text = message;
            if (!persistent && _statusMessageTimer != null) _statusMessageTimer.Start();
        }

        private void ShowDefaultStatus(int visibleCount)
        {
            _defaultStatusText = visibleCount + " из " + _config.Items.Count +
                " приложений   ·   Ctrl+O — добавить   ·   Ctrl+F — поиск   ·   " +
                "Перетащи плитку для перемещения   ·   Правая кнопка — действия";
            if (!_statusMessageVisible && _status != null)
                _status.Text = _defaultStatusText;
        }

        // Only the lower panel backdrop is translucent. Its content remains fully visible.
        internal static int NormalizeBottomPanelOpacity(int? opacity)
        {
            return opacity.HasValue && opacity.Value >= 0 && opacity.Value <= 100
                ? opacity.Value : 100;
        }

        private void ApplyBottomPanelOpacity(int opacity)
        {
            if (_footerBorder == null) return;
            opacity = NormalizeBottomPanelOpacity(opacity);
            if (opacity == 100)
            {
                _footerBorder.Background = HeaderBrush;
                return;
            }
            Brush backdrop = HeaderBrush.Clone();
            backdrop.Opacity = opacity / 100.0;
            backdrop.Freeze();
            _footerBorder.Background = backdrop;
        }

        // A single image/backdrop is painted behind both the tiles and the footer,
        // so making the footer transparent reveals the same continuous wallpaper.
        private void ApplyMainBackground(string preset)
        {
            if (_mainBackgroundHost == null) return;
            _mainBackgroundHost.Background = FluentUi.MainWindowBackground(preset,
                Math.Max(1, _mainBackgroundHost.ActualWidth),
                Math.Max(1, _mainBackgroundHost.ActualHeight));
        }

        // UI 2.0: updated visual composition. Launch, persistence, DnD and monitors are unchanged.
        private void BuildLayout()
        {
            DockPanel root = new DockPanel { Background = BackgroundBrush };
            Content = root;

            // A single row releases vertical space for application groups.
            Border header = new Border
            {
                Background = FluentUi.HeaderGradient(),
                BorderBrush = StrokeBrush, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 9, 14, 8)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            DockPanel backgroundArea = new DockPanel
            {
                Background = FluentUi.MainWindowBackground(
                    _config.MainBackgroundPreset, 850, 520)
            };
            _mainBackgroundHost = backgroundArea;
            backgroundArea.SizeChanged += delegate(object sender, SizeChangedEventArgs e)
            {
                if (FluentUi.NormalizeMainBackground(_config.MainBackgroundPreset) == "blue-arcs")
                    ApplyMainBackground(_config.MainBackgroundPreset);
            };
            // The header remains opaque, while the body shares one continuous background.
            root.Children.Add(backgroundArea);
            Grid commandRow = new Grid();
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Child = commandRow;

            StackPanel brand = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 13, 0),
                ToolTip = "AppShelf · Ctrl+Alt+Space — показать или скрыть окно"
            };
            // Real branding asset, not the temporary square-grid text glyph.
            ImageSource brandMark = FluentUi.BrandImageSource() ?? Icon;
            if (brandMark != null)
            {
                brand.Children.Add(new Image
                {
                    Source = brandMark, Width = 32, Height = 32,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    Margin = new Thickness(0, 0, 8, 0)
                });
            }
            TextBlock title = Text("AppShelf", 19, WhiteBrush);
            title.FontWeight = FontWeights.Bold;
            brand.Children.Add(title);
            Grid.SetColumn(brand, 0);
            commandRow.Children.Add(brand);

            Button add = ActionButton("+  Добавить", true);
            add.Name = "AddItemButton";
            add.MinWidth = 104;
            add.MinHeight = 35;
            add.Margin = new Thickness(0, 0, 9, 0);
            add.Padding = new Thickness(10, 0, 10, 0);
            add.VerticalAlignment = VerticalAlignment.Center;
            add.HorizontalContentAlignment = HorizontalAlignment.Center;
            add.VerticalContentAlignment = VerticalAlignment.Center;
            // Use a geometrically centered plus instead of a font glyph followed by spaces.
            Grid plusIcon = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 7, 0) };
            plusIcon.Children.Add(new Border
            {
                Width = 10, Height = 1.5, Background = BackgroundBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            plusIcon.Children.Add(new Border
            {
                Width = 1.5, Height = 10, Background = BackgroundBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            TextBlock addLabel = Text("Добавить", 12.5, BackgroundBrush);
            addLabel.FontWeight = FontWeights.SemiBold;
            StackPanel addContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            addContent.Children.Add(plusIcon);
            addContent.Children.Add(addLabel);
            add.Content = addContent;
            add.ToolTip = "Добавить приложение, сайт или виджет (Ctrl+O — выбрать файл)";
            ContextMenu addMenu = new ContextMenu();
            MenuItem addProgram = new MenuItem { Header = "Приложение или ярлык…" };
            addProgram.Click += delegate { AddFromDialog(); };
            addMenu.Items.Add(addProgram);
            MenuItem addWebsite = new MenuItem { Header = "Сайт по ссылке…" };
            addWebsite.Click += delegate { ShowAddSiteDialog(null, ""); };
            addMenu.Items.Add(addWebsite);
            MenuItem addFolder = new MenuItem { Header = "Папку…" };
            addFolder.Click += delegate { AddFolderFromDialog(); };
            addMenu.Items.Add(addFolder);
            MenuItem addWidget = new MenuItem { Header = "Виджет плагина…", Icon = FluentUi.MenuGlyph("puzzle") };
            addWidget.Click += delegate { ShowAddPluginWidget(); };
            addMenu.Items.Add(addWidget);
            addMenu.Opened += delegate { addWidget.IsEnabled = _canSave && WidgetsInFreeLayout(); };
            add.Click += delegate
            {
                addMenu.PlacementTarget = add;
                addMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                addMenu.IsOpen = true;
            };
            Grid.SetColumn(add, 1);
            commandRow.Children.Add(add);

            // Native TextBox drawing fixes Cyrillic/Latin glyph clipping.
            // The search card grows and shrinks with the window, but never wraps away
            // from the input and menu actions.
            Border searchCard = new Border
            {
                MinWidth = 80, Height = 35,
                Background = TileBrush,
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid searchGrid = new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchCard.Child = searchGrid;
            Grid.SetColumn(searchCard, 2);
            commandRow.Children.Add(searchCard);
            TextBlock searchIcon = Text("⌕", 22, AccentBrush);
            searchIcon.HorizontalAlignment = HorizontalAlignment.Center;
            searchIcon.IsHitTestVisible = false;
            Grid.SetColumn(searchIcon, 0);
            searchGrid.Children.Add(searchIcon);

            // Shape-based dots stay centered regardless of font glyph metrics.
            Button more = ActionButton("", false);
            more.Width = 43;
            more.Height = 35;
            more.Margin = new Thickness(0);
            more.Padding = new Thickness(0);
            StackPanel moreDots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            for (int i = 0; i < 3; i++)
            {
                moreDots.Children.Add(new Border
                {
                    Width = 4, Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = more.Foreground,
                    Margin = new Thickness(1.5, 0, 1.5, 0)
                });
            }
            more.Content = moreDots;
            more.ToolTip = "Дополнительные действия и настройки";
            Grid.SetColumn(more, 3);
            commandRow.Children.Add(more);

            ContextMenu tools = new ContextMenu();
            MenuItem reminders = new MenuItem { Header = "Напоминания", Icon = FluentUi.MenuGlyph("bell") };
            reminders.Click += delegate { ShowRemindersWindow(); };
            tools.Items.Add(reminders);
            _collectionsMenuItem = new MenuItem
            {
                Header = "Подборки", Icon = FluentUi.MenuGlyph("folder"),
                Visibility = _config.ShowCollections == false ? Visibility.Collapsed : Visibility.Visible
            };
            _collectionsMenuItem.Click += delegate { ShowCollectionsWindow(); };
            tools.Items.Add(_collectionsMenuItem);
            tools.Items.Add(CreatePluginToolsMenu(tools));
            _newGroupMenuItem = new MenuItem
            {
                Header = "Создать группу", Icon = FluentUi.MenuGlyph("folder"),
                IsEnabled = _config.GroupingEnabled
            };
            _newGroupMenuItem.Click += delegate { CreateGroup(); };
            tools.Items.Add(_newGroupMenuItem);
            MenuItem gallery = new MenuItem
            {
                Header = "Выбрать фон…", Icon = FluentUi.MenuGlyph("settings")
            };
            gallery.Click += delegate { ShowBackgroundGallery(); };
            tools.Items.Add(gallery);
            tools.Items.Add(new Separator());

            MenuItem quick = new MenuItem { Header = "Быстрые настройки", Icon = FluentUi.MenuGlyph("sliders") };
            _groupMenuItem = new MenuItem { Header = "Группировка приложений",
                IsCheckable = true, IsChecked = _config.GroupingEnabled };
            _groupMenuItem.Click += delegate { SetGrouping(_groupMenuItem.IsChecked); };
            quick.Items.Add(_groupMenuItem);
            MenuItem tileModes = new MenuItem { Header = "Режим плиток" };
            _tileModeCompactMenuItem = new MenuItem { Header = "Компактный", IsCheckable = true };
            _tileModeNormalMenuItem = new MenuItem { Header = "Обычный", IsCheckable = true };
            _tileModeLabeledMenuItem = new MenuItem { Header = "Обычный с подписями", IsCheckable = true };
            _tileModeCompactMenuItem.Click += delegate { SetTileDisplayMode("compact"); };
            _tileModeNormalMenuItem.Click += delegate { SetTileDisplayMode("normal"); };
            _tileModeLabeledMenuItem.Click += delegate { SetTileDisplayMode("labeled"); };
            tileModes.Items.Add(_tileModeCompactMenuItem);
            tileModes.Items.Add(_tileModeNormalMenuItem);
            tileModes.Items.Add(_tileModeLabeledMenuItem);
            quick.Items.Add(tileModes);
            UpdateTileModeMenu();
            // Only the three layout modes are quick actions; detailed options
            // (width, alignment and nine-zone positions) belong in Settings.
            MenuItem groupLayoutModes = new MenuItem { Header = "Размещение групп" };
            Dictionary<string, MenuItem> groupLayoutChoices =
                new Dictionary<string, MenuItem>(StringComparer.Ordinal);
            Action refreshGroupLayoutChoices = delegate
            {
                string currentMode = NormalizeGroupLayoutMode(_config.GroupLayoutMode);
                foreach (KeyValuePair<string, MenuItem> pair in groupLayoutChoices)
                    pair.Value.IsChecked = pair.Key == currentMode;
                groupLayoutModes.IsEnabled = _config.GroupingEnabled;
            };
            foreach (string layoutMode in new[] { "vertical", "grid", "free", "canvas" })
            {
                string chosen = layoutMode;
                MenuItem option = new MenuItem
                {
                    Header = chosen == "vertical" ? "Список" :
                        chosen == "grid" ? "Автоматическая сетка" : chosen == "canvas" ? "Свободное полотно" : "Свободное размещение",
                    IsCheckable = true
                };
                option.Click += delegate
                {
                    SetGroupLayoutMode(chosen);
                    refreshGroupLayoutChoices();
                };
                groupLayoutModes.Items.Add(option);
                groupLayoutChoices.Add(chosen, option);
            }
            groupLayoutModes.SubmenuOpened += delegate { refreshGroupLayoutChoices(); };
            refreshGroupLayoutChoices();
            quick.Items.Add(groupLayoutModes);
            MenuItem canvasCommands = new MenuItem { Header = "Расставить группы" };
            string[] commands = { "vertical", "rows", "compact", "left", "right", "reset" };
            string[] titles = { "Вертикально", "Рядами", "Уплотнить", "По левому краю", "По правому краю", "Сбросить расположение" };
            for (int i = 0; i < commands.Length; i++)
            {
                string command = commands[i];
                MenuItem item = new MenuItem { Header = titles[i] };
                item.Click += delegate { ArrangeCanvas(command); };
                canvasCommands.Items.Add(item);
            }
            quick.Items.Add(canvasCommands);
            quick.SubmenuOpened += delegate { canvasCommands.Visibility = _canvasBoard == null ? Visibility.Collapsed : Visibility.Visible;
                canvasCommands.IsEnabled = _canSave && (_search == null || String.IsNullOrWhiteSpace(_search.Text)); };
            canvasCommands.Visibility = Visibility.Collapsed;
            _pinMenuItem = new MenuItem { Header = "Поверх остальных окон",
                IsCheckable = true, IsChecked = _config.AlwaysOnTop };
            _pinMenuItem.Click += delegate
            {
                if (!_canSave) { ShowSaveDisabled(); _pinMenuItem.IsChecked = _config.AlwaysOnTop; return; }
                AppConfig pending = _config.Copy();
                pending.AlwaysOnTop = _pinMenuItem.IsChecked;
                if (SaveConfigChanges(pending)) Topmost = _config.AlwaysOnTop;
                _pinMenuItem.IsChecked = _config.AlwaysOnTop;
            };
            quick.Items.Add(_pinMenuItem);
            quick.Items.Add(new Separator());
            MenuItem panelModes = new MenuItem { Header = "Нижняя панель" };
            Dictionary<string, MenuItem> panelChoices = new Dictionary<string, MenuItem>();
            foreach (string panelMode in new string[] { "compact", "categories", "detailed" })
            {
                string chosen = panelMode;
                MenuItem item = new MenuItem { Header = chosen == "compact" ? "Компактная" :
                    chosen == "categories" ? "Категории" : "Подробная", IsCheckable = true };
                item.Click += delegate
                {
                    SetBottomPanelMode(chosen);
                    foreach (KeyValuePair<string, MenuItem> pair in panelChoices)
                        pair.Value.IsChecked = pair.Key == NormalizeBottomPanelMode(_config.BottomPanelMode);
                };
                panelModes.Items.Add(item);
                panelChoices.Add(chosen, item);
            }
            quick.Items.Add(panelModes);
            MenuItem sound = new MenuItem { Header = "Звук включён", IsCheckable = true };
            sound.Click += delegate { SetAppSounds(sound.IsChecked); sound.IsChecked = _config.SoundsEnabled != false; };
            quick.Items.Add(sound);
            quick.SubmenuOpened += delegate
            {
                _groupMenuItem.IsChecked = _config.GroupingEnabled;
                _pinMenuItem.IsChecked = _config.AlwaysOnTop;
                UpdateTileModeMenu();
                refreshGroupLayoutChoices();
                foreach (KeyValuePair<string, MenuItem> pair in panelChoices)
                    pair.Value.IsChecked = pair.Key == NormalizeBottomPanelMode(_config.BottomPanelMode);
                sound.IsChecked = _config.SoundsEnabled != false;
            };
            tools.Items.Add(quick);

            MenuItem utilities = new MenuItem { Header = "Инструменты", Icon = FluentUi.MenuGlyph("tools") };
            MenuItem start = new MenuItem { Header = "Открыть папку «Пуск»" };
            start.Click += delegate { ShowStartMenuFolder(); };
            utilities.Items.Add(start);
            MenuItem backup = new MenuItem { Header = "Экспорт / импорт данных…" };
            backup.Click += delegate { ShowBackupMenuWindow(); };
            utilities.Items.Add(backup);
            MenuItem rdpTool = new MenuItem { Header = "RDP-антипростой", IsCheckable = true };
            rdpTool.Click += delegate { SetRdpKeepAwake(rdpTool.IsChecked); rdpTool.IsChecked = _config.RdpKeepAwake; };
            utilities.Items.Add(rdpTool);
            utilities.SubmenuOpened += delegate { rdpTool.IsChecked = _config.RdpKeepAwake; };
            tools.Items.Add(utilities);
            tools.Items.Add(new Separator());
            MenuItem settings = new MenuItem { Header = "Настройки…", Icon = FluentUi.MenuGlyph("settings") };
            settings.Click += delegate { ShowSettings(); };
            tools.Items.Add(settings);
            MenuItem about = new MenuItem { Header = "О программе", Icon = FluentUi.MenuGlyph("info") };
            about.Click += delegate { ShowAboutWindow(); };
            tools.Items.Add(about);
            more.Click += delegate
            {
                tools.PlacementTarget = more;
                tools.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                tools.IsOpen = true;
            };

            _search = new TextBox
            {
                // Override the implicit FluentUi.TextBoxStyle only for the search field.
                // This restores the framework's native editing/rendering template.
                Style = new Style(typeof(TextBox)),
                Height = 31,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = WhiteBrush,
                CaretBrush = AccentBrush,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 0, 9, 0),
                ToolTip = "Введите название приложения для поиска"
            };
            Grid.SetColumn(_search, 1);
            searchGrid.Children.Add(_search);
            _search.TextChanged += delegate { RenderTiles(); };
            _search.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    AppItem first = FirstMatch();
                    if (first != null) LaunchItem(first, first.RunAsAdmin);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _search.Clear();
                    e.Handled = true;
                }
            };

            Border footer = new Border
            {
                Background = HeaderBrush, BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(21, 11, 18, 9)
            };
            _footerBorder = footer;
            ApplyBottomPanelOpacity(NormalizeBottomPanelOpacity(_config.BottomPanelOpacity));
            DockPanel.SetDock(footer, Dock.Bottom);
            backgroundArea.Children.Add(footer);
            StackPanel footerStack = new StackPanel();
            footer.Child = footerStack;
            _monitorBorder = new Border { Padding = new Thickness(0, 0, 0, 1) };
            footerStack.Children.Add(_monitorBorder);
            WrapPanel monitors = new WrapPanel { Orientation = Orientation.Horizontal };
            _monitorBorder.Child = monitors;
            _pingInternational = Text("1.1.1.1: проверяем…", 12, WhiteBrush);
            _pingRussian = Text("ya.ru: проверяем…", 12, WhiteBrush);
            _route = Text("Маршрут: проверяем…", 12, MutedBrush);
            _weather = Text("Погода: укажи город", 12, WhiteBrush);
            // The detailed weather chip must also keep full conditions visible.
            _weather.TextWrapping = TextWrapping.Wrap;
            _weather.MaxWidth = 310;
            _pomodoro = Text("Помодоро: 25:00", 12, WhiteBrush);
            _countdownText = Text("Таймер: 00:05:00", 12, WhiteBrush);
            _rdpStatus = Text("RDP: проверяем…", 12, MutedBrush);
            _pingInternationalChip = FluentUi.Chip(_pingInternational, FluentUi.Green, false);
            _pingRussianChip = FluentUi.Chip(_pingRussian, FluentUi.Green, false);
            _routeChip = FluentUi.Chip(_route, AccentBrush, true);
            _weatherChip = FluentUi.Chip(_weather, FluentUi.Amber, true);
            _pomodoroChip = FluentUi.Chip(_pomodoro, FluentUi.Green, true);
            _countdownChip = FluentUi.Chip(_countdownText, FluentUi.Amber, true);
            _rdpChip = FluentUi.Chip(_rdpStatus, AccentBrush, true);
            CreateRemindersChip();
            _routeChip.ToolTip = "Открыть диагностику сети Windows, DNS, HTTPS и прокси.";
            _routeChip.MouseLeftButtonUp += delegate { ToggleBottomModuleWindow("route"); };
            _pomodoroChip.ToolTip = "Нажми, чтобы открыть Помодоро.";
            _pomodoroChip.MouseLeftButtonUp += delegate { ToggleBottomModuleWindow("pomodoro"); };
            _countdownChip.ToolTip = "Нажми, чтобы открыть обычный таймер.";
            _countdownChip.MouseLeftButtonUp += delegate { ToggleBottomModuleWindow("countdown"); };
            _rdpChip.ToolTip = "Режим антипростоя RDP. Нажми, чтобы открыть настройки.";
            _rdpChip.MouseLeftButtonUp += delegate { ShowSettingsSection("windows"); };
            _weatherChip.ToolTip = "Нажми для прогноза на 7 дней. Правая кнопка — параметры.";
            _weatherChip.MouseLeftButtonUp += delegate { ToggleBottomModuleWindow("weather"); };
            AttachWeatherContextMenu(_weatherChip);
            monitors.Children.Add(_pingInternationalChip);
            monitors.Children.Add(_pingRussianChip);
            monitors.Children.Add(_routeChip);
            monitors.Children.Add(_weatherChip);
            monitors.Children.Add(_pomodoroChip);
            monitors.Children.Add(_countdownChip);
            monitors.Children.Add(_rdpChip);
            monitors.Children.Add(_remindersChip);
            _bottomFullMonitors = monitors;
            InitializeBottomPanelLayouts();
            CreatePluginWidgetSurface();
            _bottomPanelLayout.Children.Add(_pluginWidgets.IndicatorHost);
            _status = Text("", 11.5, WhiteBrush);
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.ToolTip = "Ctrl+O — добавить · Ctrl+F — поиск · Перетащи плитки, чтобы изменить порядок или группу";
            // A separate, consistently dark background keeps status text legible
            // even when the footer itself is fully transparent over a bright image.
            Border statusBackdrop = new Border
            {
                Background = FluentUi.Brush("#BF101827"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 2, 0, 0),
                Child = _status
            };
            footerStack.Children.Add(statusBackdrop);
            _statusMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusMessageTimer.Tick += delegate
            {
                _statusMessageTimer.Stop();
                _statusMessageVisible = false;
                if (_status != null) _status.Text = _defaultStatusText;
            };
            UpdateMonitorVisibility();

            // A transparent hit-testable surface keeps tile DnD intact and exposes
            // the body wallpaper even beneath the footer when opacity is lowered.
            Grid contentGrid = new Grid { Background = Brushes.Transparent };
            _mainContentGrid = contentGrid;
            backgroundArea.Children.Add(contentGrid);
            ScrollViewer scroller = new ScrollViewer
            {
                Name = "AppShelfTilesScroller",
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(21, 17, 17, 16)
            };
            // Match Settings and About without affecting nested tile/group controls.
            FluentUi.StyleSettingsScroll(scroller);
            _tilesScroller = scroller;
            scroller.SizeChanged += delegate { UpdateGroupWidths(); };
            scroller.ScrollChanged += delegate(object sender, ScrollChangedEventArgs e)
            {
                if (Math.Abs(e.ViewportWidthChange) > 0.5) UpdateGroupWidths();
            };
            _tileHost = new StackPanel { AllowDrop = true };
            scroller.AllowDrop = true;
            contentGrid.AllowDrop = true;
            StackPanel workspace = new StackPanel();
            workspace.Children.Add(_tileHost);
            scroller.Content = workspace;
            contentGrid.Children.Add(scroller);
            _empty = Text("Здесь пока нет приложений.\n\nПеретащи ярлык из Проводника\nили нажми «Добавить приложение».", 16, MutedBrush);
            _empty.TextAlignment = TextAlignment.Center;
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.IsHitTestVisible = false;
            contentGrid.Children.Add(_empty);

            AllowDrop = true;
            PreviewDragEnter += OnDragOver;
            PreviewDragOver += OnDragOver;
            PreviewDragLeave += OnDragLeave;
            PreviewDrop += OnDropData;
            PreviewMouseLeftButtonUp += delegate
            {
                _dragCandidate = null;
                _groupDragCandidate = null;
            };
            PreviewKeyDown += OnKeyDown;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
            {
                AddFromDialog(); e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                _search.Focus(); _search.SelectAll(); e.Handled = true;
            }
        }

        private void CreateTray()
        {
            try
            {
                _trayIcon = new Forms.NotifyIcon();
                try { _trayDrawingIcon = Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); }
                catch { _trayDrawingIcon = (Drawing.Icon)Drawing.SystemIcons.Application.Clone(); }
                // ExtractAssociatedIcon may return null without throwing. Visible alone does not
                // mean Windows can show a NotifyIcon: it also needs a non-null image.
                if (_trayDrawingIcon == null) _trayDrawingIcon = (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
                _trayIcon.Icon = _trayDrawingIcon;
                _trayIcon.Text = "AppShelf — быстрый запуск";
                Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
                Forms.ToolStripMenuItem show = new Forms.ToolStripMenuItem("Открыть AppShelf");
                show.Click += delegate { ShowPanel(); };
                menu.Items.Add(show);
                Forms.ToolStripMenuItem remindersMenu = new Forms.ToolStripMenuItem("Напоминания");
                remindersMenu.Click += delegate { Dispatcher.BeginInvoke(new Action(ShowRemindersWindow)); };
                menu.Items.Add(remindersMenu);
                Forms.ToolStripMenuItem exit = new Forms.ToolStripMenuItem("Выход");
                exit.Click += delegate
                {
                    // Closing from the tray is an explicit exit, never a hide.
                    _explicitExitRequested = true;
                    Close();
                };
                menu.Items.Add(exit);
                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += delegate { ShowPanel(); };
                _trayIcon.Visible = true;
            }
            catch (Exception ex) { ConfigStore.Log(ex); }
        }

        private void ShowPanel()
        {
            ShowInTaskbar = true;
            ShowActivated = true;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized)
            {
                bool maximize = _lastNonMinimizedWasMaximized;
                WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
            }
            Activate();
            Topmost = _config.AlwaysOnTop;
            _search.Focus();
        }

        private void RestoreWindowPlacement()
        {
            if (!_config.WindowPlacementSaved) return;
            if (!ConfigStore.IsValidGeometry(_config.WindowLeft, _config.WindowTop,
                _config.WindowWidth, _config.WindowHeight)) return;

            Width = Math.Max(MinWidth, _config.WindowWidth);
            Height = Math.Max(MinHeight, _config.WindowHeight);
            Left = _config.WindowLeft;
            Top = _config.WindowTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
            _lastNonMinimizedWasMaximized = _config.WindowMaximized;
            if (_config.WindowMaximized) WindowState = WindowState.Maximized;
        }

        private void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            // GetWindowRect and Screen.WorkingArea both use device pixels.
            // This avoids comparing WPF DIPs with Win32 pixels on high-DPI displays.
            try
            {
                if (_config.WindowPlacementSaved)
                {
                    IntPtr handle = new WindowInteropHelper(this).Handle;
                    NativeRect native;
                    if (GetWindowRect(handle, out native))
                    {
                        Drawing.Rectangle bounds = Drawing.Rectangle.FromLTRB(
                            native.Left, native.Top, native.Right, native.Bottom);
                        bool enoughVisible = false;
                        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
                        {
                            Drawing.Rectangle intersection = Drawing.Rectangle.Intersect(
                                bounds, screen.WorkingArea);
                            if (intersection.Width >= 100 && intersection.Height >= 75)
                            {
                                enoughVisible = true;
                                break;
                            }
                        }
                        if (!enoughVisible)
                        {
                            // Saved monitor has been unplugged or its layout has changed.
                            // Keep the saved size when possible, but return to the primary screen.
                            Rect work = SystemParameters.WorkArea;
                            WindowState = WindowState.Normal;
                            Width = Math.Min(Math.Max(MinWidth, Width), Math.Max(MinWidth, work.Width - 30));
                            Height = Math.Min(Math.Max(MinHeight, Height), Math.Max(MinHeight, work.Height - 30));
                            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
                            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
                        }
                    }
                }
            }
            catch (Exception ex) { ConfigStore.Log(ex); }
            RegisterGlobalShortcut(sender, e);
        }

        private void OnWindowGeometryChanged(object sender, EventArgs e)
        {
            if (!_windowPlacementTrackingReady || !_canSave || _windowPlacementSaveTimer == null)
                return;
            _windowPlacementSaveTimer.Stop();
            _windowPlacementSaveTimer.Start();
        }

        private void SaveWindowPlacement()
        {
            if (!_windowPlacementTrackingReady || !_canSave) return;
            try
            {
                // RestoreBounds retains last normal bounds even while minimized to tray.
                Rect bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;
                if (!ConfigStore.IsValidGeometry(bounds.Left, bounds.Top,
                    bounds.Width, bounds.Height)) return;
                bool maximized = WindowState == WindowState.Maximized ||
                    (WindowState == WindowState.Minimized && _lastNonMinimizedWasMaximized);
                AppConfig pending = _config.Copy();
                pending.WindowLeft = bounds.Left;
                pending.WindowTop = bounds.Top;
                pending.WindowWidth = bounds.Width;
                pending.WindowHeight = bounds.Height;
                pending.WindowMaximized = maximized;
                pending.WindowPlacementSaved = true;
                SaveConfigChanges(pending, false);
            }
            catch (Exception ex) { ConfigStore.Log(ex); }
        }

        private void OnApplicationSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            // Windows logoff/shutdown must not be turned into "hide to tray".
            _sessionEnding = true;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_windowPlacementSaveTimer != null) _windowPlacementSaveTimer.Stop();
            SaveWindowPlacement();
            if (!_explicitExitRequested && !_sessionEnding &&
                _config != null && _config.CloseToTrayEnabled &&
                _trayIcon != null && _trayIcon.Visible && _trayIcon.Icon != null &&
                (Application.Current == null || !Application.Current.Dispatcher.HasShutdownStarted))
            {
                // Alt+F4 and the title-bar X both keep the application running.
                // If the tray icon is unavailable, Close is allowed to exit.
                e.Cancel = true;
                Hide();
            }
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            if (WindowState != WindowState.Minimized)
                _lastNonMinimizedWasMaximized = WindowState == WindowState.Maximized;
            // Always use the ordinary Windows taskbar when the user minimizes.
            // Capture normal bounds, not the transient minimized state.
            if (WindowState == WindowState.Minimized && _windowPlacementSaveTimer != null)
            {
                _windowPlacementSaveTimer.Stop();
                SaveWindowPlacement();
            }
            else if (_windowPlacementTrackingReady) OnWindowGeometryChanged(sender, e);
        }

        private void RegisterGlobalShortcut(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(handle);
            if (_hwndSource == null) return;
            _hwndSource.AddHook(GlobalHotKeyMessage);
            // MOD_CONTROL | MOD_ALT; VK_SPACE.
            _registeredHotkey = RegisterHotKey(handle, HotKeyId, 0x0002 | 0x0001, 0x20);
            if (!_registeredHotkey)
                ShowStatusMessage("Ctrl+Alt+Space занято другой программой. Открывай AppShelf через трей.", true);
        }

        private IntPtr GlobalHotKeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotKeyId)
            {
                if (IsVisible && WindowState != WindowState.Minimized && IsActive)
                {
                    if (_windowPlacementSaveTimer != null) _windowPlacementSaveTimer.Stop();
                    SaveWindowPlacement();
                    Hide();
                }
                else ShowPanel();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            StopPluginRuntime();
            if (Application.Current != null)
                Application.Current.SessionEnding -= OnApplicationSessionEnding;
            if (_windowPlacementSaveTimer != null) _windowPlacementSaveTimer.Stop();
            if (_automaticBackupTimer != null) _automaticBackupTimer.Stop();
            if (_statusMessageTimer != null) _statusMessageTimer.Stop();
            if (_networkTimer != null) _networkTimer.Stop();
            if (_weatherTimer != null) _weatherTimer.Stop();
            if (_weatherCacheExpiryTimer != null) _weatherCacheExpiryTimer.Stop();
            if (_pomodoroTimer != null) _pomodoroTimer.Stop();
            if (_countdownTimer != null) _countdownTimer.Stop();
            if (_rdpTimer != null) _rdpTimer.Stop();
            StopReminders();
            if (_hwndSource != null)
            {
                if (_registeredHotkey) UnregisterHotKey(new WindowInteropHelper(this).Handle, HotKeyId);
                _hwndSource.RemoveHook(GlobalHotKeyMessage);
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                if (_trayIcon.ContextMenuStrip != null) _trayIcon.ContextMenuStrip.Dispose();
                _trayIcon.Dispose();
            }
            if (_trayDrawingIcon != null) _trayDrawingIcon.Dispose();
        }

        private void StartMonitors()
        {
            _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _networkTimer.Tick += delegate { RefreshNetwork(); };
            _networkTimer.Start();
            _weatherTimer = new DispatcherTimer { Interval = WeatherRefreshInterval() };
            _weatherTimer.Tick += delegate { RefreshWeather(); };
            _weatherTimer.Start();
            _pomodoroTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pomodoroTimer.Tick += delegate { TickPomodoro(); };
            _pomodoroTimer.Start();
            UpdateCountdownAvailability();
            _rdpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(50) };
            _rdpTimer.Tick += delegate { TickRdpKeepAwake(); };
            _rdpTimer.Start();
            StartReminderTimer();
            UpdatePomodoroUi();
            UpdateCountdownUi();
            UpdateRdpStatus();
            RefreshNetwork();
            RefreshWeather();
        }

        private void UpdateMonitorVisibility()
        {
            _pingInternationalChip.Visibility = LegacyNetworkEnabled ? Visibility.Visible : Visibility.Collapsed;
            _pingRussianChip.Visibility = LegacyNetworkEnabled ? Visibility.Visible : Visibility.Collapsed;
            _routeChip.Visibility = LegacyNetworkEnabled ? Visibility.Visible : Visibility.Collapsed;
            _weatherChip.Visibility = _config.ShowWeather ? Visibility.Visible : Visibility.Collapsed;
            _pomodoroChip.Visibility = _config.ShowPomodoro ? Visibility.Visible : Visibility.Collapsed;
            _countdownChip.Visibility = _config.ShowCountdownTimer != false ? Visibility.Visible : Visibility.Collapsed;
            _rdpChip.Visibility = _config.RdpKeepAwake ? Visibility.Visible : Visibility.Collapsed;
            // Reminders remain available even if all optional status monitors are hidden.
            _monitorBorder.Visibility = Visibility.Visible;
            ApplyBottomPanelMode();
        }

        private static readonly string[] BottomPanelKeys = new string[]
        {
            "network1", "network2", "route", "weather", "pomodoro", "countdown", "rdp", "reminders"
        };

        private string BottomIndicatorName(string key)
        {
            switch (key)
            {
                case "network1": return NetworkChecks.Label(NetworkChecks.Resolve(_config, 1), 1);
                case "network2": return NetworkChecks.Label(NetworkChecks.Resolve(_config, 2), 2);
                case "route": return "Сеть";
                case "weather": return "Погода";
                case "pomodoro": return "Помодоро";
                case "countdown": return "Таймер";
                case "rdp": return "RDP";
                default: return "Напоминания";
            }
        }

        // Favorites identify stable network slots, not today's configurable addresses.
        // The actual target still appears in the live check result and diagnostics.
        private static string BottomFavoriteName(string key)
        {
            if (key == "network1") return "Проверка сети 1";
            if (key == "network2") return "Проверка сети 2";
            return null;
        }

        private static string NormalizeBottomPanelMode(string value)
        {
            return value == "compact" || value == "categories" ? value : "detailed";
        }

        private static List<string> BottomPanelCategories(AppConfig config)
        {
            string[] defaults = new string[] { "network", "weather", "time" };
            if (config == null || config.BottomPanelCategories == null)
                return new List<string>(defaults);
            List<string> enabled = new List<string>();
            foreach (string category in defaults)
                if (config.BottomPanelCategories.Contains(category)) enabled.Add(category);
            // Older or damaged settings must never leave an unusable empty panel.
            return enabled.Count == 0 ? new List<string>(defaults) : enabled;
        }

        private static List<string> BottomPanelFavorites(AppConfig config)
        {
            List<string> result = new List<string>();
            IList<string> choices = config == null ? null : config.BottomPanelFavorites;
            if (choices == null)
                choices = new string[] { "route", "weather", "countdown", "reminders" };
            foreach (string key in choices)
            {
                if (Array.IndexOf(BottomPanelKeys, key) >= 0 && !result.Contains(key))
                    result.Add(key);
            }
            return result;
        }

        private bool BottomIndicatorEnabled(string key)
        {
            switch (key)
            {
                case "network1": case "network2": case "route": return LegacyNetworkEnabled;
                case "weather": return _config.ShowWeather;
                case "pomodoro": return _config.ShowPomodoro;
                case "countdown": return _config.ShowCountdownTimer != false;
                case "rdp": return _config.RdpKeepAwake;
                default: return true;
            }
        }

        private TextBlock BottomIndicatorText(string key)
        {
            switch (key)
            {
                case "network1": return _pingInternational;
                case "network2": return _pingRussian;
                case "route": return _route;
                case "weather": return _weather;
                case "pomodoro": return _pomodoro;
                case "countdown": return _countdownText;
                case "rdp": return _rdpStatus;
                default: return _remindersChipText;
            }
        }

        // Only direct clicks on a bottom-panel indicator toggle the module window.
        // Other entry points keep their existing activate-or-open behaviour.
        private void ToggleBottomModuleWindow(string key)
        {
            Window opened = key == "weather" ? (Window)_forecastWindow :
                key == "pomodoro" ? _pomodoroWindow :
                key == "countdown" ? _countdownWindow :
                key == "reminders" ? _remindersWindow :
                key == "route" || key == "network1" || key == "network2" ? (Window)_happWindow : null;
            if (opened != null && opened.IsVisible)
            {
                opened.Close();
                return;
            }
            if (key == "route" || key == "network1" || key == "network2") ShowHappDiagnostics();
            else if (key == "weather") ShowForecastWindow();
            else if (key == "pomodoro") ShowPomodoroWindow();
            else if (key == "countdown") ShowCountdownWindow();
            else if (key == "rdp") ShowSettingsSection("windows");
            else ShowRemindersWindow();
        }

        private Button BottomIndicatorButton(string key, bool compact)
        {
            // Show full weather and reminder text on multiple lines. Other
            // indicators remain single-line, and WrapPanel still reflows buttons.
            bool multiline = key == "weather" || key == "reminders";
            TextBlock label = new TextBlock
            {
                FontSize = compact ? 11 : 12,
                Foreground = WhiteBrush,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = multiline ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                MaxWidth = compact ? (multiline ? 210 : 145) : 310
            };
            label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
            {
                Source = BottomIndicatorText(key),
                Mode = System.Windows.Data.BindingMode.OneWay
            });
            Button button = ActionButton("", false);
            button.Content = label;
            button.ToolTip = BottomIndicatorName(key) + ". Нажми для открытия инструмента.";
            button.Margin = new Thickness(0, 0, 5, 5);
            button.MinHeight = 32;
            button.Click += delegate
            {
                ToggleBottomModuleWindow(key);
            };
            if (key == "weather") AttachWeatherContextMenu(button);
            if (compact)
                button.SizeChanged += delegate { AlignCompactExpandButton(); };
            return button;
        }

        // Match the tallest visible compact indicator, not the height of the
        // entire WrapPanel (which may contain several rows on narrow windows).
        private void AlignCompactExpandButton()
        {
            if (_bottomExpandButton == null || _bottomCompactItems == null) return;
            double height = 0;
            foreach (UIElement child in _bottomCompactItems.Children)
            {
                FrameworkElement item = child as FrameworkElement;
                if (item != null && item.ActualHeight > height)
                    height = item.ActualHeight;
            }
            if (height <= 0) return;
            height = Math.Max(_bottomExpandButton.MinHeight, height);
            if (Double.IsNaN(_bottomExpandButton.Height) ||
                Math.Abs(_bottomExpandButton.Height - height) > 0.5)
                _bottomExpandButton.Height = height;
        }

        private void InitializeBottomPanelLayouts()
        {
            _monitorBorder.Child = null;
            _bottomPanelLayout = new StackPanel();
            _monitorBorder.Child = _bottomPanelLayout;

            _bottomCompactRow = new DockPanel { LastChildFill = true };
            _bottomPanelLayout.Children.Add(_bottomCompactRow);
            Button expand = ActionButton("Подробнее ▾", false);
            _bottomExpandButton = expand;
            expand.MinWidth = 98;
            // Match the indicator cards' bottom spacing and their live height.
            expand.Margin = new Thickness(8, 0, 0, 5);
            expand.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(expand, Dock.Right);
            _bottomCompactRow.Children.Add(expand);
            expand.Click += delegate
            {
                _bottomCompactExpanded = !_bottomCompactExpanded;
                expand.Content = _bottomCompactExpanded ? "Свернуть ▴" : "Подробнее ▾";
                ApplyBottomPanelMode();
            };
            _bottomCompactItems = new WrapPanel();
            _bottomCompactItems.SizeChanged += delegate { AlignCompactExpandButton(); };
            _bottomCompactRow.Children.Add(_bottomCompactItems);

            _bottomCategoryTabs = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3 };
            _bottomPanelLayout.Children.Add(_bottomCategoryTabs);
            foreach (string category in new string[] { "network", "weather", "time" })
            {
                string chosen = category;
                Button button = ActionButton("", false);
                button.Tag = chosen;
                button.Style = FluentUi.BottomCategoryButtonStyle();
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Padding = new Thickness(8, 7, 8, 7);
                button.Margin = new Thickness(3, 0, 3, 0);
                Grid card = new Grid();
                card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                card.Children.Add(FluentUi.BottomCategoryIcon(category));
                TextBlock title = Text(category == "network" ? "Сеть" : category == "weather" ? "Погода" : "Время", 12, WhiteBrush);
                title.TextAlignment = TextAlignment.Center;
                title.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(title, 1);
                card.Children.Add(title);
                TextBlock summary = Text("", 11, MutedBrush);
                summary.TextAlignment = TextAlignment.Center;
                summary.TextTrimming = TextTrimming.CharacterEllipsis;
                System.Windows.Data.MultiBinding binding = new System.Windows.Data.MultiBinding
                {
                    Converter = new BottomCategorySummary(this, category),
                    Mode = System.Windows.Data.BindingMode.OneWay
                };
                foreach (TextBlock source in new TextBlock[] { _route, _weather, _countdownText, _pomodoro })
                    binding.Bindings.Add(new System.Windows.Data.Binding("Text") { Source = source });
                // The configuration object is replaced when settings are saved.
                binding.Bindings.Add(new System.Windows.Data.Binding("Tag") { Source = _bottomCategoryTabs });
                summary.SetBinding(TextBlock.TextProperty, binding);
                Grid.SetRow(summary, 2);
                card.Children.Add(summary);
                button.Content = card;
                button.ToolTip = category == "network" ? "Сетевые инструменты" :
                    category == "weather" ? "Прогноз погоды" : "Таймер, Помодоро и напоминания";
                if (category == "weather") AttachWeatherContextMenu(button);
                button.Click += delegate
                {
                    _bottomOpenCategory = _bottomOpenCategory == chosen ? null : chosen;
                    RefreshBottomCategoryDetails();
                };
                _bottomCategoryButtons.Add(chosen, button);
            }
            _bottomCategoryDetails = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            _bottomPanelLayout.Children.Add(_bottomCategoryDetails);
            _bottomPanelLayout.Children.Add(_bottomFullMonitors);

            ContextMenu menu = new ContextMenu();
            foreach (string mode in new string[] { "compact", "categories", "detailed" })
            {
                string selected = mode;
                MenuItem item = new MenuItem { Header = mode == "compact" ? "Компактная панель" :
                    mode == "categories" ? "По категориям" : "Подробная панель" };
                item.Click += delegate
                {
                    if (!_canSave) return;
                    AppConfig settings = _config.Copy();
                    settings.BottomPanelMode = selected;
                    if (SaveConfigChanges(settings)) ApplyBottomPanelMode();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            MenuItem configure = new MenuItem { Header = "Настроить нижнюю панель…" };
            configure.Click += delegate { ShowSettingsSection("appearance-panel"); };
            menu.Items.Add(configure);
            MenuItem indicator = new MenuItem { Header = "Добавить индикатор плагина…" };
            indicator.Click += delegate { ShowAddPluginIndicator(); };
            menu.Items.Add(indicator);
            menu.Opened += delegate { indicator.IsEnabled = _canSave; };
            _monitorBorder.ContextMenu = menu;
        }

        private sealed class BottomCategorySummary : System.Windows.Data.IMultiValueConverter
        {
            private readonly MainWindow _window;
            private readonly string _category;
            public BottomCategorySummary(MainWindow window, string category) { _window = window; _category = category; }
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                AppConfig config = _window._config;
                if (_category == "network")
                    return config.ShowNetwork && !config.NetworkPluginMigrated ? System.Convert.ToString(values[0]).Replace("Маршрут: ", "") : "Мониторинг выключен";
                if (_category == "weather")
                {
                    if (!config.ShowWeather) return "Погода выключена";
                    string weather = System.Convert.ToString(values[1]);
                    // Extract only the current temperature from the existing live indicator.
                    System.Text.RegularExpressions.Match temperature =
                        System.Text.RegularExpressions.Regex.Match(weather, @"-?\d+ °C");
                    return temperature.Success ? temperature.Value : weather.Replace("Погода: ", "");
                }
                string countdown = System.Convert.ToString(values[2]);
                string pomodoro = System.Convert.ToString(values[3]);
                if (config.ShowPomodoro && (pomodoro.Contains("▶") || config.ShowCountdownTimer == false))
                    return pomodoro.Replace("Помодоро: ", "");
                if (config.ShowCountdownTimer != false) return countdown.Replace("Таймер: ", "");
                return "Таймеры выключены";
            }
            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }

        private void RefreshBottomCategoryDetails()
        {
            if (_bottomCategoryDetails == null) return;
            _bottomCategoryDetails.Children.Clear();
            if (_bottomOpenCategory == null)
            {
                _bottomCategoryDetails.Visibility = Visibility.Collapsed;
                return;
            }
            foreach (string key in BottomPanelKeys)
            {
                bool categoryMatch = _bottomOpenCategory == "network"
                    ? (key == "network1" || key == "network2" || key == "route" || key == "rdp")
                    : _bottomOpenCategory == "weather" ? key == "weather"
                    : (key == "pomodoro" || key == "countdown" || key == "reminders");
                if (categoryMatch && BottomIndicatorEnabled(key))
                    _bottomCategoryDetails.Children.Add(BottomIndicatorButton(key, false));
            }
            _bottomCategoryDetails.Visibility = _bottomCategoryDetails.Children.Count == 0
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyBottomPanelMode()
        {
            if (_bottomPanelLayout == null || _config == null) return;
            string mode = NormalizeBottomPanelMode(_config.BottomPanelMode);
            _bottomCategoryTabs.Tag = _config;
            List<string> categories = BottomPanelCategories(_config);
            _bottomCategoryTabs.Columns = categories.Count;
            _bottomCategoryTabs.Children.Clear();
            foreach (string category in categories)
                _bottomCategoryTabs.Children.Add(_bottomCategoryButtons[category]);
            if (_bottomOpenCategory != null && !categories.Contains(_bottomOpenCategory))
                _bottomOpenCategory = null;
            if (mode != "compact" && _bottomCompactExpanded)
            {
                _bottomCompactExpanded = false;
                ((Button)_bottomCompactRow.Children[0]).Content = "Подробнее ▾";
            }
            _bottomCompactRow.Visibility = mode == "compact" ? Visibility.Visible : Visibility.Collapsed;
            _bottomCategoryTabs.Visibility = mode == "categories" ? Visibility.Visible : Visibility.Collapsed;
            _bottomFullMonitors.Visibility = mode == "detailed" ||
                (mode == "compact" && _bottomCompactExpanded) ? Visibility.Visible : Visibility.Collapsed;
            _bottomCompactItems.Children.Clear();
            if (mode == "compact")
            {
                foreach (string key in BottomPanelFavorites(_config))
                    if (BottomIndicatorEnabled(key))
                        _bottomCompactItems.Children.Add(BottomIndicatorButton(key, true));
                if (_bottomCompactItems.Children.Count == 0)
                    _bottomCompactItems.Children.Add(Text("Нет выбранных показателей", 12, MutedBrush));
                AlignCompactExpandButton();
            }
            if (mode != "categories") _bottomCategoryDetails.Visibility = Visibility.Collapsed;
            else RefreshBottomCategoryDetails();
        }

        private bool LegacyNetworkEnabled { get { return _config.ShowNetwork && !_config.NetworkPluginMigrated; } }

        private bool NetworkTargetsCurrent(NetworkChecks.Target first, NetworkChecks.Target second)
        {
            if (!LegacyNetworkEnabled) return false;
            NetworkChecks.Target currentFirst = NetworkChecks.Resolve(_config, 1);
            NetworkChecks.Target currentSecond = NetworkChecks.Resolve(_config, 2);
            return String.Equals(first.Mode, currentFirst.Mode, StringComparison.Ordinal) &&
                String.Equals(first.Value, currentFirst.Value, StringComparison.Ordinal) &&
                String.Equals(second.Mode, currentSecond.Mode, StringComparison.Ordinal) &&
                String.Equals(second.Value, currentSecond.Value, StringComparison.Ordinal);
        }

        private async void RefreshNetwork()
        {
            if (_networkPending || !LegacyNetworkEnabled) return;
            _networkPending = true;
            // Keep the targets that actually started these asynchronous probes.
            NetworkChecks.Target firstTarget = NetworkChecks.Resolve(_config, 1);
            NetworkChecks.Target secondTarget = NetworkChecks.Resolve(_config, 2);
            try
            {
                Task<string> first = NetworkChecks.ProbeAsync(firstTarget, 1);
                Task<string> second = NetworkChecks.ProbeAsync(secondTarget, 2);
                await Task.WhenAll(first, second);
                // A settings save may replace the targets while these probes run.
                // Never publish a result belonging to the previous configuration.
                if (!IsLoaded || !NetworkTargetsCurrent(firstTarget, secondTarget)) return;
                _pingInternational.Text = first.Result;
                _pingRussian.Text = second.Result;
                _route.Text = RouteSummary();
            }
            catch (Exception ex)
            {
                if (!IsLoaded || !NetworkTargetsCurrent(firstTarget, secondTarget)) return;
                ConfigStore.Log(ex);
                _pingInternational.Text = "Проверка 1: ошибка";
                _pingRussian.Text = "Проверка 2: ошибка";
                _route.Text = RouteSummary();
            }
            finally
            {
                _networkPending = false;
                // A RefreshNetwork call during the old request was skipped by
                // _networkPending. Start one check for the latest saved targets.
                if (IsLoaded && LegacyNetworkEnabled &&
                    !NetworkTargetsCurrent(firstTarget, secondTarget))
                    RefreshNetwork();
            }
        }

        private static string RouteSummary()
        {
            // Reports Windows' selected interface, not the VPN app's toggle state.
            try
            {
                uint index;
                uint dest = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
                if (GetBestInterface(dest, out index) != 0) return "Маршрут: неизвестен";
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        IPv4InterfaceProperties properties = nic.GetIPProperties().GetIPv4Properties();
                        if (properties != null && properties.Index == (int)index)
                        {
                            if (nic.Name.IndexOf("happ", StringComparison.OrdinalIgnoreCase) >= 0)
                                return "Маршрут: Happ TUN";
                            string shortName = nic.Name.Length > 24 ? nic.Name.Substring(0, 24) + "…" : nic.Name;
                            return "Маршрут: " + shortName;
                        }
                    }
                    catch (NetworkInformationException) { }
                }
                return "Маршрут: интерфейс #" + index;
            }
            catch { return "Маршрут: неизвестен"; }
        }

        internal static async Task<string> ProbeEndpoint(string host, string httpsUrl)
        {
            // Run tests simultaneously. HTTPS measures the HTTP request, not ICMP latency.
            Task<long?> icmp = TryIcmp(host);
            Task<long?> https = TryHttps(httpsUrl);
            await Task.WhenAll(icmp, https);
            string icmpText = icmp.Result.HasValue ? "ICMP " + icmp.Result.Value + " мс" : "ICMP —";
            string connectionText;
            if (https.Result.HasValue)
                connectionText = "HTTPS " + https.Result.Value + " мс";
            else
            {
                // An HTTPS failure may be due to TLS or HTTP policy, not routing.
                long? tcp = await TryTcp(host);
                connectionText = tcp.HasValue ? "TCP 443 " + (tcp.Value == 0 ? "<1" : tcp.Value.ToString()) +
                    " мс (HTTPS сбой)" : "HTTPS/TCP —";
            }
            return host + ": " + icmpText + " · " + connectionText;
        }

        private static Task<long?> TryTcp(string host)
        {
            return TryTcp(host, 443);
        }

        internal static async Task<long?> TryTcp(string host, int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    Task connect = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(1800)) != connect) return null;
                    await connect;
                    watch.Stop();
                    return watch.ElapsedMilliseconds;
                }
            }
            catch { return null; }
        }

        internal static async Task<long?> TryIcmp(string host)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply result = await ping.SendPingAsync(host, 1500);
                    if (result.Status == IPStatus.Success) return result.RoundtripTime;
                }
            }
            catch { /* ICMP can be blocked: not a network failure by itself. */ }
            return null;
        }

        internal static async Task<long?> TryHttps(string url)
        {
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.UserAgent = "AppShelf/1.4 (network check)";
                request.Timeout = 3500;
                request.ReadWriteTimeout = 3500;
                request.AllowAutoRedirect = true;
                Stopwatch watch = Stopwatch.StartNew();
                Task<WebResponse> responseTask = request.GetResponseAsync();
                if (await Task.WhenAny(responseTask, Task.Delay(3500)) != responseTask)
                {
                    request.Abort();
                    return null;
                }
                try
                {
                    using (WebResponse response = await responseTask)
                    {
                        watch.Stop();
                        return watch.ElapsedMilliseconds;
                    }
                }
                catch (WebException ex)
                {
                    // A valid HTTP error (e.g. 403/405) still proves TLS/HTTP reachability.
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        response.Dispose();
                        watch.Stop();
                        return watch.ElapsedMilliseconds;
                    }
                    return null;
                }
            }
            catch { return null; }
        }

        // Network availability is an expected state of a weather widget. Do
        // not treat it like a defect; malformed data and unexpected exceptions
        // still need their full stacks in error.log.
        private static bool IsExpectedWeatherNetworkFailure(Exception error, out bool tlsFailure)
        {
            tlsFailure = false;
            for (Exception current = error; current != null; current = current.InnerException)
            {
                if (current is TimeoutException) return true;
                if (current is System.Security.Authentication.AuthenticationException)
                {
                    tlsFailure = true;
                    return true;
                }
                WebException web = current as WebException;
                if (web == null) continue;
                if (web.Status == WebExceptionStatus.SecureChannelFailure ||
                    web.Status == WebExceptionStatus.TrustFailure)
                {
                    tlsFailure = true;
                    return true;
                }
                switch (web.Status)
                {
                    case WebExceptionStatus.Timeout:
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.ConnectionClosed:
                    case WebExceptionStatus.ReceiveFailure:
                    case WebExceptionStatus.SendFailure:
                    case WebExceptionStatus.KeepAliveFailure:
                    case WebExceptionStatus.RequestCanceled:
                        return true;
                    case WebExceptionStatus.ProtocolError:
                        HttpWebResponse response = web.Response as HttpWebResponse;
                        int? status = web.Data["AppShelf.HttpStatusCode"] as int?;
                        if (!status.HasValue && response != null)
                        {
                            try { status = (int)response.StatusCode; }
                            catch (ObjectDisposedException) { }
                        }
                        if (status == 408 || status == 429 || status >= 500)
                            return true;
                        break;
                }
            }
            return false;
        }

        private static string WeatherErrorText(Exception error)
        {
            // Read nested causes: the weather client wraps the original WebException.
            for (Exception current = error; current != null; current = current.InnerException)
            {
                if (current is TimeoutException) return "тайм-аут запроса";
                WebException web = current as WebException;
                if (web == null) continue;
                if (web.Status == WebExceptionStatus.SecureChannelFailure)
                    return "ошибка TLS/HTTPS";
                if (web.Status == WebExceptionStatus.TrustFailure)
                    return "неверный сертификат HTTPS";
                if (web.Status == WebExceptionStatus.NameResolutionFailure)
                    return "ошибка DNS";
                if (web.Status == WebExceptionStatus.ConnectFailure)
                    return "нет соединения с сервисом";
                if (web.Status == WebExceptionStatus.Timeout)
                    return "тайм-аут HTTPS";
                if (web.Status == WebExceptionStatus.ProtocolError)
                {
                    HttpWebResponse response = web.Response as HttpWebResponse;
                    if (response != null) return "HTTP " + (int)response.StatusCode;
                }
                return "сбой HTTPS: " + web.Status;
            }
            string message = error.Message ?? "неизвестная ошибка";
            return message.Length <= 72 ? message : message.Substring(0, 69) + "…";
        }

        private void UpdateForecastWindow()
        {
            if (_forecastWindow == null) return;
            string city = (_config.WeatherCity ?? "").Trim();
            bool matches = _config.ShowWeather && _lastWeatherProvider == WeatherProviderKey(_config.WeatherProvider) &&
                String.Equals(_lastWeatherCity, city, StringComparison.OrdinalIgnoreCase);
            WeatherSnapshot relevant = matches ? _lastWeather : null;
            _forecastWindow.UpdateForecast(relevant, city,
                matches ? _lastWeatherError : null, _weatherPending && _config.ShowWeather,
                matches && _lastWeatherIsCached);
        }

        private void ShowForecastWindow()
        {
            if (String.IsNullOrWhiteSpace(_config.WeatherCity))
            {
                ShowSettingsSection("weather");
                return;
            }
            if (_forecastWindow != null)
            {
                if (_forecastWindow.WindowState == WindowState.Minimized)
                    _forecastWindow.WindowState = WindowState.Normal;
                _forecastWindow.Activate();
                return;
            }
            _forecastWindow = new WeatherForecastWindow(
                delegate { RefreshWeather(); }, delegate { ShowSettingsSection("weather"); });
            _forecastWindow.Owner = this;
            _forecastWindow.Closed += delegate { _forecastWindow = null; };
            UpdateForecastWindow();
            _forecastWindow.Show();
            if (_lastWeather == null && !_weatherPending) RefreshWeather();
        }

        private TimeSpan WeatherRefreshInterval()
        {
            // Outside the Nordic region MET Norway asks for no more than one request every 3 h.
            return _config.WeatherProvider == "metno" || String.IsNullOrEmpty(_config.WeatherProvider)
                ? TimeSpan.FromHours(3) : TimeSpan.FromMinutes(30);
        }

        private static string WeatherProviderKey(string provider)
        {
            return provider == "openmeteo" ? provider : "metno";
        }

        private void AttachWeatherContextMenu(FrameworkElement indicator)
        {
            ContextMenu menu = CreateWeatherContextMenu();
            indicator.ContextMenu = menu;
            // The lower-panel container also has a menu. Handle the right click
            // at the weather indicator, before it can select the container's menu.
            // ContextMenu remains attached for the keyboard context-menu key.
            indicator.PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Right) return;
                e.Handled = true;
                menu.PlacementTarget = indicator;
                menu.IsOpen = true;
            };
        }

        private ContextMenu CreateWeatherContextMenu()
        {
            // Each weather indicator gets its own menu; WPF context menus have one owner.
            ContextMenu menu = new ContextMenu();
            MenuItem forecast = new MenuItem { Header = "Прогноз на неделю" };
            forecast.Click += delegate { ShowForecastWindow(); };
            menu.Items.Add(forecast);
            MenuItem refresh = new MenuItem { Header = "Обновить погоду" };
            refresh.Click += delegate { RefreshWeather(); };
            menu.Items.Add(refresh);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Источник погоды", IsEnabled = false });
            MenuItem met = new MenuItem { Header = "MET Norway", IsCheckable = true };
            MenuItem openMeteo = new MenuItem { Header = "Open-Meteo", IsCheckable = true };
            met.Click += delegate { SelectWeatherProvider("metno"); };
            openMeteo.Click += delegate { SelectWeatherProvider("openmeteo"); };
            menu.Items.Add(met);
            menu.Items.Add(openMeteo);
            menu.Opened += delegate
            {
                string current = WeatherProviderKey(_config.WeatherProvider);
                met.IsChecked = current == "metno";
                openMeteo.IsChecked = current == "openmeteo";
            };
            menu.Items.Add(new Separator());
            MenuItem city = new MenuItem { Header = "Изменить город" };
            city.Click += delegate { ShowSettingsSection("weather"); };
            menu.Items.Add(city);
            return menu;
        }

        private void SelectWeatherProvider(string provider)
        {
            string selected = WeatherProviderKey(provider);
            if (WeatherProviderKey(_config.WeatherProvider) == selected) return;
            AppConfig pending = _config.Copy();
            pending.WeatherProvider = selected;
            if (!SaveConfigChanges(pending)) return;
            if (_weatherTimer != null) _weatherTimer.Interval = WeatherRefreshInterval();
            RefreshWeather();
        }


        private void ResetWeatherSnapshot()
        {
            _lastWeather = null;
            _lastWeatherSavedUtc = DateTime.MinValue;
            _lastWeatherIsCached = false;
            if (_weatherCacheExpiryTimer != null) _weatherCacheExpiryTimer.Stop();
        }

        private void SetWeatherSnapshot(WeatherSnapshot forecast, DateTime savedUtc, bool cached)
        {
            _lastWeather = forecast;
            _lastWeatherSavedUtc = savedUtc;
            _lastWeatherIsCached = cached;
            if (_weatherCacheExpiryTimer == null)
            {
                _weatherCacheExpiryTimer = new DispatcherTimer();
                _weatherCacheExpiryTimer.Tick += delegate
                {
                    _weatherCacheExpiryTimer.Stop();
                    if (_lastWeather == null) return;
                    if (WeatherDiskCache.IsFresh(_lastWeatherSavedUtc, DateTime.UtcNow))
                    {
                        ScheduleWeatherExpiration();
                        return;
                    }
                    ResetWeatherSnapshot();
                    _lastWeatherError = "сохранённые данные устарели";
                    _weather.Text = _weatherPending ? "Погода: обновление…" : "Погода: " + _lastWeatherError;
                    _weather.ToolTip = "Последний полученный прогноз старше 24 часов.";
                    UpdateForecastWindow();
                };
            }
            ScheduleWeatherExpiration();
        }

        private void ScheduleWeatherExpiration()
        {
            _weatherCacheExpiryTimer.Stop();
            TimeSpan remaining = _lastWeatherSavedUtc.Add(WeatherDiskCache.Lifetime) - DateTime.UtcNow;
            _weatherCacheExpiryTimer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(100);
            _weatherCacheExpiryTimer.Start();
        }

        private void ShowWeatherSnapshot()
        {
            if (_lastWeather == null) return;
            string saved = _lastWeather.UpdatedAt.ToString("dd.MM HH:mm");
            _weather.Text = (_lastWeatherIsCached ? "Погода · сохранено " + saved + " · " : "Погода · ") +
                _lastWeather.Summary();
            _weather.ToolTip = (_lastWeatherIsCached
                ? "Сохранённые данные от " + saved + ". " +
                  (String.IsNullOrWhiteSpace(_lastWeatherError)
                    ? "Ожидается обновление прогноза."
                    : "Не удалось обновить: " + _lastWeatherError + ".")
                : "Источник: " + _lastWeather.Source + ".") +
                "\nДанные: " + _lastWeather.Source +
                " (CC BY 4.0). Прогноз обработан AppShelf; ссылки на лицензии — в окне прогноза." +
                "\nНажми для прогноза на 7 дней. Правая кнопка — настройки города.";
        }

        private void SynchronizeWeatherSettings()
        {
            string city = (_config.WeatherCity ?? "").Trim();
            string provider = WeatherProviderKey(_config.WeatherProvider);
            if (String.Equals(city, _weatherSettingsCity, StringComparison.OrdinalIgnoreCase) &&
                provider == _weatherSettingsProvider && _config.ShowWeather == _weatherSettingsVisible) return;
            _weatherSettingsCity = city;
            _weatherSettingsProvider = provider;
            _weatherSettingsVisible = _config.ShowWeather;
            _weatherSettingsRevision++;
            ResetWeatherSnapshot();
            _lastWeatherCity = city;
            _lastWeatherProvider = provider;
            _lastWeatherError = null;
            WeatherSnapshot cached;
            DateTime savedUtc;
            if (_config.ShowWeather && WeatherDiskCache.TryLoad(city, provider, DateTime.UtcNow, out cached, out savedUtc))
            {
                SetWeatherSnapshot(cached, savedUtc, true);
                // Successful MET responses count against the three-hour provider interval across restarts.
                if (provider == "metno" && !_metWeatherRequests.ContainsKey(city))
                    _metWeatherRequests[city] = savedUtc;
                ShowWeatherSnapshot();
            }
            else
            {
                _weather.Text = city.Length == 0 ? "Погода: указать город" : "Погода: обновление…";
                _weather.ToolTip = "Прогноз для новых настроек ещё не получен.";
            }
        }

        private bool WeatherRequestIsCurrent(long revision)
        {
            SynchronizeWeatherSettings();
            return IsLoaded && _config.ShowWeather && revision == _weatherSettingsRevision;
        }

        private async void RefreshWeather()
        {
            SynchronizeWeatherSettings();
            if (_weatherPending || !_config.ShowWeather) { UpdateForecastWindow(); return; }
            string city = _weatherSettingsCity;
            string provider = _weatherSettingsProvider;
            long revision = _weatherSettingsRevision;
            DateTime previousRequest;
            if (provider == "metno" && _metWeatherRequests.TryGetValue(city, out previousRequest) &&
                DateTime.UtcNow - previousRequest < TimeSpan.FromHours(3))
            {
                if (_lastWeather != null) ShowWeatherSnapshot();
                else _weather.Text = "MET Norway: ожидаем интервал обновления";
                _weather.ToolTip = (_lastWeatherIsCached ? "Сохранённые данные. " : "") +
                    "MET Norway: следующий запрос для этого города не раньше " +
                    previousRequest.AddHours(3).ToLocalTime().ToString("dd.MM HH:mm") + ".";
                // A warm restart should poll when the remaining cooldown ends,
                // not another three hours after opening AppShelf.
                if (_weatherTimer != null)
                {
                    TimeSpan remaining = previousRequest.AddHours(3) - DateTime.UtcNow;
                    _weatherTimer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
                }
                UpdateForecastWindow();
                return;
            }
            if (city.Length == 0)
            {
                _weather.Text = "Погода: указать город";
                _weather.ToolTip = "Нажми, чтобы указать город";
                ResetWeatherSnapshot();
                _lastWeatherCity = city;
                _lastWeatherError = null;
                UpdateForecastWindow();
                return;
            }
            _weatherPending = true;
            if (_weatherTimer != null) _weatherTimer.Interval = WeatherRefreshInterval();
            if (provider == "metno") _metWeatherRequests[city] = DateTime.UtcNow;
            if (_lastWeather != null) ShowWeatherSnapshot();
            else _weather.Text = "Погода: обновление…";
            UpdateForecastWindow();
            try
            {
                WeatherSnapshot current = await WeatherClient.GetForecast(city, provider);
                if (WeatherRequestIsCurrent(revision))
                {
                    DateTime savedUtc = DateTime.UtcNow;
                    SetWeatherSnapshot(current, savedUtc, false);
                    _lastWeatherCity = city;
                    _lastWeatherProvider = provider;
                    _lastWeatherError = null;
                    ShowWeatherSnapshot();
                    UpdateForecastWindow();
                    WeatherDiskCache.Save(city, provider, current, savedUtc);
                }
            }
            catch (Exception ex)
            {
                if (WeatherRequestIsCurrent(revision))
                {
                    bool tlsFailure;
                    bool expectedNetworkFailure = IsExpectedWeatherNetworkFailure(ex, out tlsFailure);
                    if (!expectedNetworkFailure)
                        ConfigStore.Log(new InvalidOperationException(
                            "Ошибка погоды для '" + city + "' (" + provider + ")", ex));
                    else if (tlsFailure)
                    {
                        string reason = WeatherErrorText(ex);
                        string key = city + "|" + provider + "|" + reason;
                        if (_loggedWeatherTlsFailures.Add(key))
                            ConfigStore.Log(new InvalidOperationException(
                                "Погода: проблема TLS/HTTPS для '" + city +
                                "' (" + provider + "): " + reason + "."));
                    }
                    _lastWeatherCity = city;
                    _lastWeatherProvider = provider;
                    _lastWeatherError = WeatherErrorText(ex);
                    if (_lastWeather != null && WeatherDiskCache.IsFresh(_lastWeatherSavedUtc, DateTime.UtcNow))
                    {
                        _lastWeatherIsCached = true;
                        ShowWeatherSnapshot();
                    }
                    else
                    {
                        ResetWeatherSnapshot();
                        _weather.Text = "Погода: " + _lastWeatherError;
                        _weather.ToolTip = "Ошибка получения погоды: " + _lastWeatherError +
                            (expectedNetworkFailure
                                ? "\nПовтори обновление позже."
                                : "\nПодробности: " + ConfigStore.LogPath) +
                            "\nНажми для прогноза или открой настройки правой кнопкой.";
                    }
                    UpdateForecastWindow();
                }
            }
            finally
            {
                _weatherPending = false;
                SynchronizeWeatherSettings();
                UpdateForecastWindow();
                // One follow-up for changed settings; a failure for unchanged settings is not retried here.
                if (IsLoaded && _config.ShowWeather && revision != _weatherSettingsRevision) RefreshWeather();
            }
        }

        private AppItem InternalDraggedItem(IDataObject data)
        {
            if (!data.GetDataPresent(TileDragFormat)) return null;
            string path = data.GetData(TileDragFormat) as string;
            if (String.IsNullOrWhiteSpace(path)) return null;
            foreach (AppItem item in _config.Items)
                if (item != null && String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                    return item;
            return null;
        }

        // Only explicitly defined groups can be reordered. "Без группы" is
        // a special destination, not a movable user-defined group.
        private string InternalDraggedGroup(IDataObject data)
        {
            if (!_config.GroupingEnabled || !data.GetDataPresent(GroupDragFormat)) return null;
            string name = data.GetData(GroupDragFormat) as string;
            if (String.IsNullOrWhiteSpace(name)) return null;
            foreach (string group in AllGroups())
                if (String.Equals(group, name, StringComparison.OrdinalIgnoreCase)) return group;
            return null;
        }

        private static bool HasSupportedFiles(IDataObject data)
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
            string[] paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return false;
            foreach (string path in paths)
                if (IsSupported(path)) return true;
            return false;
        }

        private static DependencyObject FindVisualAncestor(DependencyObject child)
        {
            if (child == null) return null;
            try { return VisualTreeHelper.GetParent(child); }
            catch (InvalidOperationException) { return LogicalTreeHelper.GetParent(child); }
        }

        // Resolve both the tile underneath the pointer and the nearest group drop
        // zone. The group's Expander is marked as well, so a collapsed or empty
        // group can receive a tile by dropping it on the heading.
        private bool ResolveFreeGroupZone(object originalSource, out string zone,
            out Border surface)
        {
            zone = null;
            surface = null;
            if (!_config.GroupingEnabled ||
                NormalizeGroupLayoutMode(_config.GroupLayoutMode) != "free") return false;
            DependencyObject current = originalSource as DependencyObject;
            while (current != null)
            {
                FrameworkElement element = current as FrameworkElement;
                FreeGroupDropTarget target = element == null
                    ? null : element.Tag as FreeGroupDropTarget;
                if (target != null)
                {
                    zone = target.Zone;
                    surface = target.Surface;
                    return true;
                }
                if (Object.ReferenceEquals(current, this)) break;
                current = FindVisualAncestor(current);
            }
            return false;
        }

        private bool ResolveDropTarget(object originalSource, out AppItem targetItem,
            out Button targetButton, out string group, out Border zone)
        {
            targetItem = null;
            targetButton = null;
            group = "";
            zone = null;
            DependencyObject current = originalSource as DependencyObject;
            while (current != null)
            {
                FrameworkElement element = current as FrameworkElement;
                if (element != null)
                {
                    if (targetItem == null)
                    {
                        Button tile = element as Button;
                        if (tile != null && tile.Tag is AppItem)
                        {
                            targetButton = tile;
                            targetItem = (AppItem)tile.Tag;
                        }
                    }
                    GroupDropTarget dropTarget = element.Tag as GroupDropTarget;
                    if (dropTarget != null)
                    {
                        group = dropTarget.Group;
                        zone = dropTarget.Zone;
                        return true;
                    }
                }
                if (Object.ReferenceEquals(current, this)) break;
                current = FindVisualAncestor(current);
            }
            // Files can also be dropped on the header; internal tiles need a
            // concrete content/group target so accidental releases do nothing.
            return false;
        }

        private void HighlightDropZone(Border zone)
        {
            if (Object.ReferenceEquals(_highlightedDropZone, zone)) return;
            ClearDropHighlight();
            _highlightedDropZone = zone;
            if (zone != null) zone.Background = HoverBrush;
        }

        private void ClearDropHighlight()
        {
            if (_highlightedDropZone == null) return;
            _highlightedDropZone.Background = Brushes.Transparent;
            _highlightedDropZone = null;
        }

        // Never report Move for files from Explorer: the original desktop
        // shortcut must stay untouched. Link is a safe fallback for sources
        // that don't advertise Copy.
        private static DragDropEffects ExternalFileEffect(DragDropEffects allowed)
        {
            if ((allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
            if ((allowed & DragDropEffects.Link) != 0) return DragDropEffects.Link;
            return DragDropEffects.None;
        }

        private FreeCanvasRect CanvasPosition(string key)
        {
            if (_config.CanvasPositions != null)
                foreach (KeyValuePair<string, FreeCanvasRect> item in _config.CanvasPositions)
                    if (String.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) return item.Value;
            return null;
        }

        private string CanvasDragKey(IDataObject data)
        {
            if (_canvasBoard == null) return null;
            string group = InternalDraggedGroup(data);
            if (group != null) return "app:" + group;
            return _pluginWidgets == null ? null : _pluginWidgets.CanvasDragKey(data);
        }

        private bool CanEditCanvas()
        { return _canSave && (_canvasBoard == null || !_canvasBoard.IsTemporaryLayout) && (_search == null || String.IsNullOrWhiteSpace(_search.Text)); }

        private void ArrangeCanvas(string command)
        {
            if (_canvasBoard == null || !_canSave || (_search != null && !String.IsNullOrWhiteSpace(_search.Text))) return;
            AppConfig pending = _config.Copy();
            pending.CanvasInitialized = true;
            if (command == "reset") pending.CanvasPositions = null;
            else
            {
                int columns = Math.Max(1, (int)Math.Floor(Math.Max(200, _tilesScroller.ActualWidth -
                    _tilesScroller.Padding.Left - _tilesScroller.Padding.Right - SystemParameters.VerticalScrollBarWidth) / _canvasBoard.CellStep));
                Dictionary<string, FreeCanvasRect> arranged = FreeCanvasLayout.Arrange(_canvasBoard.Capture(), command, columns);
                if (arranged == null) return;
                pending.CanvasPositions = arranged;
            }
            if (SaveConfigChanges(pending)) RenderTiles();
        }

        private void MigrateCanvasLayout(AppConfig pending)
        {
            if (pending.GroupLayoutMode != "canvas" || pending.CanvasInitialized || !_config.GroupingEnabled ||
                NormalizeGroupLayoutMode(_config.GroupLayoutMode) == "canvas" ||
                (pending.CanvasPositions != null && pending.CanvasPositions.Count != 0) ||
                (_search != null && !String.IsNullOrWhiteSpace(_search.Text))) return;
            int step = FreeCanvasLayout.NormalizeStep(pending.CanvasGridStep);
            Dictionary<string, FreeCanvasRect> positions = new Dictionary<string, FreeCanvasRect>(StringComparer.OrdinalIgnoreCase);
            List<FreeCanvasRect> occupied = new List<FreeCanvasRect>();
            List<KeyValuePair<string, Border>> cards = new List<KeyValuePair<string, Border>>();
            foreach (GroupLayoutCard group in _groupLayoutCards)
                cards.Add(new KeyValuePair<string, Border>("app:" + group.Group, group.Card));
            if (_pluginWidgets != null) cards.AddRange(_pluginWidgets.CanvasCards());
            foreach (KeyValuePair<string, Border> item in cards)
            {
                Border card = item.Value;
                if (card.ActualWidth <= 0 || card.ActualHeight <= 0 || !card.IsDescendantOf(_tileHost)) continue;
                Point origin;
                try { origin = card.TransformToAncestor(_tileHost).Transform(new Point()); }
                catch (InvalidOperationException) { continue; }
                int width = Math.Max((int)Math.Ceiling(200.0 / step), (int)Math.Ceiling((card.ActualWidth + 8) / step));
                int height = Math.Max(2, (int)Math.Ceiling((card.ActualHeight + 8) / step));
                FreeCanvasRect rect = new FreeCanvasRect(FreeCanvasLayout.Snap(origin.X, step), FreeCanvasLayout.Snap(origin.Y, step), width, height);
                if (!FreeCanvasLayout.CanPlace(rect, occupied))
                    rect = FreeCanvasLayout.FindFirstFree(width, height, Math.Max(width, (int)Math.Ceiling(Math.Max(800, _tileHost.ActualWidth) / step)), occupied);
                if (rect == null) continue;
                positions.Add(item.Key, rect); occupied.Add(rect);
            }
            if (positions.Count != 0) pending.CanvasPositions = positions;
            pending.CanvasInitialized = true;
        }

        private bool SaveCanvasPlacement(string key, FreeCanvasRect candidate)
        {
            if (_canvasBoard == null || !CanEditCanvas() || !FreeCanvasLayout.IsValid(candidate)) return false;
            AppConfig pending = _config.Copy();
            pending.CanvasInitialized = true;
            if (pending.CanvasPositions == null)
                pending.CanvasPositions = new Dictionary<string, FreeCanvasRect>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, FreeCanvasRect> positions = _canvasBoard.Capture();
            // Do not turn temporary collision reflow into permanent geometry.
            // Initialize unsaved cards; change only the explicitly edited card.
            Dictionary<string, FreeCanvasRect> changes = new Dictionary<string, FreeCanvasRect>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, FreeCanvasRect> item in positions)
                if (!pending.CanvasPositions.ContainsKey(item.Key)) changes[item.Key] = item.Value;
            changes[key] = candidate;
            foreach (KeyValuePair<string, FreeCanvasRect> item in changes)
                pending.CanvasPositions[item.Key] = item.Value.Copy();
            if (!SaveConfigChanges(pending)) return false;
            foreach (KeyValuePair<string, FreeCanvasRect> item in changes)
                _canvasBoard.SetPreferred(item.Key, item.Value);
            return true;
        }

        private bool HandleCanvasGroupDrag(DragEventArgs e, bool drop)
        {
            string key = CanvasDragKey(e.Data);
            if (key == null) return false;
            e.Effects = DragDropEffects.None;
            if (!CanEditCanvas()) { e.Handled = true; return true; }
            _canvasBoard.ShowGrid(true);
            FreeCanvasRect candidate = _canvasBoard.MoveCandidate(key, e.GetPosition(_canvasBoard));
            _canvasBoard.ShowPreview(candidate);
            if (candidate != null && (!drop || SaveCanvasPlacement(key, candidate))) e.Effects = DragDropEffects.Move;
            if (drop) _canvasBoard.EndGroupDrag();
            e.Handled = true;
            return true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            if (HandleCanvasGroupDrag(e, false)) return;
            if (_pluginWidgets != null && _pluginWidgets.IsWidgetDrag(e.Data))
            {
                ClearDropHighlight();
                if (_canSave && _pluginWidgets.PreviewWidgetDrop(e)) e.Effects = DragDropEffects.Move;
                e.Handled = true; return;
            }
            AppItem targetItem;
            Button targetButton;
            string group;
            Border zone;
            bool hasDestination = ResolveDropTarget(e.OriginalSource,
                out targetItem, out targetButton, out group, out zone);
            string draggedGroup = InternalDraggedGroup(e.Data);
            string freeZone;
            Border freeSurface;
            bool hasFreeZone = ResolveFreeGroupZone(e.OriginalSource,
                out freeZone, out freeSurface);
            if (_pluginWidgets != null && _pluginWidgets.IsGroupDrag(e.Data))
            {
                if (hasFreeZone && _pluginWidgets.CanDropGroup(e.Data, freeZone)) e.Effects = DragDropEffects.Move;
                HighlightDropZone(e.Effects == DragDropEffects.Move ? freeSurface : null);
                e.Handled = true; return;
            }
            if (_canSave && draggedGroup != null && hasFreeZone)
            {
                bool movingZone = !String.Equals(GroupZoneFor(draggedGroup),
                    freeZone, StringComparison.Ordinal);
                bool reordering = hasDestination && group.Length != 0 &&
                    !String.Equals(draggedGroup, group, StringComparison.OrdinalIgnoreCase);
                if (movingZone || reordering) e.Effects = DragDropEffects.Move;
            }
            else if (_canSave && draggedGroup != null && hasDestination &&
                group.Length != 0 && !String.Equals(draggedGroup, group, StringComparison.OrdinalIgnoreCase))
                e.Effects = DragDropEffects.Move;
            else if (_canSave && InternalDraggedItem(e.Data) != null && hasDestination)
                e.Effects = DragDropEffects.Move;
            else if (_canSave && (HasSupportedFiles(e.Data) || DroppedSiteUrl(e.Data) != null))
                e.Effects = ExternalFileEffect(e.AllowedEffects);

            HighlightDropZone(e.Effects == DragDropEffects.None ? null :
                draggedGroup != null && hasFreeZone ? freeSurface : zone);
            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            if (_canvasBoard != null) _canvasBoard.ShowGrid(false);
            if (_pluginWidgets != null) _pluginWidgets.ClearWidgetDropPreview();
            ClearDropHighlight();
        }

        private void OnDropData(object sender, DragEventArgs e)
        {
            ClearDropHighlight();
            e.Effects = DragDropEffects.None;
            if (!_canSave) { ShowSaveDisabled(); e.Handled = true; return; }
            if (HandleCanvasGroupDrag(e, true)) return;
            if (_pluginWidgets != null && _pluginWidgets.IsWidgetDrag(e.Data))
            {
                if (_pluginWidgets.DropWidget(e)) e.Effects = DragDropEffects.Move;
                e.Handled = true; return;
            }

            AppItem targetItem;
            Button targetButton;
            string group;
            Border zone;
            bool hasDestination = ResolveDropTarget(e.OriginalSource,
                out targetItem, out targetButton, out group, out zone);
            string draggedGroup = InternalDraggedGroup(e.Data);
            AppItem dragged = InternalDraggedItem(e.Data);
            string freeZone;
            Border freeSurface;
            bool hasFreeZone = ResolveFreeGroupZone(e.OriginalSource,
                out freeZone, out freeSurface);
            if (_pluginWidgets != null && _pluginWidgets.IsGroupDrag(e.Data))
            {
                if (hasFreeZone && _pluginWidgets.DropGroup(e.Data, freeZone)) e.Effects = DragDropEffects.Move;
                e.Handled = true; return;
            }
            if (draggedGroup != null && hasFreeZone)
            {
                bool sameZone = String.Equals(GroupZoneFor(draggedGroup),
                    freeZone, StringComparison.Ordinal);
                bool changed = sameZone && hasDestination && group.Length != 0 &&
                    !String.Equals(draggedGroup, group, StringComparison.OrdinalIgnoreCase)
                    ? SwapGroups(draggedGroup, group)
                    : !sameZone && SetGroupZoneOverride(draggedGroup, freeZone);
                if (changed) e.Effects = DragDropEffects.Move;
            }
            else if (draggedGroup != null && hasDestination && group.Length != 0 &&
                !String.Equals(draggedGroup, group, StringComparison.OrdinalIgnoreCase))
            {
                if (SwapGroups(draggedGroup, group)) e.Effects = DragDropEffects.Move;
            }
            else if (dragged != null && hasDestination)
            {
                bool insertAfter = targetButton != null &&
                    e.GetPosition(targetButton).X >= targetButton.ActualWidth / 2;
                if (MoveDroppedItem(dragged, targetItem, group, insertAfter)) e.Effects = DragDropEffects.Move;
            }
            else if (HasSupportedFiles(e.Data) &&
                ExternalFileEffect(e.AllowedEffects) != DragDropEffects.None)
            {
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null)
                {
                    if (AddPaths(paths, hasDestination && _config.GroupingEnabled ? group : ""))
                        e.Effects = ExternalFileEffect(e.AllowedEffects);
                }
            }
            else if (ExternalFileEffect(e.AllowedEffects) != DragDropEffects.None)
            {
                Uri site = DroppedSiteUrl(e.Data);
                if (site != null &&
                    AddSite(site, SiteLinks.SuggestedName(site),
                        hasDestination && _config.GroupingEnabled ? group : ""))
                    e.Effects = ExternalFileEffect(e.AllowedEffects);
            }
            e.Handled = true;
        }

        private bool MoveDroppedItem(AppItem dragged, AppItem targetItem,
            string destinationGroup, bool insertAfter)
        {
            if (Object.ReferenceEquals(dragged, targetItem)) return false;
            int draggedIndex = _config.Items.IndexOf(dragged);
            if (draggedIndex < 0) return false;
            AppConfig pending = _config.Copy();
            AppItem moved = pending.Items[draggedIndex];

            // When grouping is disabled, only reorder: do not erase membership
            // information the user may want to restore later.
            if (_config.GroupingEnabled)
                moved.Group = destinationGroup ?? "";

            pending.Items.RemoveAt(draggedIndex);
            int insertionIndex = -1;
            if (targetItem != null)
            {
                int targetIndex = _config.Items.IndexOf(targetItem);
                if (targetIndex > draggedIndex) targetIndex--;
                if (targetIndex >= 0) insertionIndex = targetIndex + (insertAfter ? 1 : 0);
            }
            if (insertionIndex < 0)
            {
                // Drop on the group heading or in free space: append to group.
                if (_config.GroupingEnabled)
                {
                    for (int i = 0; i < pending.Items.Count; i++)
                        if (String.Equals(GroupOf(pending.Items[i]), destinationGroup,
                            StringComparison.OrdinalIgnoreCase)) insertionIndex = i + 1;
                }
                if (insertionIndex < 0) insertionIndex = pending.Items.Count;
            }
            pending.Items.Insert(insertionIndex, moved);
            if (!SaveConfigChanges(pending)) return false;
            RenderTiles();
            ShowStatusMessage("Перемещено: " + dragged.Name +
                (_config.GroupingEnabled ? " → " + (String.IsNullOrEmpty(destinationGroup)
                    ? "Без группы" : destinationGroup) : " · порядок изменён"));
            return true;
        }

        private static bool IsSupported(string path)
        {
            if (Directory.Exists(path)) return true;
            if (!File.Exists(path)) return false;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".exe" || ext == ".lnk" || ext == ".bat" || ext == ".cmd" ||
                ext == ".url" || ext == ".msc" || ext == ".com";
        }

        // Browsers expose dragged links using different formats. Accept only HTTP(S) URIs;
        // never treat internal tile/group drag identifiers as external websites.
        private static Uri DroppedSiteUrl(IDataObject data)
        {
            string[] formats = new string[]
            {
                "UniformResourceLocatorW", "text/uri-list", "text/x-moz-url",
                DataFormats.UnicodeText, "UniformResourceLocator", DataFormats.Text
            };
            foreach (string format in formats)
            {
                try
                {
                    if (!data.GetDataPresent(format)) continue;
                    object raw = data.GetData(format);
                    string text = raw as string;
                    Stream stream = raw as Stream;
                    if (text == null && stream != null)
                    {
                        if (stream.CanSeek && stream.Length > 16384) continue;
                        long position = stream.CanSeek ? stream.Position : 0;
                        try
                        {
                            // DragOver and Drop may receive the very same browser stream.
                            if (stream.CanSeek) stream.Position = 0;
                            using (MemoryStream copy = new MemoryStream())
                            {
                                byte[] buffer = new byte[1024];
                                int read;
                                bool oversized = false;
                                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    if (copy.Length + read > 16384) { oversized = true; break; }
                                    copy.Write(buffer, 0, read);
                                }
                                if (oversized) continue;
                                byte[] bytes = copy.ToArray();
                                text = format == "UniformResourceLocatorW" || format == "text/x-moz-url"
                                    ? Encoding.Unicode.GetString(bytes)
                                    : Encoding.UTF8.GetString(bytes);
                            }
                        }
                        finally { if (stream.CanSeek) stream.Position = position; }
                    }
                    if (String.IsNullOrWhiteSpace(text) || text.Length > 8192) continue;
                    foreach (string line in text.Split(new char[] { '\r', '\n', '\0' },
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                        Uri uri;
                        // Dragged text must already be an absolute URL, not arbitrary words.
                        if (line.IndexOf("://", StringComparison.Ordinal) < 0) continue;
                        if (SiteLinks.TryNormalize(line, out uri)) return uri;
                    }
                }
                catch (Exception) { } // An unsupported browser format is not a failed drop.
            }
            return null;
        }

        private bool AddSite(Uri url, string label, string destinationGroup)
        {
            if (!_canSave) { ShowSaveDisabled(); return false; }
            string name = (label ?? "").Trim();
            if (name.Length == 0 || name.Length > 120) return false;
            foreach (AppItem existing in _config.Items)
            {
                if (existing == null) continue;
                Uri previous = SiteLinks.ReadShortcutUrl(existing.Path);
                if (previous != null &&
                    String.Equals(previous.AbsoluteUri, url.AbsoluteUri, StringComparison.Ordinal))
                {
                    ShowStatusMessage("Этот сайт уже добавлен: " + name);
                    return false;
                }
            }
            string filename = null;
            try
            {
                filename = SiteLinks.CreateShortcut(url);
                AppConfig pending = _config.Copy();
                pending.Items.Add(new AppItem
                {
                    Path = filename,
                    Name = name,
                    Group = _config.GroupingEnabled ? (destinationGroup ?? "") : "",
                    RunAsAdmin = false
                });
                if (!SaveConfigChanges(pending)) return false;
                RenderTiles();
                ShowStatusMessage("Добавлен сайт: " + name);
                return true;
            }
            catch (Exception ex)
            {
                ShowError("Не удалось добавить сайт.", ex);
                return false;
            }
            finally
            {
                // Never keep an orphan if apps.json could not be committed.
                if (filename != null && !ContainsAppPath(_config.Items, filename))
                {
                    try { File.Delete(filename); }
                    catch (Exception ex) { ConfigStore.Log(ex); }
                }
            }
        }

        private void ShowAddSiteDialog(string initialUrl, string destinationGroup)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            Window dialog = new Window
            {
                Title = "AppShelf — Добавить сайт",
                Owner = this, Width = 490, Height = 275, MinWidth = 390,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = HeaderBrush, Foreground = WhiteBrush,
                FontFamily = new FontFamily("Segoe UI")
            };
            StackPanel form = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            dialog.Content = form;
            form.Children.Add(Text("Адрес сайта (https://...)", 13, MutedBrush));
            TextBox urlBox = new TextBox
            {
                Text = initialUrl ?? "", MaxLength = 4096, Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 9), Padding = new Thickness(6, 4, 6, 4)
            };
            form.Children.Add(urlBox);
            form.Children.Add(Text("Название плитки", 13, MutedBrush));
            TextBox nameBox = new TextBox
            {
                MaxLength = 120, Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(6, 4, 6, 4)
            };
            form.Children.Add(nameBox);
            bool editingName = false;
            bool fillingName = false;
            nameBox.TextChanged += delegate { if (!fillingName) editingName = true; };
            urlBox.TextChanged += delegate
            {
                if (editingName) return;
                Uri parsed;
                fillingName = true;
                nameBox.Text = SiteLinks.TryNormalize(urlBox.Text, out parsed)
                    ? SiteLinks.SuggestedName(parsed) : "";
                fillingName = false;
            };
            TextBlock error = Text("", 11, AccentBrush);
            error.TextWrapping = TextWrapping.Wrap;
            error.Margin = new Thickness(0, 0, 0, 5);
            form.Children.Add(error);
            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            form.Children.Add(actions);
            Button save = ActionButton("Добавить сайт", true);
            save.IsDefault = true;
            save.Click += delegate
            {
                Uri url;
                if (!SiteLinks.TryNormalize(urlBox.Text, out url))
                {
                    error.Text = "Введите корректный адрес HTTP или HTTPS.";
                    return;
                }
                if (String.IsNullOrWhiteSpace(nameBox.Text))
                {
                    error.Text = "Укажите название сайта.";
                    return;
                }
                if (AddSite(url, nameBox.Text, destinationGroup))
                    dialog.DialogResult = true;
                else error.Text = "Сайт уже добавлен или сохранить изменения не удалось.";
            };
            actions.Children.Add(save);
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            actions.Children.Add(cancel);
            dialog.Loaded += delegate { urlBox.Focus(); urlBox.CaretIndex = urlBox.Text.Length; };
            dialog.ShowDialog();
        }

        private void AddFromDialog()
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Добавить приложения в AppShelf", Multiselect = true,
                Filter = "Программы и ярлыки (*.exe;*.lnk;*.bat;*.cmd;*.url;*.msc;*.com)|" +
                    "*.exe;*.lnk;*.bat;*.cmd;*.url;*.msc;*.com|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
        }

        private void AddFolderFromDialog()
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для быстрого доступа из AppShelf";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                    AddPaths(new string[] { dialog.SelectedPath });
            }
        }

        private void ShowStartMenuFolder()
        {
            try
            {
                string folder = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex) { ShowError("Не удалось открыть меню «Пуск».", ex); }
        }

        private void AddPaths(string[] paths)
        {
            AddPaths(paths, "");
        }

        private bool AddPaths(string[] paths, string destinationGroup)
        {
            if (!_canSave) { ShowSaveDisabled(); return false; }
            List<AppItem> additions = new List<AppItem>();
            List<string> createdShortcuts = new List<string>();
            try
            {
                foreach (string raw in paths)
                {
                    string path;
                    try { path = System.IO.Path.GetFullPath(raw); }
                    catch { continue; }
                    if (!IsSupported(path)) continue;
                    if (ContainsAppPath(_config.Items, path) || ContainsAppPath(additions, path)) continue;
                    bool folder = Directory.Exists(path);
                    string label = folder
                        ? System.IO.Path.GetFileName(path.TrimEnd(
                            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                        : System.IO.Path.GetFileNameWithoutExtension(path);
                    if (String.IsNullOrWhiteSpace(label)) label = path;
                    string savedPath = path;
                    bool shortcut = !folder && ConfigStore.IsShortcut(path);
                    if (shortcut)
                    {
                        try
                        {
                            savedPath = ConfigStore.CopyShortcut(path);
                            createdShortcuts.Add(savedPath);
                        }
                        catch (Exception ex) { ShowError("Не удалось сохранить копию ярлыка «" + path + "».", ex); continue; }
                    }
                    additions.Add(new AppItem
                    {
                        Path = savedPath,
                        SourcePath = shortcut ? path : null,
                        Name = label,
                        Kind = folder ? "folder" : null,
                        RunAsAdmin = false,
                        Group = _config.GroupingEnabled ? (destinationGroup ?? "") : ""
                    });
                }
                // Build from the latest live state after any copy-error dialogs have closed.
                AppConfig pending = _config.Copy();
                foreach (AppItem addition in additions)
                    if (!ContainsAppPath(pending.Items, addition.SourcePath ?? addition.Path)) pending.Items.Add(addition);
                if (pending.Items.Count == _config.Items.Count)
                {
                    if (paths.Length != 0) ShowStatusMessage("Новых приложений нет: уже добавлены или не поддерживаются.");
                    return false;
                }
                if (!SaveConfigChanges(pending)) return false;
                RenderTiles();
                return true;
            }
            finally { DeleteUnusedShortcutCopies(createdShortcuts); }
        }

        private static bool SameStoredPath(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second)) return false;
            // Explorer may supply a folder with or without its trailing separator.
            return String.Equals(first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAppPath(IEnumerable<AppItem> items, string path)
        {
            foreach (AppItem item in items)
                if (item != null && (SameStoredPath(item.Path, path) ||
                    SameStoredPath(item.SourcePath, path))) return true;
            return false;
        }

        private void DeleteUnusedShortcutCopies(IEnumerable<string> paths)
        {
            foreach (string path in paths)
                if (!ContainsAppPath(_config.Items, path))
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { ConfigStore.Log(ex); }
                }
        }

        private ImageSource GetIcon(string path)
        {
            Uri site = SiteLinks.ReadShortcutUrl(path);
            if (site != null)
            {
                string key = SiteLinks.IconPath(site);
                ImageSource icon;
                if (_iconCache.TryGetValue(key, out icon) && icon != null) return icon;
                icon = SiteLinks.LoadCachedIcon(site);
                if (icon != null) { _iconCache[key] = icon; return icon; }
                if (_faviconRequested.Add(key))
                {
                    // Network access must never block the UI or delay launching a tile.
                    Task.Run<bool>(delegate { return SiteLinks.FetchFavicon(site); })
                        .ContinueWith(delegate(Task<bool> task)
                        {
                            if (task.IsFaulted || task.IsCanceled || !task.Result) return;
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                if (!IsLoaded) return;
                                ImageSource loaded = SiteLinks.LoadCachedIcon(site);
                                if (loaded != null)
                                {
                                    _iconCache[key] = loaded;
                                    RenderTiles();
                                }
                            }));
                        });
                }
            }
            ImageSource cached;
            if (_iconCache.TryGetValue(path, out cached)) return cached;
            ImageSource result = null;
            try { result = ShellIcon.GetWithoutShortcutOverlay(path); }
            catch (Exception ex) { ConfigStore.Log(ex); }
            _iconCache[path] = result;
            return result;
        }

        private static string FolderIconInitials(string name)
        {
            string title = (name ?? "").Trim();
            string[] words = title.Split(new char[] { ' ', '-', '_', '.' },
                StringSplitOptions.RemoveEmptyEntries);
            StringBuilder initials = new StringBuilder(2);
            if (words.Length > 1)
            {
                foreach (string word in words)
                {
                    foreach (char ch in word)
                        if (Char.IsLetterOrDigit(ch))
                        {
                            initials.Append(ch);
                            break;
                        }
                    if (initials.Length == 2) break;
                }
            }
            if (initials.Length < 2)
            {
                initials.Clear();
                foreach (char ch in title)
                    if (Char.IsLetterOrDigit(ch))
                    {
                        initials.Append(ch);
                        if (initials.Length == 2) break;
                    }
            }
            return initials.Length == 0 ? "П" : initials.ToString().ToUpperInvariant();
        }

        private static FrameworkElement MakeFolderIcon(AppItem item, double size)
        {
            // FNV-1a over the canonical path yields the same color after a restart.
            uint hash = 2166136261u;
            string key = (item.Path ?? "").TrimEnd('\\', '/').ToUpperInvariant();
            unchecked
            {
                foreach (char ch in key)
                {
                    hash ^= ch;
                    hash *= 16777619u;
                }
            }
            Color[] palette = new Color[]
            {
                Color.FromRgb(63, 122, 188), Color.FromRgb(132, 93, 183),
                Color.FromRgb(53, 146, 132), Color.FromRgb(186, 107, 75),
                Color.FromRgb(62, 135, 168), Color.FromRgb(161, 91, 141),
                Color.FromRgb(104, 127, 186), Color.FromRgb(151, 121, 72)
            };
            Color color = palette[(int)(hash % (uint)palette.Length)];
            SolidColorBrush front = new SolidColorBrush(color);
            SolidColorBrush tab = new SolidColorBrush(Color.FromRgb(
                (byte)Math.Min(255, color.R + 24),
                (byte)Math.Min(255, color.G + 24),
                (byte)Math.Min(255, color.B + 24)));
            front.Freeze();
            tab.Freeze();

            // Vector icon; nothing is downloaded or stored outside apps.json.
            Canvas canvas = new Canvas { Width = 64, Height = 64 };
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 7,20 V 17 Q 7,12 12,12 H 25 L 31,18 H 51 Q 57,18 57,24 V 49 Q 57,54 52,54 H 12 Q 7,54 7,49 Z"),
                Fill = tab, Stroke = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)),
                StrokeThickness = 1
            });
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 6,26 Q 6,23 10,23 H 54 Q 58,23 58,27 V 50 Q 58,55 53,55 H 11 Q 6,55 6,50 Z"),
                Fill = front, Stroke = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)),
                StrokeThickness = 1
            });
            TextBlock initials = new TextBlock
            {
                Text = FolderIconInitials(item.Name),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 19,
                FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center, Width = 64
            };
            Canvas.SetLeft(initials, 0);
            Canvas.SetTop(initials, 28);
            canvas.Children.Add(initials);
            return new Viewbox
            {
                Width = size, Height = size, Child = canvas,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        private Button MakeTile(AppItem item)
        {
            string mode = CurrentTileDisplayMode();
            bool compact = mode == "compact";
            bool labeled = mode == "labeled";
            double tileScale = FluentUi.NormalizeTileScale(_config.TileScalePercent) / 100.0;
            Button tile = new Button
            {
                Width = Math.Round((compact ? 78 : (labeled ? 124 : 100)) * tileScale),
                Height = Math.Round((compact ? 78 : (labeled ? 123 : 100)) * tileScale),
                Margin = new Thickness(3, 3, 8, 5), Padding = new Thickness(8),
                Background = TileBrush, BorderBrush = StrokeBrush, BorderThickness = new Thickness(1),
                Foreground = WhiteBrush, Cursor = Cursors.Hand, AllowDrop = true,
                Tag = item,
                ToolTip = item.Name + "\n" + (SiteLinks.ReadShortcutUrl(item.Path) == null
                    ? (item.SourcePath ?? item.Path)
                    : SiteLinks.ReadShortcutUrl(item.Path).AbsoluteUri) +
                    (item.RunAsAdmin ? "\nПо умолчанию: запуск от администратора" : "") +
                    "\nПравая кнопка — действия"
            };
            StackPanel inside = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            double iconSize = Math.Round((compact ? 47 : 51) * tileScale);
            if (String.Equals(item.Kind, "folder", StringComparison.OrdinalIgnoreCase))
            {
                inside.Children.Add(MakeFolderIcon(item, iconSize));
            }
            else
            {
                ImageSource icon = GetIcon(item.Path);
                if (icon == null)
                {
                    TextBlock fallback = Text("▣", 35, AccentBrush);
                    fallback.HorizontalAlignment = HorizontalAlignment.Center;
                    inside.Children.Add(fallback);
                }
                else
                {
                    inside.Children.Add(new Image
                    {
                        Source = icon, Width = iconSize, Height = iconSize,
                        Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center
                    });
                }
            }
            if (labeled)
            {
                TextBlock label = Text(item.Name, 12, WhiteBrush);
                label.Margin = new Thickness(0, 9 * tileScale, 0, 0);
                label.MaxWidth = Math.Round(108 * tileScale);
                label.TextAlignment = TextAlignment.Center;
                label.TextWrapping = TextWrapping.Wrap;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.MaxHeight = 33;
                inside.Children.Add(label);
                if (item.RunAsAdmin)
                {
                    TextBlock uac = Text("◆  администратор", 10, AccentBrush);
                    uac.HorizontalAlignment = HorizontalAlignment.Center;
                    inside.Children.Add(uac);
                }
            }
            tile.Content = inside;
            bool suppressClickAfterDrag = false;
            tile.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                suppressClickAfterDrag = false;
                _dragCandidate = item;
                _dragStart = e.GetPosition(this);
            };
            tile.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!_canSave || _dragInProgress || !Object.ReferenceEquals(_dragCandidate, item) ||
                    e.LeftButton != MouseButtonState.Pressed) return;
                Point current = e.GetPosition(this);
                if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

                _dragCandidate = null;
                _dragInProgress = true;
                suppressClickAfterDrag = true;
                // Buttons capture the mouse for Click; release it before dragging,
                // otherwise letting go could also launch the program.
                if (tile.IsMouseCaptured) tile.ReleaseMouseCapture();
                try
                {
                    DataObject data = new DataObject();
                    data.SetData(TileDragFormat, item.Path);
                    DragDrop.DoDragDrop(tile, data, DragDropEffects.Move);
                }
                catch (Exception ex)
                {
                    ShowError("Не удалось переместить плитку перетаскиванием.", ex);
                }
                finally
                {
                    _dragInProgress = false;
                    ClearDropHighlight();
                }
                e.Handled = true;
            };
            tile.Click += delegate
            {
                if (_dragInProgress || suppressClickAfterDrag) return;
                LaunchItem(item, item.RunAsAdmin);
            };
            tile.ContextMenu = MakeContextMenu(item);
            return tile;
        }

        private static string GroupOf(AppItem item)
        {
            return (item.Group ?? "").Trim();
        }

        private AppItem FirstMatch()
        {
            foreach (AppItem item in _config.Items)
                if (Matches(item)) return item;
            return null;
        }

        private bool Matches(AppItem item)
        {
            if (item == null || String.IsNullOrWhiteSpace(item.Path)) return false;
            string query = _search == null ? "" : (_search.Text ?? "").Trim();
            return query.Length == 0 || (item.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeGroupWidthMode(string value)
        {
            return value == "content" ? "content" : "full";
        }

        private static string NormalizeGroupLayoutMode(string value)
        {
            return value == "grid" || value == "free" || value == "canvas" ? value : "vertical";
        }

        private static readonly string[] FreeGroupZones = {
            "top-left", "top-center", "top-right",
            "middle-left", "middle-center", "middle-right",
            "bottom-left", "bottom-center", "bottom-right"
        };

        private static string NormalizeGroupZone(string value)
        {
            return Array.IndexOf(FreeGroupZones, value) >= 0 ? value : "top-left";
        }

        private static string FreeGroupZoneLabel(string zone)
        {
            switch (zone)
            {
                case "top-center": return "Сверху по центру";
                case "top-right": return "Сверху справа";
                case "middle-left": return "По центру слева";
                case "middle-center": return "По центру";
                case "middle-right": return "По центру справа";
                case "bottom-left": return "Снизу слева";
                case "bottom-center": return "Снизу по центру";
                case "bottom-right": return "Снизу справа";
                default: return "Сверху слева";
            }
        }

        private string GroupZoneFor(string group)
        {
            if (!String.IsNullOrWhiteSpace(group) && _config.GroupZoneOverrides != null)
            {
                foreach (KeyValuePair<string, string> saved in _config.GroupZoneOverrides)
                    if (String.Equals(saved.Key, group, StringComparison.OrdinalIgnoreCase) &&
                        Array.IndexOf(FreeGroupZones, saved.Value) >= 0)
                        return saved.Value;
            }
            return NormalizeGroupZone(_config.GroupDefaultZone);
        }

        private static string NormalizeGroupAlignment(string value)
        {
            return value == "center" || value == "right" || value == "alternate"
                ? value : "left";
        }

        private static string NormalizeGroupOverride(string value)
        {
            return value == "left" || value == "center" || value == "right"
                ? value : null;
        }

        private string GroupAlignmentFor(string group, int position)
        {
            if (_config.GroupAlignmentOverrides != null && !String.IsNullOrWhiteSpace(group))
            {
                string preference;
                if (_config.GroupAlignmentOverrides.TryGetValue(group, out preference))
                {
                    string valid = NormalizeGroupOverride(preference);
                    if (valid != null) return valid;
                }
            }
            string alignment = NormalizeGroupAlignment(_config.GroupAlignment);
            return alignment == "alternate" ? (position % 2 == 0 ? "left" : "right") : alignment;
        }

        // Width is based on tile slots (including their margins), not a fixed
        // percentage of the window. The final column count is limited by the
        // viewport so WrapPanel can flow into additional lines.
        private double NaturalGroupWidth(int itemCount, double available)
        {
            string mode = CurrentTileDisplayMode();
            double scale = FluentUi.NormalizeTileScale(_config.TileScalePercent) / 100.0;
            double tileWidth = Math.Round((mode == "compact" ? 78 :
                mode == "labeled" ? 124 : 100) * scale) + 11;
            const double chrome = 38; // Group border/padding and drop-zone padding.
            int columns = Math.Min(Math.Max(1, itemCount),
                Math.Max(1, (int)Math.Floor(Math.Max(1, available - chrome) / tileWidth)));
            // Empty groups need a larger visible drop target; nonempty cards
            // may hug their last tile with only modest room for the header.
            double minimum = itemCount == 0 ? 190 : 168;
            return Math.Max(1, Math.Min(available,
                Math.Max(minimum, Math.Ceiling(columns * tileWidth + chrome))));
        }

        private void ShowFreeGroupZoneHints(bool visible)
        {
            foreach (TextBlock hint in _freeZoneHints)
                hint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Nine independent drop targets with a common column width and
        // independent vertical tracks. A tall group
        // in one column must not push down groups in the other columns.
        internal sealed class FreeZoneBoard : Grid
        {
            private bool narrow;
            private double viewport;
            private readonly double[] heights = new double[9];

            internal void SetViewport(bool stack, double height)
            {
                if (narrow == stack && Math.Abs(viewport - height) < 0.1) return;
                narrow = stack; viewport = height;
                InvalidateMeasure();
            }

            protected override Size MeasureOverride(Size constraint)
            {
                double width = Double.IsInfinity(constraint.Width) ? 600 : Math.Max(0, constraint.Width);
                double columnWidth = narrow ? width : width / 3;
                double desiredHeight = 0;
                for (int i = 0; i < InternalChildren.Count; i++)
                {
                    UIElement cell = InternalChildren[i];
                    cell.Measure(new Size(columnWidth, Double.PositiveInfinity));
                    heights[i] = Math.Max(i / 3 == 1 || narrow ? 26 : 0, cell.DesiredSize.Height);
                    if (narrow) desiredHeight += heights[i];
                }
                if (!narrow)
                    for (int col = 0; col < 3; col++)
                        desiredHeight = Math.Max(desiredHeight, heights[col] + heights[3 + col] + heights[6 + col]);
                return new Size(width, Math.Max(viewport, desiredHeight));
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                if (narrow)
                {
                    double y = 0;
                    for (int i = 0; i < InternalChildren.Count; i++)
                    {
                        InternalChildren[i].Arrange(new Rect(0, y, finalSize.Width, heights[i]));
                        y += heights[i];
                    }
                }
                else
                {
                    double width = finalSize.Width / 3;
                    for (int col = 0; col < 3; col++)
                    {
                        double top = heights[col], bottom = heights[6 + col];
                        double total = Math.Max(viewport, top + heights[3 + col] + bottom);
                        double middle = Math.Max(0, total - top - bottom);
                        InternalChildren[col].Arrange(new Rect(col * width, 0, width, top));
                        InternalChildren[3 + col].Arrange(new Rect(col * width, top, width, middle));
                        InternalChildren[6 + col].Arrange(new Rect(col * width, top + middle, width, bottom));
                    }
                }
                return finalSize;
            }
        }

        private void UpdateFreeBoardLayout(double available)
        {
            if (_freeLayoutGrid == null) return;
            bool narrow = available < 600;
            double viewportHeight = _tilesScroller.ActualHeight -
                _tilesScroller.Padding.Top - _tilesScroller.Padding.Bottom;
            if (Double.IsNaN(viewportHeight) || viewportHeight < 0) viewportHeight = 0;
            ((FreeZoneBoard)_freeLayoutGrid).SetViewport(narrow, viewportHeight);
            for (int i = 0; i < FreeGroupZones.Length; i++)
            {
                int row = i / 3;
                int col = i % 3;
                Border cell = _freeZoneCells[i];
                StackPanel stack = _freeZoneHosts[FreeGroupZones[i]];
                Grid.SetRow(cell, narrow ? i : row);
                Grid.SetColumn(cell, narrow ? 0 : col);
                Grid.SetColumnSpan(cell, narrow ? 3 : 1);
                cell.MinHeight = narrow && stack.Children.Count == 0 ? 26 : 0;
                stack.HorizontalAlignment = col == 0 ? HorizontalAlignment.Left :
                    col == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right;
                stack.VerticalAlignment = narrow || row == 0 ? VerticalAlignment.Top :
                    row == 1 ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            }
        }

        private void UpdateGroupWidths()
        {
            if (_canvasBoard != null)
            {
                double outer = _tilesScroller.ActualWidth > 1 ? _tilesScroller.ActualWidth : Width;
                double viewport = outer - _tilesScroller.Padding.Left - _tilesScroller.Padding.Right - SystemParameters.VerticalScrollBarWidth;
                _canvasBoard.SetViewport(Math.Max(1, viewport), _config.CanvasViewportMode);
                _canvasBoard.InvalidateMeasure();
                return;
            }
            if (_updatingGroupWidths || _tilesScroller == null ||
                (_groupLayoutCards.Count == 0 && _freeLayoutGrid == null)) return;
            // Reserve scrollbar room even while hidden: otherwise the scrollbar
            // appearing may alter wrapping and oscillate around the threshold.
            double outerWidth = _tilesScroller.ActualWidth;
            if (Double.IsNaN(outerWidth) || outerWidth <= 1)
                outerWidth = !Double.IsNaN(ActualWidth) && ActualWidth > 1
                    ? ActualWidth : Width;
            double available = outerWidth - _tilesScroller.Padding.Left -
                _tilesScroller.Padding.Right - SystemParameters.VerticalScrollBarWidth;
            if (Double.IsNaN(available) || Double.IsInfinity(available) || available <= 1) return;

            string layout = NormalizeGroupLayoutMode(_config.GroupLayoutMode);
            bool grid = layout == "grid";
            bool free = layout == "free";
            bool natural = free || grid || NormalizeGroupWidthMode(_config.GroupWidthMode) == "content";
            try
            {
                _updatingGroupWidths = true;
                if (free) UpdateFreeBoardLayout(available);
                foreach (GroupLayoutCard group in _groupLayoutCards)
                {
                    Border card = group.Card;
                    if (natural)
                    {
                        // Reserve the right gap between cards only in grid mode.
                        double limit = Math.Max(1, free
                            ? (available < 600 ? available - 12 : available / 3 - 16)
                            : available - (grid ? 9 : 0));
                        // Collapsed groups have no visible tiles; keep only a small drop target.
                        Expander expander = card.Child as Expander;
                        int visibleTiles = expander != null && !expander.IsExpanded ? 0 : group.TileCount;
                        double width = NaturalGroupWidth(visibleTiles, limit);
                        if (Double.IsNaN(card.Width) || Math.Abs(card.Width - width) > 0.5)
                            card.Width = width;
                        string align = grid || free ? "left" :
                            GroupAlignmentFor(group.Group, group.Position);
                        card.HorizontalAlignment = align == "right" ? HorizontalAlignment.Right :
                            align == "center" ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                        card.Margin = new Thickness(0, 0, grid ? 9 : 0, 8);
                        group.Heading.MaxWidth = Math.Max(1, width - 84);
                    }
                    else
                    {
                        card.Width = Double.NaN;
                        card.HorizontalAlignment = HorizontalAlignment.Stretch;
                        card.Margin = new Thickness(0, 0, 0, 8);
                        group.Heading.MaxWidth = Double.PositiveInfinity;
                    }
                }
                if (free)
                {
                    if (_pluginWidgets != null) _pluginWidgets.FitBoard(available < 600 ? available - 12 : available / 3 - 16);
                    _freeLayoutGrid.InvalidateMeasure();
                }
            }
            finally { _updatingGroupWidths = false; }
        }

        // Used by the appearance slider. Changing brush values in place preserves
        // the live group expansion state, app tiles, keyboard focus and drag targets.
        private void ApplyGroupAppearancePreview(string preset, int opacity)
        {
            if (_tileHost == null) return;
            Brush groupBrush = FluentUi.MainGroupBackground(preset, opacity);
            Brush borderBrush = FluentUi.MainGroupBorder(preset);
            if (_pluginWidgets != null) _pluginWidgets.ApplyAppearance(groupBrush, borderBrush);
            foreach (GroupLayoutCard group in _groupLayoutCards)
            {
                group.Card.Background = groupBrush;
                group.Card.BorderBrush = borderBrush;
            }
        }

        private void RenderTiles()
        {
            if (_tileHost == null) return;
            if (_pluginWidgets != null) _pluginWidgets.DetachBoard();
            _tileHost.Children.Clear();
            _groupLayoutCards.Clear();
            _freeLayoutGrid = null;
            _canvasBoard = null;
            _tilesScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _freeZoneHosts.Clear();
            _freeZoneCells.Clear();
            _freeZoneHints.Clear();
            int count = 0;
            if (!_config.GroupingEnabled)
            {
                WrapPanel list = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                Border zone = NewDropZone("", list);
                _tileHost.Children.Add(zone);
                foreach (AppItem item in _config.Items)
                {
                    if (!Matches(item)) continue;
                    list.Children.Add(MakeTile(item));
                    count++;
                }
            }
            else
            {
                List<string> groups = AllGroups();
                string layoutMode = NormalizeGroupLayoutMode(_config.GroupLayoutMode);
                bool gridMode = layoutMode == "grid";
                bool freeMode = layoutMode == "free";
                bool canvasMode = layoutMode == "canvas";
                WrapPanel gridHost = null;
                if (canvasMode)
                {
                    _canvasBoard = new FreeCanvasBoard(FreeCanvasLayout.NormalizeStep(_config.CanvasGridStep));
                    _canvasBoard.CommitPlacement = SaveCanvasPlacement;
                    _canvasBoard.CanEdit = CanEditCanvas;
                    _tilesScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                    _tileHost.Children.Add(_canvasBoard);
                }
                if (gridMode)
                {
                    gridHost = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                    _tileHost.Children.Add(gridHost);
                }
                if (freeMode)
                {
                    _freeLayoutGrid = new FreeZoneBoard
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    for (int i = 0; i < 9; i++)
                        _freeLayoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    for (int i = 0; i < 3; i++)
                        _freeLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition
                        { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
                    for (int i = 0; i < FreeGroupZones.Length; i++)
                    {
                        string key = FreeGroupZones[i];
                        Grid content = new Grid();
                        StackPanel stack = new StackPanel { Orientation = Orientation.Vertical };
                        content.Children.Add(stack);
                        TextBlock hint = Text(FreeGroupZoneLabel(key), 11, MutedBrush);
                        hint.HorizontalAlignment = HorizontalAlignment.Center;
                        hint.VerticalAlignment = VerticalAlignment.Center;
                        hint.IsHitTestVisible = false;
                        hint.Visibility = Visibility.Collapsed;
                        content.Children.Add(hint);
                        Border cell = new Border
                        {
                            Background = Brushes.Transparent,
                            Padding = new Thickness(4),
                            AllowDrop = true,
                            Child = content,
                            ToolTip = "Перетащи сюда группу: " + FreeGroupZoneLabel(key)
                        };
                        cell.Tag = new FreeGroupDropTarget(key, cell);
                        Grid.SetRow(cell, i / 3);
                        Grid.SetColumn(cell, i % 3);
                        _freeLayoutGrid.Children.Add(cell);
                        _freeZoneCells.Add(cell);
                        _freeZoneHints.Add(hint);
                        _freeZoneHosts.Add(key, stack);
                    }
                    _tileHost.Children.Add(_freeLayoutGrid);
                }
                // The special ungrouped section is only useful when it contains
                // visible applications. Keep it last, after the user-defined groups.
                bool hasUngrouped = false;
                foreach (AppItem item in _config.Items)
                    if (Matches(item) && GroupOf(item).Length == 0)
                    {
                        hasUngrouped = true;
                        break;
                    }
                if (hasUngrouped) groups.Add("");
                foreach (string group in groups)
                {
                    WrapPanel list = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                    int inGroup = 0;
                    foreach (AppItem item in _config.Items)
                    {
                        if (!Matches(item) || !String.Equals(GroupOf(item), group, StringComparison.OrdinalIgnoreCase))
                            continue;
                        list.Children.Add(MakeTile(item));
                        inGroup++;
                        count++;
                    }
                    // User-defined groups remain visible even when empty so they
                    // can receive dragged tiles. "Без группы" is hidden if empty.
                    Border zone = NewDropZone(group, list);
                    Expander section = new Expander
                    {
                        IsExpanded = true, Foreground = WhiteBrush, Margin = new Thickness(0)
                    };
                    TextBlock heading = Text((group.Length == 0 ? "Без группы" : group) +
                        "  ·  " + inGroup, 15, WhiteBrush);
                    heading.FontWeight = FontWeights.SemiBold;
                    heading.Margin = new Thickness(5, 0, 0, 4);
                    heading.TextTrimming = TextTrimming.None;
                    heading.TextWrapping = TextWrapping.Wrap;
                    heading.ToolTip = heading.Text;
                    DockPanel headerPanel = new DockPanel { LastChildFill = true };
                    if (group.Length != 0)
                    {
                        string selectedGroup = group;
                        List<string> orderedGroups = AllGroups();
                        int position = orderedGroups.FindIndex(delegate(string name)
                        { return String.Equals(name, selectedGroup, StringComparison.OrdinalIgnoreCase); });
                        ContextMenu groupMenu = new ContextMenu();
                        MenuItem up = MenuAction("Переместить выше", delegate { MoveGroup(selectedGroup, -1); });
                        up.IsEnabled = _canSave && position > 0;
                        MenuItem down = MenuAction("Переместить ниже", delegate { MoveGroup(selectedGroup, 1); });
                        down.IsEnabled = _canSave && position >= 0 && position < orderedGroups.Count - 1;
                        groupMenu.Items.Add(up);
                        groupMenu.Items.Add(down);
                        groupMenu.Items.Add(new Separator());
                        groupMenu.Items.Add(MenuAction("Переименовать группу", delegate { RenameGroup(selectedGroup); }));
                        groupMenu.Items.Add(MenuAction("Удалить группу", delegate { DeleteGroup(selectedGroup); }));
                        // Individual overrides are meaningful only for vertically
                        // stacked groups sized to their tiles, never in a compact grid.
                        if (NormalizeGroupLayoutMode(_config.GroupLayoutMode) == "vertical" &&
                            NormalizeGroupWidthMode(_config.GroupWidthMode) == "content")
                        {
                            groupMenu.Items.Add(new Separator());
                            MenuItem alignMenu = new MenuItem { Header = "Выравнивание группы" };
                            string current = null;
                            if (_config.GroupAlignmentOverrides != null)
                                _config.GroupAlignmentOverrides.TryGetValue(selectedGroup, out current);
                            foreach (string option in new[] { "inherit", "left", "center", "right" })
                            {
                                string selected = option;
                                string label = option == "inherit" ? "По общему правилу" :
                                    option == "left" ? "Слева" :
                                    option == "center" ? "По центру" : "Справа";
                                MenuItem choice = MenuAction(label, delegate
                                { SetGroupAlignmentOverride(selectedGroup, selected); });
                                choice.IsCheckable = true;
                                choice.IsChecked = option == "inherit"
                                    ? NormalizeGroupOverride(current) == null
                                    : current == option;
                                choice.IsEnabled = _canSave;
                                choice.Tag = selected;
                                alignMenu.Items.Add(choice);
                            }
                            // Menu is retained when alignment changes in place.
                            // Refresh checkmarks every time the submenu opens.
                            alignMenu.SubmenuOpened += delegate
                            {
                                string selectedPreference = null;
                                if (_config.GroupAlignmentOverrides != null)
                                    _config.GroupAlignmentOverrides.TryGetValue(selectedGroup,
                                        out selectedPreference);
                                foreach (MenuItem item in alignMenu.Items)
                                {
                                    string value = item.Tag as string;
                                    item.IsChecked = value == "inherit"
                                        ? NormalizeGroupOverride(selectedPreference) == null
                                        : selectedPreference == value;
                                }
                            };
                            groupMenu.Items.Add(alignMenu);
                        }
                        if (freeMode)
                        {
                            groupMenu.Items.Add(new Separator());
                            MenuItem zoneMenu = new MenuItem { Header = "Размещение группы" };
                            foreach (string zoneOption in new[] { "inherit",
                                "top-left", "top-center", "top-right",
                                "middle-left", "middle-center", "middle-right",
                                "bottom-left", "bottom-center", "bottom-right" })
                            {
                                string chosenZone = zoneOption;
                                string caption = chosenZone == "inherit"
                                    ? "По общему правилу" : FreeGroupZoneLabel(chosenZone);
                                MenuItem choice = MenuAction(caption, delegate
                                { SetGroupZoneOverride(selectedGroup, chosenZone); });
                                choice.IsCheckable = true;
                                choice.IsEnabled = _canSave;
                                choice.Tag = chosenZone;
                                zoneMenu.Items.Add(choice);
                            }
                            zoneMenu.SubmenuOpened += delegate
                            {
                                string preference = null;
                                if (_config.GroupZoneOverrides != null)
                                    _config.GroupZoneOverrides.TryGetValue(selectedGroup, out preference);
                                foreach (MenuItem choice in zoneMenu.Items)
                                {
                                    string selected = choice.Tag as string;
                                    choice.IsChecked = selected == "inherit"
                                        ? preference == null : preference == selected;
                                }
                            };
                            groupMenu.Items.Add(zoneMenu);
                        }
                        headerPanel.ContextMenu = groupMenu;
                        heading.ToolTip = heading.Text + "\n" + (freeMode
                            ? "Правая кнопка — размещение группы и другие действия"
                            : "Правая кнопка — порядок, переименование, удаление");

                        // Drag by the dedicated handle, never by an application tile.
                        // A normal click on the heading still expands/collapses.
                        TextBlock grip = Text("⋮⋮", 19, MutedBrush);
                        grip.Margin = new Thickness(12, 0, 5, 4);
                        grip.Cursor = Cursors.SizeAll;
                        grip.ToolTip = canvasMode ? "Перетащи группу по свободному полотну" : freeMode
                            ? "Перетащи в одну из девяти зон или на группу в той же зоне"
                            : "Перетащи на заголовок другой группы, чтобы поменять группы местами";
                        DockPanel.SetDock(grip, Dock.Right);
                        headerPanel.Children.Add(grip);
                        Point canvasDragStart = new Point();
                        grip.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                        {
                            if (!_canSave || _groupDragInProgress || (canvasMode && !CanEditCanvas())) return;
                            _groupDragStart = e.GetPosition(this);
                            if (_canvasBoard != null) canvasDragStart = e.GetPosition(_canvasBoard);
                            _groupDragCandidate = grip.CaptureMouse() ? selectedGroup : null;
                            e.Handled = true;
                        };
                        grip.PreviewMouseLeftButtonUp += delegate
                        {
                            _groupDragCandidate = null;
                            if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
                        };
                        grip.LostMouseCapture += delegate
                        {
                            if (String.Equals(_groupDragCandidate, selectedGroup, StringComparison.OrdinalIgnoreCase))
                                _groupDragCandidate = null;
                        };
                        grip.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
                        {
                            if (!_canSave || _groupDragInProgress ||
                                !String.Equals(_groupDragCandidate, selectedGroup,
                                    StringComparison.OrdinalIgnoreCase) ||
                                e.LeftButton != MouseButtonState.Pressed) return;
                            Point current = e.GetPosition(this);
                            if (Math.Abs(current.X - _groupDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                                Math.Abs(current.Y - _groupDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                            _groupDragCandidate = null;
                            _groupDragInProgress = true;
                            if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
                            try
                            {
                                DataObject data = new DataObject();
                                data.SetData(GroupDragFormat, selectedGroup);
                                if (freeMode) ShowFreeGroupZoneHints(true);
                                if (_canvasBoard != null) _canvasBoard.BeginGroupDrag("app:" + selectedGroup, canvasDragStart);
                                DragDrop.DoDragDrop(grip, data, DragDropEffects.Move);
                            }
                            catch (Exception ex)
                            {
                                ShowError("Не удалось переместить группу.", ex);
                            }
                            finally
                            {
                                _groupDragInProgress = false;
                                ShowFreeGroupZoneHints(false);
                                if (_canvasBoard != null) _canvasBoard.EndGroupDrag();
                                ClearDropHighlight();
                            }
                            e.Handled = true;
                        };
                    }
                    headerPanel.Children.Add(heading);
                    section.Header = headerPanel;
                    section.Content = zone;
                    section.Expanded += delegate { UpdateGroupWidths(); };
                    section.Collapsed += delegate { UpdateGroupWidths(); };
                    section.Tag = new GroupDropTarget(group, zone);
                    section.AllowDrop = true;
                    Border groupCard = new Border
                    {
                        Child = section, Tag = "AppShelfMainGroupCard",
                        Background = FluentUi.MainGroupBackground(_config.MainBackgroundPreset,
                            FluentUi.NormalizeGroupOpacity(_config.GroupOpacity)),
                        BorderBrush = FluentUi.MainGroupBorder(_config.MainBackgroundPreset),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(13),
                        Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 8)
                    };
                    _groupLayoutCards.Add(new GroupLayoutCard(groupCard, group, inGroup,
                        _groupLayoutCards.Count, heading));
                    if (canvasMode)
                    {
                        string key = "app:" + group;
                        System.Windows.Automation.AutomationProperties.SetName(groupCard, group.Length == 0 ? "Без группы" : group);
                        int columns = (int)Math.Ceiling(NaturalGroupWidth(inGroup, 400) / _canvasBoard.CellStep);
                        _canvasBoard.AddCard(key, groupCard, columns, CanvasPosition(key));
                    }
                    else if (gridHost != null) gridHost.Children.Add(groupCard);
                    else if (freeMode) _freeZoneHosts[GroupZoneFor(group)].Children.Add(groupCard);
                    else _tileHost.Children.Add(groupCard);
                }
            }
            if (WidgetsInFreeLayout() && _pluginWidgets != null)
            {
                if (_canvasBoard != null)
                    foreach (KeyValuePair<string, Border> card in _pluginWidgets.AttachCanvas())
                        _canvasBoard.AddCard(card.Key, card.Value, (int)Math.Ceiling(400.0 / _canvasBoard.CellStep), CanvasPosition(card.Key));
                else _pluginWidgets.AttachBoard(_freeZoneHosts, NormalizeGroupZone(_config.GroupDefaultZone));
            }
            UpdateGroupWidths();
            if (_pluginWidgets != null) _pluginWidgets.ApplyAppearance(
                FluentUi.MainGroupBackground(_config.MainBackgroundPreset, FluentUi.NormalizeGroupOpacity(_config.GroupOpacity)),
                FluentUi.MainGroupBorder(_config.MainBackgroundPreset));
            _empty.Text = _config.Items.Count == 0
                ? "Пока здесь пусто.\n\nПеретащи EXE или ярлык из Проводника\nили нажми «+ Добавить»."
                : "Ничего не найдено.";
            _empty.Visibility = count == 0 && (!WidgetsInFreeLayout() || _pluginWidgets == null || !_pluginWidgets.HasWidgets)
                ? Visibility.Visible : Visibility.Collapsed;
            ShowDefaultStatus(count);
        }

        private Border NewDropZone(string group, WrapPanel list)
        {
            Border zone = new Border
            {
                AllowDrop = true, Background = Brushes.Transparent,
                MinHeight = 58, BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1), Padding = new Thickness(3),
                Child = list
            };
            zone.Tag = new GroupDropTarget(group, zone);
            list.AllowDrop = true;
            return zone;
        }

        private List<string> AllGroups()
        {
            List<string> groups = new List<string>();
            if (_config.Groups != null)
                foreach (string name in _config.Groups)
                    if (!String.IsNullOrWhiteSpace(name) && !ContainsGroup(groups, name)) groups.Add(name);
            foreach (AppItem item in _config.Items)
            {
                if (item == null) continue;
                string group = GroupOf(item);
                if (group.Length != 0 && !ContainsGroup(groups, group)) groups.Add(group);
            }
            return groups;
        }

        private static bool ContainsGroup(List<string> groups, string name)
        {
            foreach (string group in groups)
                if (String.Equals(group, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private MenuItem MenuAction(string label, Action action)
        {
            MenuItem item = new MenuItem { Header = label };
            item.Click += delegate { action(); };
            return item;
        }

        private ContextMenu MakeContextMenu(AppItem item)
        {
            ContextMenu menu = new ContextMenu();
            bool folder = String.Equals(item.Kind, "folder", StringComparison.OrdinalIgnoreCase);
            menu.Items.Add(MenuAction(folder ? "Открыть папку" : "Запустить",
                delegate { LaunchItem(item, false); }));
            Uri website = folder ? null : SiteLinks.ReadShortcutUrl(item.Path);
            if (folder)
            {
                menu.Items.Add(MenuAction("Копировать путь", delegate
                {
                    try { Clipboard.SetText(item.Path); }
                    catch (Exception ex) { ShowError("Не удалось скопировать путь.", ex); }
                }));
            }
            else if (website == null)
            {
                menu.Items.Add(MenuAction("Запустить от администратора", delegate { LaunchItem(item, true); }));
                MenuItem admin = new MenuItem
                {
                    Header = "Всегда запускать от администратора", IsCheckable = true,
                    IsChecked = item.RunAsAdmin, IsEnabled = _canSave
                };
                admin.Click += delegate
                {
                    int index = _config.Items.IndexOf(item);
                    if (index < 0) return;
                    AppConfig pending = _config.Copy();
                    pending.Items[index].RunAsAdmin = admin.IsChecked;
                    if (SaveConfigChanges(pending)) RenderTiles();
                    admin.IsChecked = item.RunAsAdmin;
                };
                menu.Items.Add(admin);
            }
            else
            {
                Uri address = website;
                menu.Items.Add(MenuAction("Посмотреть адрес…", delegate { ShowSiteAddress(address); }));
                menu.Items.Add(MenuAction("Копировать адрес", delegate
                {
                    try { Clipboard.SetText(address.AbsoluteUri); }
                    catch (Exception ex) { ShowError("Не удалось скопировать адрес.", ex); }
                }));
                MenuItem editSite = MenuAction("Изменить адрес…", delegate { EditSiteAddress(item); });
                editSite.IsEnabled = _canSave;
                menu.Items.Add(editSite);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Переименовать", delegate { RenameItem(item); }));
            MenuItem groupsMenu = new MenuItem { Header = "Переместить в группу", IsEnabled = _canSave };
            MenuItem ungrouped = MenuAction("Без группы", delegate { SetItemGroup(item, ""); });
            ungrouped.IsCheckable = true;
            ungrouped.IsChecked = GroupOf(item).Length == 0;
            groupsMenu.Items.Add(ungrouped);
            foreach (string name in AllGroups())
            {
                string group = name;
                MenuItem toGroup = MenuAction(group, delegate { SetItemGroup(item, group); });
                toGroup.IsCheckable = true;
                toGroup.IsChecked = String.Equals(GroupOf(item), group, StringComparison.OrdinalIgnoreCase);
                groupsMenu.Items.Add(toGroup);
            }
            groupsMenu.Items.Add(new Separator());
            groupsMenu.Items.Add(MenuAction("+ Создать группу…", delegate
            {
                CreateGroup(item);
            }));
            menu.Items.Add(groupsMenu);
            MenuItem left = MenuAction("Переместить левее", delegate { MoveItem(item, -1); });
            MenuItem right = MenuAction("Переместить правее", delegate { MoveItem(item, 1); });
            left.IsEnabled = _canSave;
            right.IsEnabled = _canSave;
            menu.Items.Add(left);
            menu.Items.Add(right);
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Убрать из AppShelf", delegate { RemoveItem(item); }));
            return menu;
        }

        private void ShowSiteAddress(Uri uri)
        {
            // The URL is selectable in a read-only field, including long addresses.
            Window dialog = new Window
            {
                Title = "AppShelf — Адрес сайта", Owner = this,
                Width = 530, Height = 168, MinWidth = 390,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = HeaderBrush, Foreground = WhiteBrush,
                FontFamily = new FontFamily("Segoe UI")
            };
            StackPanel layout = new StackPanel { Margin = new Thickness(16) };
            dialog.Content = layout;
            layout.Children.Add(Text("Адрес сайта", 13, MutedBrush));
            TextBox link = new TextBox
            {
                Text = uri.AbsoluteUri, IsReadOnly = true,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 8),
                Height = 30, Padding = new Thickness(6, 3, 6, 3)
            };
            layout.Children.Add(link);
            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            layout.Children.Add(buttons);
            Button copy = ActionButton("Копировать", true);
            copy.Click += delegate
            {
                try { Clipboard.SetText(uri.AbsoluteUri); }
                catch (Exception ex) { ShowError("Не удалось скопировать адрес.", ex); }
            };
            buttons.Children.Add(copy);
            Button close = ActionButton("Закрыть", false);
            close.IsCancel = true;
            close.Click += delegate { dialog.Close(); };
            buttons.Children.Add(close);
            dialog.Loaded += delegate { link.Focus(); link.SelectAll(); };
            dialog.ShowDialog();
        }

        private void EditSiteAddress(AppItem item)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            Uri previous = SiteLinks.ReadShortcutUrl(item.Path);
            if (previous == null) return;
            string proposed = InputDialog("Изменить адрес сайта", "Новый адрес сайта:", previous.AbsoluteUri);
            if (proposed == null) return;
            Uri uri;
            if (!SiteLinks.TryNormalize(proposed, out uri))
            {
                MessageBox.Show(this, "Введите корректный HTTP(S)-адрес.",
                    "AppShelf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (String.Equals(uri.AbsoluteUri, previous.AbsoluteUri, StringComparison.Ordinal)) return;
            foreach (AppItem existing in _config.Items)
            {
                if (Object.ReferenceEquals(existing, item)) continue;
                Uri duplicate = SiteLinks.ReadShortcutUrl(existing.Path);
                if (duplicate != null &&
                    String.Equals(duplicate.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal))
                {
                    MessageBox.Show(this, "Этот сайт уже добавлен.",
                        "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            int index = _config.Items.IndexOf(item);
            if (index < 0) return;
            string created = null;
            try
            {
                created = SiteLinks.CreateShortcut(uri);
                AppConfig pending = _config.Copy();
                pending.Items[index].Path = created;
                pending.Items[index].SourcePath = null;
                pending.Items[index].RunAsAdmin = false;
                if (!SaveConfigChanges(pending)) return;
                RenderTiles();
                ShowStatusMessage("Адрес сайта изменён: " + uri.AbsoluteUri);
            }
            catch (Exception ex) { ShowError("Не удалось изменить адрес сайта.", ex); }
            finally
            {
                if (created != null && !ContainsAppPath(_config.Items, created))
                {
                    try { File.Delete(created); }
                    catch (Exception ex) { ConfigStore.Log(ex); }
                }
            }
        }

        private void LaunchItem(AppItem item, bool admin)
        {
            try
            {
                Launcher.Start(item, admin);
                ShowStatusMessage(String.Equals(item.Kind, "folder", StringComparison.OrdinalIgnoreCase)
                    ? "Открыта папка: " + item.Name
                    : "Запущено: " + item.Name + (admin ? " (запрошены права администратора)" : ""));
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) ShowStatusMessage("Запуск от администратора отменён.");
                else ShowError("Не удалось запустить «" + item.Name + "».", ex);
            }
            catch (Exception ex) { ShowError("Не удалось запустить «" + item.Name + "».", ex); }
        }

        private void SetBottomPanelMode(string mode)
        {
            if (mode != "compact" && mode != "categories" && mode != "detailed") return;
            if (!_canSave) { ShowSaveDisabled(); return; }
            if (NormalizeBottomPanelMode(_config.BottomPanelMode) == mode) return;
            AppConfig pending = _config.Copy();
            pending.BottomPanelMode = mode;
            if (SaveConfigChanges(pending)) ApplyBottomPanelMode();
        }

        private void SetAppSounds(bool enabled)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            if ((_config.SoundsEnabled != false) == enabled) return;
            AppConfig pending = _config.Copy();
            pending.SoundsEnabled = enabled;
            SaveConfigChanges(pending);
        }

        private void SetRdpKeepAwake(bool enabled)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            if (enabled == _config.RdpKeepAwake) return;
            if (enabled && MessageBox.Show(this,
                "AppShelf будет отправлять F15 при бездействии только внутри RDP-сеанса. " +
                "Это не отменяет серверные политики. Включить?",
                "AppShelf — антипростой RDP", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppConfig pending = _config.Copy();
            pending.RdpKeepAwake = enabled;
            if (SaveConfigChanges(pending)) UpdateRdpStatus();
        }

        private void SetGrouping(bool enabled)
        {
            if (!_canSave) { ShowSaveDisabled(); _groupMenuItem.IsChecked = _config.GroupingEnabled; return; }
            AppConfig pending = _config.Copy();
            pending.GroupingEnabled = enabled;
            bool saved = SaveConfigChanges(pending);
            _groupMenuItem.IsChecked = _config.GroupingEnabled;
            _newGroupMenuItem.IsEnabled = _config.GroupingEnabled;
            if (saved) RenderTiles();
        }

        private void SetGroupLayoutMode(string mode)
        {
            if (mode != "vertical" && mode != "grid" && mode != "free" && mode != "canvas") return;
            if (!_canSave) { ShowSaveDisabled(); return; }
            if (!_config.GroupingEnabled || NormalizeGroupLayoutMode(_config.GroupLayoutMode) == mode)
                return;
            AppConfig pending = _config.Copy();
            pending.GroupLayoutMode = mode;
            MigrateCanvasLayout(pending);
            if (SaveConfigChanges(pending)) RenderTiles();
        }

        private string CurrentTileDisplayMode()
        {
            string mode = _config.TileDisplayMode;
            return mode == "compact" || mode == "normal" || mode == "labeled"
                ? mode : (_config.Compact ? "compact" : "labeled");
        }

        private void UpdateTileModeMenu()
        {
            string mode = CurrentTileDisplayMode();
            _tileModeCompactMenuItem.IsChecked = mode == "compact";
            _tileModeNormalMenuItem.IsChecked = mode == "normal";
            _tileModeLabeledMenuItem.IsChecked = mode == "labeled";
        }

        private void SetTileDisplayMode(string mode)
        {
            if (mode != "compact" && mode != "normal" && mode != "labeled") return;
            if (!_canSave) { ShowSaveDisabled(); UpdateTileModeMenu(); return; }
            if (CurrentTileDisplayMode() == mode) { UpdateTileModeMenu(); return; }
            AppConfig pending = _config.Copy();
            pending.TileDisplayMode = mode;
            // Old AppShelf builds still depend on the compact Boolean.
            pending.Compact = mode != "labeled";
            bool saved = SaveConfigChanges(pending);
            UpdateTileModeMenu();
            if (saved) RenderTiles();
        }

        // A release note belongs to the compiled build, not to whichever
        // CHANGELOG.md happens to be installed alongside the executable.
        private static string CompiledReleaseNotes(string versionText)
        {
            string notesVersion = null;
            string notes = null;
            object[] metadata = typeof(MainWindow).Assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyMetadataAttribute), false);
            foreach (object attribute in metadata)
            {
                System.Reflection.AssemblyMetadataAttribute value =
                    attribute as System.Reflection.AssemblyMetadataAttribute;
                if (value == null) continue;
                if (value.Key == "AppShelf.ReleaseNotesVersion") notesVersion = value.Value;
                else if (value.Key == "AppShelf.ReleaseNotes") notes = value.Value;
            }
            // Fail closed when release preparation updates the number but not the notes.
            return String.Equals(notesVersion, versionText, StringComparison.Ordinal) &&
                !String.IsNullOrWhiteSpace(notes) ? notes : null;
        }

        private void ShowAboutWindow()
        {
            string versionText = AppUpdate.InstalledVersionText();
            Window dialog = new Window
            {
                Title = "AppShelf — О программе", Owner = this,
                Width = 460, Height = 390, MinWidth = 380, MinHeight = 245,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = BackgroundBrush, Foreground = WhiteBrush,
                FontFamily = new FontFamily("Segoe UI")
            };
            FluentUi.PrepareWindow(dialog);
            DockPanel root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
            dialog.Content = root;

            // Buttons remain available even at the smallest supported window size.
            WrapPanel actions = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 9, 0, 0)
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            root.Children.Add(actions);
            Button license = ActionButton("Лицензия", false);
            license.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(
                    "https://github.com/mishakov1c/app_shelf/blob/main/LICENSE")
                    { UseShellExecute = true }); }
                catch (Exception ex) { ShowError("Не удалось открыть лицензию.", ex); }
            };
            actions.Children.Add(license);
            Button repository = ActionButton("GitHub", false);
            repository.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://github.com/mishakov1c/app_shelf")
                    { UseShellExecute = true }); }
                catch (Exception ex) { ShowError("Не удалось открыть репозиторий.", ex); }
            };
            actions.Children.Add(repository);
            Button updates = ActionButton("Проверить обновления", false);
            updates.Click += delegate { ShowUpdateWindow(dialog); };
            actions.Children.Add(updates);
            Button close = ActionButton("Закрыть", true);
            close.Click += delegate { dialog.Close(); };
            actions.Children.Add(close);

            // The header and notes scroll together; the footer stays pinned.
            // Otherwise a short dialog could leave the notes with zero height.
            ScrollViewer notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            // Reuse the scoped dark scrollbar from Settings without changing other windows.
            FluentUi.StyleSettingsScroll(notesScroll);
            root.Children.Add(notesScroll);
            StackPanel scrollContent = new StackPanel();
            notesScroll.Content = scrollContent;
            StackPanel header = new StackPanel();
            scrollContent.Children.Add(header);
            TextBlock title = Text("AppShelf", 23, WhiteBrush);
            title.FontWeight = FontWeights.Bold;
            header.Children.Add(title);
            TextBlock number = Text("Версия " + versionText, 14, AccentBrush);
            number.Margin = new Thickness(0, 8, 0, 10);
            header.Children.Add(number);
            TextBlock description = Text(
                "Панель быстрого запуска приложений с напоминаниями, таймерами и инструментами Windows.",
                12, MutedBrush);
            description.TextWrapping = TextWrapping.Wrap;
            header.Children.Add(description);
            TextBlock author = Text("Автор: mishakov1c", 12, MutedBrush);
            author.Margin = new Thickness(0, 10, 0, 8);
            header.Children.Add(author);
            TextBlock licenseNotice = Text(
                "© 2026 mishakov1c · GNU GPL v3 (GPL-3.0-only).\n" +
                "Программа предоставляется без каких-либо гарантий. Полный текст — «Лицензия».",
                11, MutedBrush);
            licenseNotice.TextWrapping = TextWrapping.Wrap;
            licenseNotice.Margin = new Thickness(0, 0, 0, 12);
            header.Children.Add(licenseNotice);

            StackPanel notesContent = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            scrollContent.Children.Add(notesContent);
            TextBlock whatsNew = Text("Что нового в версии " + versionText, 14, WhiteBrush);
            whatsNew.FontWeight = FontWeights.SemiBold;
            whatsNew.Margin = new Thickness(0, 0, 0, 8);
            notesContent.Children.Add(whatsNew);
            string notes = CompiledReleaseNotes(versionText);
            if (notes == null)
            {
                TextBlock empty = Text(
                    "Список изменений для этой сборки не указан. Полная история версий — на GitHub.",
                    12, MutedBrush);
                empty.TextWrapping = TextWrapping.Wrap;
                notesContent.Children.Add(empty);
            }
            else
            {
                foreach (string note in notes.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    TextBlock bullet = Text("•  " + note.Trim(), 12, WhiteBrush);
                    bullet.TextWrapping = TextWrapping.Wrap;
                    bullet.Margin = new Thickness(0, 0, 0, 7);
                    notesContent.Children.Add(bullet);
                }
            }
            dialog.ShowDialog();
        }

        private string InputDialog(string title, string caption, string initial)
        {
            Window dialog = new Window
            {
                Title = title, Owner = this, Width = 440, Height = 178,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = HeaderBrush, Foreground = WhiteBrush, FontFamily = new FontFamily("Segoe UI")
            };
            StackPanel layout = new StackPanel { Margin = new Thickness(16) };
            dialog.Content = layout;
            layout.Children.Add(Text(caption, 13, WhiteBrush));
            TextBox edit = new TextBox
            {
                Text = initial ?? "", Height = 29,
                Margin = new Thickness(0, 9, 0, 10), Padding = new Thickness(5, 3, 5, 3)
            };
            layout.Children.Add(edit);
            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
            };
            layout.Children.Add(row);
            Button ok = ActionButton("Сохранить", true);
            ok.IsDefault = true;
            ok.Click += delegate
            {
                if (!String.IsNullOrWhiteSpace(edit.Text)) dialog.DialogResult = true;
            };
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            dialog.Loaded += delegate { edit.Focus(); edit.SelectAll(); };
            return dialog.ShowDialog() == true ? edit.Text.Trim() : null;
        }

        private string CreateGroup()
        {
            return CreateGroup(null);
        }

        private string CreateGroup(AppItem item)
        {
            if (!_canSave) { ShowSaveDisabled(); return null; }
            string name = InputDialog("Новая группа", "Название группы:", "");
            if (String.IsNullOrWhiteSpace(name)) return null;
            if (ContainsGroup(AllGroups(), name))
            {
                MessageBox.Show(this, "Такая группа уже существует.", "AppShelf", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return null;
            }
            AppConfig pending = _config.Copy();
            pending.Groups.Add(name);
            pending.GroupingEnabled = true;
            if (item != null)
            {
                int index = _config.Items.IndexOf(item);
                if (index < 0) return null;
                pending.Items[index].Group = name;
            }
            if (!SaveConfigChanges(pending)) return null;
            _groupMenuItem.IsChecked = true;
            _newGroupMenuItem.IsEnabled = true;
            RenderTiles();
            return name;
        }

        // The ordering is stored in the existing groups array in apps.json.
        // No config migration or changes to application group membership needed.
        private bool SaveGroupOrder(List<string> orderedGroups)
        {
            AppConfig pending = _config.Copy();
            pending.Groups = new List<string>(orderedGroups);
            if (!SaveConfigChanges(pending)) return false;
            RenderTiles();
            return true;
        }

        private void MoveGroup(string group, int direction)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            List<string> groups = AllGroups();
            int current = groups.FindIndex(delegate(string name)
            { return String.Equals(name, group, StringComparison.OrdinalIgnoreCase); });
            int next = current + direction;
            if (current < 0 || next < 0 || next >= groups.Count) return;
            string temp = groups[current];
            groups[current] = groups[next];
            groups[next] = temp;
            SaveGroupOrder(groups);
        }

        private bool SwapGroups(string from, string to)
        {
            if (!_canSave) { ShowSaveDisabled(); return false; }
            if (String.IsNullOrWhiteSpace(from) || String.IsNullOrWhiteSpace(to)) return false;
            List<string> groups = AllGroups();
            int a = groups.FindIndex(delegate(string name)
            { return String.Equals(name, from, StringComparison.OrdinalIgnoreCase); });
            int b = groups.FindIndex(delegate(string name)
            { return String.Equals(name, to, StringComparison.OrdinalIgnoreCase); });
            if (a < 0 || b < 0 || a == b) return false;
            string temp = groups[a];
            groups[a] = groups[b];
            groups[b] = temp;
            return SaveGroupOrder(groups);
        }

        private bool SetGroupZoneOverride(string group, string value)
        {
            if (!_canSave) { ShowSaveDisabled(); return false; }
            if (String.IsNullOrWhiteSpace(group) ||
                (value != "inherit" && Array.IndexOf(FreeGroupZones, value) < 0))
                return false;
            AppConfig pending = _config.Copy();
            Dictionary<string, string> overrides = pending.GroupZoneOverrides ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new List<string>(overrides.Keys))
                if (String.Equals(key, group, StringComparison.OrdinalIgnoreCase))
                    overrides.Remove(key);
            if (value != "inherit") overrides[group] = value;
            pending.GroupZoneOverrides = overrides.Count == 0 ? null : overrides;
            if (!SaveConfigChanges(pending)) return false;
            RenderTiles();
            return true;
        }

        private void SetGroupAlignmentOverride(string group, string value)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            if (String.IsNullOrWhiteSpace(group) ||
                (value != "inherit" && NormalizeGroupOverride(value) == null)) return;
            AppConfig pending = _config.Copy();
            Dictionary<string, string> overrides = pending.GroupAlignmentOverrides ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            overrides.Remove(group);
            if (value != "inherit") overrides[group] = value;
            pending.GroupAlignmentOverrides = overrides.Count == 0 ? null : overrides;
            if (SaveConfigChanges(pending)) UpdateGroupWidths();
        }

        private void RenameGroup(string name)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            string newName = InputDialog("Переименовать группу", "Новое название:", name);
            if (String.IsNullOrWhiteSpace(newName) || newName == name) return;
            if (ContainsGroup(AllGroups(), newName))
            {
                MessageBox.Show(this, "Такая группа уже существует.", "AppShelf", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            AppConfig pending = _config.Copy();
            for (int i = 0; i < pending.Groups.Count; i++)
                if (String.Equals(pending.Groups[i], name, StringComparison.OrdinalIgnoreCase))
                    pending.Groups[i] = newName;
            foreach (AppItem item in pending.Items)
                if (String.Equals(GroupOf(item), name, StringComparison.OrdinalIgnoreCase)) item.Group = newName;
            if (pending.CanvasPositions != null)
            {
                foreach (string key in new List<string>(pending.CanvasPositions.Keys))
                    if (String.Equals(key, "app:" + name, StringComparison.OrdinalIgnoreCase))
                    {
                        FreeCanvasRect rect = pending.CanvasPositions[key];
                        pending.CanvasPositions.Remove(key);
                        pending.CanvasPositions["app:" + newName] = rect;
                        break;
                    }
            }
            if (pending.GroupZoneOverrides != null)
            {
                foreach (string key in new List<string>(pending.GroupZoneOverrides.Keys))
                    if (String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        string savedZone = pending.GroupZoneOverrides[key];
                        pending.GroupZoneOverrides.Remove(key);
                        pending.GroupZoneOverrides[newName] = savedZone;
                        break;
                    }
            }
            if (pending.GroupAlignmentOverrides != null)
            {
                string previousKey = null;
                foreach (string key in pending.GroupAlignmentOverrides.Keys)
                    if (String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                        { previousKey = key; break; }
                if (previousKey != null)
                {
                    string preference = pending.GroupAlignmentOverrides[previousKey];
                    pending.GroupAlignmentOverrides.Remove(previousKey);
                    pending.GroupAlignmentOverrides[newName] = preference;
                }
            }
            if (SaveConfigChanges(pending)) RenderTiles();
        }

        private void DeleteGroup(string name)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            if (MessageBox.Show(this, "Удалить группу «" + name + "»?\n" +
                "Приложения останутся в AppShelf и перейдут в «Без группы».", "AppShelf",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppConfig pending = _config.Copy();
            pending.Groups.RemoveAll(delegate(string group)
            { return String.Equals(group, name, StringComparison.OrdinalIgnoreCase); });
            foreach (AppItem item in pending.Items)
                if (String.Equals(GroupOf(item), name, StringComparison.OrdinalIgnoreCase)) item.Group = "";
            if (pending.CanvasPositions != null)
                foreach (string key in new List<string>(pending.CanvasPositions.Keys))
                    if (String.Equals(key, "app:" + name, StringComparison.OrdinalIgnoreCase))
                        pending.CanvasPositions.Remove(key);
            if (pending.GroupZoneOverrides != null)
            {
                foreach (string key in new List<string>(pending.GroupZoneOverrides.Keys))
                    if (String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                        pending.GroupZoneOverrides.Remove(key);
                if (pending.GroupZoneOverrides.Count == 0) pending.GroupZoneOverrides = null;
            }
            if (pending.GroupAlignmentOverrides != null)
            {
                pending.GroupAlignmentOverrides.Remove(name);
                if (pending.GroupAlignmentOverrides.Count == 0)
                    pending.GroupAlignmentOverrides = null;
            }
            if (SaveConfigChanges(pending)) RenderTiles();
        }

        private void SetItemGroup(AppItem item, string group)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            int index = _config.Items.IndexOf(item);
            if (index < 0) return;
            AppConfig pending = _config.Copy();
            pending.Items[index].Group = group ?? "";
            if (SaveConfigChanges(pending)) RenderTiles();
        }

        private void MoveItem(AppItem item, int step)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            List<AppItem> visible = new List<AppItem>();
            foreach (AppItem other in _config.Items)
            {
                if (_config.GroupingEnabled && !String.Equals(GroupOf(item), GroupOf(other),
                    StringComparison.OrdinalIgnoreCase)) continue;
                visible.Add(other);
            }
            int index = visible.IndexOf(item);
            int next = index + step;
            if (index < 0 || next < 0 || next >= visible.Count) return;
            int i = _config.Items.IndexOf(item);
            int j = _config.Items.IndexOf(visible[next]);
            AppConfig pending = _config.Copy();
            AppItem moved = pending.Items[i];
            pending.Items[i] = pending.Items[j];
            pending.Items[j] = moved;
            if (SaveConfigChanges(pending)) RenderTiles();
        }

        private void RenameItem(AppItem item)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            string name = InputDialog(
                String.Equals(item.Kind, "folder", StringComparison.OrdinalIgnoreCase)
                    ? "Переименовать папку в AppShelf" : "Переименовать приложение",
                "Подпись плитки:", item.Name);
            if (String.IsNullOrWhiteSpace(name)) return;
            int index = _config.Items.IndexOf(item);
            if (index < 0) return;
            AppConfig pending = _config.Copy();
            pending.Items[index].Name = name;
            if (SaveConfigChanges(pending)) RenderTiles();
        }

        private void RemoveItem(AppItem item)
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            if (MessageBox.Show(this, "Убрать «" + item.Name + "» из AppShelf?\n" +
                (String.Equals(item.Kind, "folder", StringComparison.OrdinalIgnoreCase)
                    ? "Сама папка и её содержимое не будут удалены."
                    : "Оригинальный ярлык и программа не будут удалены."), "AppShelf",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int index = _config.Items.IndexOf(item);
            if (index < 0) return;
            AppConfig pending = _config.Copy();
            pending.Items.RemoveAt(index);
            if (!SaveConfigChanges(pending)) return;
            // Save keeps copies referenced by the new .bak and retires them on a later rotation.
            RenderTiles();
        }

        private void ShowHappDiagnostics()
        {
            if (_happWindow != null)
            {
                if (_happWindow.WindowState == WindowState.Minimized)
                    _happWindow.WindowState = WindowState.Normal;
                _happWindow.Activate();
                return;
            }
            _happWindow = new HappDiagnosticsWindow(_config.Copy());
            _happWindow.Owner = this;
            _happWindow.Closed += delegate { _happWindow = null; };
            _happWindow.Show();
        }

        // Package #11: one independent countdown, never linked to Pomodoro.
        private void UpdateCountdownAvailability()
        {
            if (_config.ShowCountdownTimer == false)
            {
                if (_countdownTimer != null) { _countdownTimer.Stop(); _countdownTimer = null; }
                if (_countdownEngine != null)
                    _countdownEngine.Reset(_countdownEngine.DurationSeconds);
                if (_countdownWindow != null) _countdownWindow.Close();
                UpdateCountdownUi();
                return;
            }
            if (_countdownTimer == null && _networkTimer != null)
            {
                _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _countdownTimer.Tick += delegate { TickCountdown(); };
                _countdownTimer.Start();
            }
        }

        private void TickCountdown()
        {
            if (_countdownEngine != null && _countdownEngine.Advance(DateTime.UtcNow))
            {
                NotificationAudio.PlayCountdown(_config);
                if (_trayIcon != null)
                {
                    try { _trayIcon.ShowBalloonTip(6000, "AppShelf · Таймер",
                        "Время вышло.", Forms.ToolTipIcon.Info); }
                    catch (Exception ex) { ConfigStore.Log(ex); }
                }
                ShowStatusMessage("Таймер завершён.");
            }
            UpdateCountdownUi();
        }

        private static string CountdownClock(TimeSpan left)
        {
            int seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
            return (seconds / 3600).ToString("00") + ":" +
                (seconds / 60 % 60).ToString("00") + ":" +
                (seconds % 60).ToString("00");
        }

        private void UpdateCountdownUi()
        {
            if (_countdownEngine == null) return;
            string clock = CountdownClock(_countdownEngine.RemainingAt(DateTime.UtcNow));
            if (_countdownText != null)
                _countdownText.Text = "Таймер: " + clock +
                    (_countdownEngine.Running ? " ▶" : _countdownEngine.Paused ? " ⏸" : "");
            if (_countdownClock != null) _countdownClock.Text = clock;
            if (_countdownStage != null)
                _countdownStage.Text = _countdownEngine.Completed ? "Время вышло" :
                    _countdownEngine.Running ? "Идёт отсчёт" :
                    _countdownEngine.Paused ? "Пауза" : "Готов к запуску";
            if (_countdownToggle != null)
                _countdownToggle.Content = _countdownEngine.Running ? "Пауза" :
                    _countdownEngine.Paused ? "Продолжить" : "Старт";
            bool editable = !_countdownEngine.Running && !_countdownEngine.Paused;
            if (_countdownHours != null) _countdownHours.IsEnabled = editable;
            if (_countdownMinutes != null) _countdownMinutes.IsEnabled = editable;
            if (_countdownSeconds != null) _countdownSeconds.IsEnabled = editable;
        }

        private void ToggleCountdown()
        {
            if (_config.ShowCountdownTimer == false) return;
            DateTime now = DateTime.UtcNow;
            if (_countdownEngine.Running)
            {
                if (_countdownEngine.Advance(now))
                {
                    // The event must be delivered even if the UI thread was blocked.
                    NotificationAudio.PlayCountdown(_config);
                    if (_trayIcon != null)
                    {
                        try { _trayIcon.ShowBalloonTip(6000, "AppShelf · Таймер",
                            "Время вышло.", Forms.ToolTipIcon.Info); }
                        catch (Exception ex) { ConfigStore.Log(ex); }
                    }
                    ShowStatusMessage("Таймер завершён.");
                }
                else _countdownEngine.Pause(now);
            }
            else if (_countdownEngine.Paused) _countdownEngine.Start(now);
            else
            {
                int h, m, sec;
                if (!Int32.TryParse(_countdownHours.Text, out h) || h < 0 || h > 23 ||
                    !Int32.TryParse(_countdownMinutes.Text, out m) || m < 0 || m > 59 ||
                    !Int32.TryParse(_countdownSeconds.Text, out sec) || sec < 0 || sec > 59 ||
                    h + m + sec == 0)
                {
                    MessageBox.Show(_countdownWindow, "Укажи время от 00:00:01 до 23:59:59.",
                        "AppShelf · Таймер", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                int duration = h * 3600 + m * 60 + sec;
                _countdownEngine.Reset(duration);
                _countdownEngine.Start(now);
                if (_canSave && _config.CountdownDurationSeconds != duration)
                {
                    AppConfig pending = _config.Copy();
                    pending.CountdownDurationSeconds = duration;
                    SaveConfigChanges(pending, false);
                }
            }
            UpdateCountdownUi();
        }

        private void ResetCountdown()
        {
            _countdownEngine.Reset(_countdownEngine.DurationSeconds);
            UpdateCountdownUi();
        }

        private void ShowCountdownWindow()
        {
            if (_config.ShowCountdownTimer == false) return;
            if (_countdownWindow != null)
            {
                if (_countdownWindow.WindowState == WindowState.Minimized)
                    _countdownWindow.WindowState = WindowState.Normal;
                _countdownWindow.Activate();
                return;
            }
            Window dialog = new Window
            {
                Title = "AppShelf — таймер", Owner = this, Width = 460, Height = 380,
                MinWidth = 425, MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            _countdownWindow = dialog;
            StackPanel panel = new StackPanel { Margin = new Thickness(22) };
            dialog.Content = panel;
            TextBlock heading = Text("ТАЙМЕР ОБРАТНОГО ОТСЧЁТА", 11, AccentBrush);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(heading);
            _countdownStage = Text("", 14, MutedBrush);
            panel.Children.Add(_countdownStage);
            _countdownClock = Text("", 55, WhiteBrush);
            _countdownClock.FontWeight = FontWeights.Bold;
            _countdownClock.Margin = new Thickness(0, 8, 0, 15);
            panel.Children.Add(_countdownClock);
            WrapPanel buttons = new WrapPanel();
            _countdownToggle = ActionButton("Старт", true);
            _countdownToggle.Click += delegate { ToggleCountdown(); };
            buttons.Children.Add(_countdownToggle);
            Button reset = ActionButton("Сброс", false);
            reset.Click += delegate { ResetCountdown(); };
            buttons.Children.Add(reset);
            panel.Children.Add(buttons);
            TextBlock hint = Text("Длительность (часы / минуты / секунды):", 12, MutedBrush);
            hint.Margin = new Thickness(0, 7, 0, 5);
            panel.Children.Add(hint);
            WrapPanel fields = new WrapPanel();
            panel.Children.Add(fields);
            int duration = _countdownEngine.DurationSeconds;
            _countdownHours = PomodoroNumberBox(fields, "Часы", duration / 3600);
            _countdownMinutes = PomodoroNumberBox(fields, "Минуты", duration / 60 % 60);
            _countdownSeconds = PomodoroNumberBox(fields, "Секунды", duration % 60);
            _countdownHours.MaxLength = 2;
            _countdownMinutes.MaxLength = 2;
            _countdownSeconds.MaxLength = 2;
            TextBlock soundHint = Text("Звук настраивается в общих настройках AppShelf.", 11, MutedBrush);
            soundHint.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(soundHint);
            dialog.Closed += delegate
            {
                _countdownWindow = null;
                _countdownClock = null;
                _countdownStage = null;
                _countdownToggle = null;
                _countdownHours = null;
                _countdownMinutes = null;
                _countdownSeconds = null;
            };
            UpdateCountdownUi();
            dialog.Show();
        }

        private string PomodoroPhaseName()
        {
            if (_pomodoroPhase == 0) return "Работа";
            return _pomodoroPhase == 1 ? "Короткий перерыв" : "Длинный перерыв";
        }

        private TimeSpan PomodoroPhaseDuration()
        {
            int minutes = _pomodoroPhase == 0 ? _config.PomodoroWorkMinutes :
                (_pomodoroPhase == 1 ? _config.PomodoroShortMinutes : _config.PomodoroLongMinutes);
            return TimeSpan.FromMinutes(minutes);
        }

        private TimeSpan PomodoroRemainingNow()
        {
            if (!_pomodoroRunning) return _pomodoroRemaining;
            TimeSpan value = _pomodoroDeadlineUtc - DateTime.UtcNow;
            return value > TimeSpan.Zero ? value : TimeSpan.Zero;
        }

        private void TickPomodoro()
        {
            if (_pomodoroRunning && DateTime.UtcNow >= _pomodoroDeadlineUtc)
            {
                bool workFinished = _pomodoroPhase == 0;
                _pomodoroRunning = false;
                if (workFinished)
                {
                    _completedPomodoros++;
                    _pomodoroPhase = _completedPomodoros % 4 == 0 ? 2 : 1;
                }
                else _pomodoroPhase = 0;
                _pomodoroRemaining = PomodoroPhaseDuration();
                string message = workFinished ? "Работа закончена. Пора сделать перерыв." :
                    "Перерыв закончился. Можно начинать работу.";
                NotificationAudio.PlayPomodoro(_config);
                if (_trayIcon != null)
                {
                    try { _trayIcon.ShowBalloonTip(6000, "AppShelf · Помодоро", message,
                        Forms.ToolTipIcon.Info); }
                    catch (Exception ex) { ConfigStore.Log(ex); }
                }
                ShowStatusMessage(message + " Следующий этап запускается вручную.");
            }
            UpdatePomodoroUi();
        }

        private void UpdatePomodoroUi()
        {
            TimeSpan left = PomodoroRemainingNow();
            int seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
            string clock = (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
            if (_pomodoro != null)
                _pomodoro.Text = "Помодоро: " + PomodoroPhaseName() + " " + clock +
                    (_pomodoroRunning ? " ▶" : " ⏸");
            if (_pomodoroClock != null) _pomodoroClock.Text = clock;
            if (_pomodoroStage != null)
                _pomodoroStage.Text = PomodoroPhaseName() + " · завершено рабочих этапов: " + _completedPomodoros;
            if (_pomodoroToggle != null)
                _pomodoroToggle.Content = _pomodoroRunning ? "Пауза" : "Старт";
        }

        private void TogglePomodoro()
        {
            if (_pomodoroRunning)
            {
                _pomodoroRemaining = PomodoroRemainingNow();
                _pomodoroRunning = false;
            }
            else
            {
                if (_pomodoroRemaining <= TimeSpan.Zero) _pomodoroRemaining = PomodoroPhaseDuration();
                _pomodoroDeadlineUtc = DateTime.UtcNow + _pomodoroRemaining;
                _pomodoroRunning = true;
            }
            UpdatePomodoroUi();
        }

        private void ResetPomodoro(bool skip)
        {
            _pomodoroRunning = false;
            if (skip) _pomodoroPhase = _pomodoroPhase == 0 ? 1 : 0;
            _pomodoroRemaining = PomodoroPhaseDuration();
            UpdatePomodoroUi();
        }

        private void ShowPomodoroWindow()
        {
            if (_pomodoroWindow != null)
            {
                if (_pomodoroWindow.WindowState == WindowState.Minimized)
                    _pomodoroWindow.WindowState = WindowState.Normal;
                _pomodoroWindow.Activate();
                return;
            }
            Window dialog = new Window
            {
                Title = "AppShelf — Помодоро", Owner = this, Width = 475, Height = 410,
                MinWidth = 435, MinHeight = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            _pomodoroWindow = dialog;
            StackPanel panel = new StackPanel { Margin = new Thickness(22) };
            dialog.Content = panel;
            TextBlock focusCaption = Text("ТАЙМЕР ФОКУСА", 11, AccentBrush);
            focusCaption.FontWeight = FontWeights.SemiBold;
            focusCaption.Margin = new Thickness(0, 0, 0, 9);
            panel.Children.Add(focusCaption);
            _pomodoroStage = Text("", 14, MutedBrush);
            panel.Children.Add(_pomodoroStage);
            _pomodoroClock = Text("", 57, WhiteBrush);
            _pomodoroClock.FontWeight = FontWeights.Bold;
            _pomodoroClock.Margin = new Thickness(0, 8, 0, 16);
            panel.Children.Add(_pomodoroClock);
            WrapPanel buttons = new WrapPanel();
            panel.Children.Add(buttons);
            _pomodoroToggle = ActionButton("Старт", true);
            _pomodoroToggle.Click += delegate { TogglePomodoro(); };
            buttons.Children.Add(_pomodoroToggle);
            Button reset = ActionButton("Сброс", false);
            reset.Click += delegate { ResetPomodoro(false); };
            buttons.Children.Add(reset);
            Button skip = ActionButton("Следующий этап", false);
            skip.Click += delegate { ResetPomodoro(true); };
            buttons.Children.Add(skip);
            panel.Children.Add(Text("Интервалы, минут (применяются после сброса или к следующему этапу):", 12, MutedBrush));
            WrapPanel settingRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 3) };
            panel.Children.Add(settingRow);
            TextBox work = PomodoroNumberBox(settingRow, "Работа", _config.PomodoroWorkMinutes);
            TextBox shortBreak = PomodoroNumberBox(settingRow, "Короткий", _config.PomodoroShortMinutes);
            TextBox longBreak = PomodoroNumberBox(settingRow, "Длинный", _config.PomodoroLongMinutes);
            Button apply = ActionButton("Сохранить интервалы", false);
            apply.IsEnabled = _canSave;
            apply.Margin = new Thickness(0, 9, 0, 4);
            apply.Click += delegate
            {
                int w, sb, lb;
                if (!Int32.TryParse(work.Text, out w) || w < 1 || w > 240 ||
                    !Int32.TryParse(shortBreak.Text, out sb) || sb < 1 || sb > 120 ||
                    !Int32.TryParse(longBreak.Text, out lb) || lb < 1 || lb > 120)
                {
                    MessageBox.Show(dialog, "Работа: 1–240 минут, перерывы: 1–120 минут.",
                        "Помодоро", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                AppConfig pending = _config.Copy();
                pending.PomodoroWorkMinutes = w;
                pending.PomodoroShortMinutes = sb;
                pending.PomodoroLongMinutes = lb;
                if (!SaveConfigChanges(pending)) return;
                ShowStatusMessage("Интервалы Помодоро сохранены.");
            };
            panel.Children.Add(apply);
            dialog.Closed += delegate
            {
                _pomodoroWindow = null;
                _pomodoroClock = null;
                _pomodoroStage = null;
                _pomodoroToggle = null;
            };
            UpdatePomodoroUi();
            dialog.Show();
        }

        private static TextBox PomodoroNumberBox(WrapPanel parent, string name, int value)
        {
            StackPanel column = new StackPanel { Margin = new Thickness(0, 0, 13, 0) };
            column.Children.Add(Text(name, 12, MutedBrush));
            TextBox input = new TextBox { Width = 80, Height = 27,
                Padding = new Thickness(4, 3, 4, 3), Text = value.ToString() };
            column.Children.Add(input);
            parent.Children.Add(column);
            return input;
        }

        private void UpdateRdpStatus()
        {
            if (_rdpStatus == null) return;
            if (!_config.RdpKeepAwake) _rdpStatus.Text = "RDP: выключено";
            else if (!RdpKeepAwake.IsRemoteSession()) _rdpStatus.Text = "RDP: не удалённый сеанс";
            else _rdpStatus.Text = "RDP: антипростой включён";
        }

        private void TickRdpKeepAwake()
        {
            if (!_config.RdpKeepAwake) return;
            try { RdpKeepAwake.KeepSessionActiveIfIdle(); }
            catch (Exception ex) { ConfigStore.Log(ex); }
            UpdateRdpStatus();
        }

        private static List<UIElement> SettingsRange(List<UIElement> controls, UIElement first, UIElement last)
        {
            int start = controls.IndexOf(first);
            int end = controls.IndexOf(last);
            if (start < 0 || end < start)
                throw new InvalidOperationException("Не удалось собрать раздел настроек.");
            return controls.GetRange(start, end - start + 1);
        }

        // A vector chevron avoids font-dependent triangle glyphs and stays thin at high DPI.
        private static void SetSettingsArrow(System.Windows.Shapes.Path arrow, bool expanded)
        {
            arrow.Data = Geometry.Parse(expanded
                ? "M 1,2 L 5,7 L 9,2"
                : "M 2,1 L 7,5 L 2,9");
        }

        private static void AddSettingsSection(StackPanel layout, string key, string title,
            IEnumerable<UIElement> elements, Dictionary<string, Border> bodies,
            Dictionary<string, System.Windows.Shapes.Path> arrows, Dictionary<string, Border> headers,
            Action<string> toggle)
        {
            // One enclosing panel owns the edges; sections are rows separated by thin lines.
            if (layout.Children.Count > 0)
                layout.Children.Add(new Border
                {
                    Tag = "SettingsAccordionDivider",
                    Height = 1, Background = FluentUi.Brush("#334153"),
                    Margin = new Thickness(10, 0, 10, 0)
                });
            StackPanel section = new StackPanel();
            Button header = ActionButton("", false);
            header.Style = FluentUi.BottomCategoryButtonStyle();
            header.Background = Brushes.Transparent;
            header.BorderBrush = Brushes.Transparent;
            header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            header.Margin = new Thickness(0);
            header.Padding = new Thickness(10, 6, 10, 6);
            header.MinHeight = 36;
            StackPanel caption = new StackPanel { Orientation = Orientation.Horizontal };
            System.Windows.Shapes.Path arrow = new System.Windows.Shapes.Path
            {
                Width = 10, Height = 10,
                Stroke = FluentUi.Secondary, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            SetSettingsArrow(arrow, false);
            caption.Children.Add(arrow);
            TextBlock label = Text(title, 12.5, WhiteBrush);
            label.FontWeight = FontWeights.Medium;
            caption.Children.Add(label);
            header.Content = caption;
            header.Tag = key;
            header.ToolTip = "Развернуть или свернуть раздел " + title;
            header.Click += delegate { toggle(key); };
            Border heading = new Border
            {
                Tag = "SettingsAccordionHeader:" + key,
                CornerRadius = new CornerRadius(9),
                Background = Brushes.Transparent, Child = header
            };
            section.Children.Add(heading);

            StackPanel contents = new StackPanel();
            foreach (UIElement control in elements) contents.Children.Add(control);
            Border body = new Border
            {
                // No separate filled card: the single container stays visible behind contents.
                Background = Brushes.Transparent,
                Padding = new Thickness(14, 8, 14, 10),
                Child = contents, Visibility = Visibility.Collapsed
            };
            section.Children.Add(body);
            bodies[key] = body;
            arrows[key] = arrow;
            headers[key] = heading;
            layout.Children.Add(section);
        }

        private static void AddNetworkTargetRow(StackPanel parent, int index,
            string savedMode, string savedTarget, out ComboBox mode, out TextBox address)
        {
            parent.Children.Add(Text("Проверка " + index, 12, WhiteBrush));
            mode = new ComboBox { Tag = "NetworkTargetMode" + index,
                MinHeight = 33, Margin = new Thickness(0, 4, 0, 5) };
            mode.Items.Add(new ComboBoxItem { Content = "По умолчанию — ICMP + HTTPS", Tag = "default" });
            mode.Items.Add(new ComboBoxItem { Content = "ICMP — ping узла", Tag = "icmp" });
            mode.Items.Add(new ComboBoxItem { Content = "HTTPS — проверка сайта", Tag = "https" });
            mode.Items.Add(new ComboBoxItem { Content = "TCP — подключение к порту", Tag = "tcp" });
            string selected = String.IsNullOrWhiteSpace(savedMode) ? "default" : savedMode;
            mode.SelectedIndex = selected == "icmp" ? 1 : selected == "https" ? 2 :
                selected == "tcp" ? 3 : 0;
            parent.Children.Add(mode);
            address = new TextBox
            {
                Text = selected == "default" ? NetworkChecks.DefaultHost(index) : savedTarget ?? "",
                MinHeight = 31, Padding = new Thickness(8, 5, 8, 5),
                Tag = "NetworkTargetAddress" + index,
                Background = TileBrush, BorderBrush = StrokeBrush,
                Foreground = WhiteBrush,
                Margin = new Thickness(0, 0, 0, 12)
            };
            parent.Children.Add(address);
            ComboBox selectedMode = mode;
            TextBox input = address;
            selectedMode.SelectionChanged += delegate
            {
                string type = (string)((ComboBoxItem)selectedMode.SelectedItem).Tag;
                bool builtIn = type == "default";
                input.IsEnabled = !builtIn;
                if (builtIn) input.Text = NetworkChecks.DefaultHost(index);
                else if (String.IsNullOrWhiteSpace(input.Text) ||
                         String.Equals(input.Text, NetworkChecks.DefaultHost(index),
                             StringComparison.OrdinalIgnoreCase))
                {
                    string host = NetworkChecks.DefaultHost(index);
                    input.Text = type == "https" ? (index == 1
                        ? "https://1.1.1.1/cdn-cgi/trace" : "https://ya.ru/") :
                        type == "tcp" ? host + ":443" : host;
                }
            };
            address.IsEnabled = mode.SelectedIndex != 0;
        }

        // The gallery only selects an existing wallpaper. Import and deletion remain
        // in Settings, so the two entry points share one library and saved preset.
        private static Brush BackgroundGalleryThumbnail(string preset)
        {
            if (!CustomBackgroundStore.IsCustomPreset(preset))
                return FluentUi.MainWindowBackground(preset, 190, 108);
            string path = CustomBackgroundStore.FilePath(preset);
            if (path == null || !File.Exists(path)) return null;
            try
            {
                BitmapImage image = new BitmapImage();
                using (FileStream input = File.OpenRead(path))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(input,
                        BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    BitmapFrame frame = decoder.Frames[0];
                    int width = frame.PixelWidth;
                    int height = frame.PixelHeight;
                    input.Position = 0;
                    image.BeginInit();
                    image.StreamSource = input;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    // Bound both landscape and portrait thumbnails; never upscale
                    // a narrow source into a bitmap hundreds of thousands of pixels tall.
                    if (width >= height) image.DecodePixelWidth = Math.Min(320, width);
                    else image.DecodePixelHeight = Math.Min(320, height);
                    image.EndInit();
                    image.Freeze();
                }
                ImageBrush brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                return brush;
            }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                return null;
            }
        }

        private void ShowBackgroundGallery()
        {
            if (!_canSave) { ShowSaveDisabled(); return; }
            Window dialog = new Window
            {
                Title = "AppShelf — галерея фонов", Owner = this,
                Width = 745, Height = 535, MinWidth = 495, MinHeight = 385,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;

            StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            TextBlock heading = Text("Выбрать фон AppShelf", 23, WhiteBrush);
            heading.FontWeight = FontWeights.Bold;
            header.Children.Add(heading);
            TextBlock explanation = Text(
                "Нажми на изображение, чтобы сразу применить его. Добавлять и удалять фоны можно в настройках.",
                12, MutedBrush);
            explanation.TextWrapping = TextWrapping.Wrap;
            explanation.Margin = new Thickness(0, 5, 0, 0);
            header.Children.Add(explanation);

            Button close = ActionButton("Закрыть", false);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 10, 0, 0);
            close.Click += delegate { dialog.Close(); };
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            FluentUi.StyleSettingsScroll(scroll);
            root.Children.Add(scroll);
            WrapPanel tiles = new WrapPanel();
            scroll.Content = tiles;
            Dictionary<string, Button> choices =
                new Dictionary<string, Button>(StringComparer.Ordinal);

            List<string> presets = new List<string>
            {
                "blue-arcs", "midnight", "graphite", "classic"
            };
            presets.AddRange(CustomBackgroundStore.ListPresets());
            foreach (string preset in presets)
            {
                string selectedPreset = preset;
                string label = preset == "blue-arcs" ? "Синие дуги" :
                    preset == "midnight" ? "Полночь" :
                    preset == "graphite" ? "Графит" :
                    preset == "classic" ? "Классический" :
                    CustomBackgroundStore.DisplayName(preset);
                Brush thumbnail = BackgroundGalleryThumbnail(preset);
                Button choice = ActionButton("", false);
                choice.Width = 210;
                choice.Height = 153;
                choice.Padding = new Thickness(6);
                choice.Margin = new Thickness(0, 0, 12, 12);
                choice.ToolTip = label;
                choice.IsEnabled = thumbnail != null;
                StackPanel content = new StackPanel();
                Border picture = new Border
                {
                    Width = 190, Height = 108,
                    Background = thumbnail ?? FluentUi.MainWindowBackground("midnight"),
                    CornerRadius = new CornerRadius(6)
                };
                content.Children.Add(picture);
                TextBlock caption = Text(thumbnail == null ? label + " (не удалось прочитать)" : label,
                    12, WhiteBrush);
                caption.TextTrimming = TextTrimming.CharacterEllipsis;
                caption.MaxWidth = 190;
                caption.Margin = new Thickness(2, 6, 2, 0);
                content.Children.Add(caption);
                choice.Content = content;
                choices.Add(preset, choice);
                tiles.Children.Add(choice);
                choice.Click += delegate
                {
                    if (String.Equals(_config.MainBackgroundPreset, selectedPreset,
                        StringComparison.Ordinal)) return;
                    AppConfig pending = _config.Copy();
                    pending.MainBackgroundPreset = selectedPreset;
                    if (!SaveConfigChanges(pending)) return;
                    ApplyMainBackground(selectedPreset);
                    ApplyGroupAppearancePreview(selectedPreset,
                        FluentUi.NormalizeGroupOpacity(_config.GroupOpacity));
                    RenderTiles();
                    foreach (KeyValuePair<string, Button> item in choices)
                        item.Value.BorderBrush = item.Key == selectedPreset
                            ? AccentBrush : StrokeBrush;
                };
            }
            foreach (KeyValuePair<string, Button> item in choices)
                item.Value.BorderBrush = item.Key == _config.MainBackgroundPreset
                    ? AccentBrush : StrokeBrush;
            dialog.ShowDialog();
        }

        private void ShowSettings()
        {
            ShowSettingsSection(null);
        }

        // Direct entry points open a specific section without changing the user's
        // normal entry point from the main Settings menu.
        private void ShowSettingsSection(string requestedSection)
        {
            WindowsStartup.State startupState;
            try { startupState = WindowsStartup.Read(); }
            catch (Exception ex)
            {
                ShowError("Не удалось прочитать автозапуск Windows.", ex);
                return;
            }
            Window dialog = new Window
            {
                Title = "AppShelf — настройки", Owner = this, Width = 555, Height = 555,
                MinWidth = 490, MinHeight = 430,
                ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            DockPanel settingsRoot = new DockPanel();
            dialog.Content = settingsRoot;
            ScrollViewer settingsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            FluentUi.StyleSettingsScroll(settingsScroll);
            StackPanel layout = new StackPanel { Margin = new Thickness(22, 19, 22, 12) };
            settingsScroll.Content = layout;
            settingsRoot.Children.Add(settingsScroll);
            TextBlock settingsTitle = Text("Настройки", 24, WhiteBrush);
            settingsTitle.FontWeight = FontWeights.Bold;
            settingsTitle.Margin = new Thickness(0, 0, 0, 2);
            layout.Children.Add(settingsTitle);
            TextBlock settingsHint = Text("Настрой панель под свой рабочий процесс.", 12, MutedBrush);
            settingsHint.Margin = new Thickness(0, 0, 0, 19);
            layout.Children.Add(settingsHint);
            TextBlock panelTitle = Text("НИЖНЯЯ ПАНЕЛЬ", 11, AccentBrush);
            panelTitle.FontWeight = FontWeights.SemiBold;
            panelTitle.Margin = new Thickness(0, 0, 0, 12);
            layout.Children.Add(panelTitle);
            layout.Children.Add(Text("Режим отображения:", 12, MutedBrush));
            ComboBox panelMode = new ComboBox { MinHeight = 34, Margin = new Thickness(0, 5, 0, 9) };
            panelMode.Items.Add(new ComboBoxItem { Content = "Компактный — избранные показатели", Tag = "compact" });
            panelMode.Items.Add(new ComboBoxItem { Content = "Категории — Сеть, Погода, Время", Tag = "categories" });
            panelMode.Items.Add(new ComboBoxItem { Content = "Подробный — все показатели", Tag = "detailed" });
            string currentPanelMode = NormalizeBottomPanelMode(_config.BottomPanelMode);
            panelMode.SelectedIndex = currentPanelMode == "compact" ? 0 : currentPanelMode == "categories" ? 1 : 2;
            layout.Children.Add(panelMode);
            StackPanel categorySettings = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            categorySettings.Children.Add(Text("Видимые категории (хотя бы одна):", 12, MutedBrush));
            WrapPanel categoryChecks = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            categorySettings.Children.Add(categoryChecks);
            List<string> enabledCategories = BottomPanelCategories(_config);
            Dictionary<string, CheckBox> categoryOptions = new Dictionary<string, CheckBox>();
            foreach (string category in new string[] { "network", "weather", "time" })
            {
                CheckBox check = new CheckBox
                {
                    Content = category == "network" ? "Сеть" : category == "weather" ? "Погода" : "Время",
                    IsChecked = enabledCategories.Contains(category),
                    Tag = category, Foreground = WhiteBrush, Margin = new Thickness(0, 0, 18, 0)
                };
                categoryOptions.Add(category, check);
                categoryChecks.Children.Add(check);
            }
            layout.Children.Add(categorySettings);
            TextBlock favoritesLabel = Text("Избранное в компактном режиме (галочка — показывать):", 12, MutedBrush);
            layout.Children.Add(favoritesLabel);
            ListBox favoriteList = new ListBox
            {
                Height = 191, Background = TileBrush, Foreground = WhiteBrush,
                BorderBrush = StrokeBrush, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 6, 0, 5)
            };
            FluentUi.StyleBottomPanelInputs(panelMode, favoriteList);
            List<string> configuredFavorites = BottomPanelFavorites(_config);
            List<string> orderedKeys = new List<string>(configuredFavorites);
            foreach (string key in BottomPanelKeys)
                if (!orderedKeys.Contains(key)) orderedKeys.Add(key);
            foreach (string key in orderedKeys)
            {
                CheckBox check = new CheckBox
                {
                    Content = BottomFavoriteName(key) ?? BottomIndicatorName(key), Tag = key,
                    IsChecked = configuredFavorites.Contains(key),
                    Padding = new Thickness(2, 3, 0, 3)
                };
                favoriteList.Items.Add(new ListBoxItem { Content = check });
            }
            layout.Children.Add(favoriteList);
            WrapPanel favoriteActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
            Button favoriteUp = ActionButton("↑ Выше", false);
            favoriteUp.Click += delegate
            {
                int i = favoriteList.SelectedIndex;
                if (i < 1) return;
                object row = favoriteList.Items[i];
                favoriteList.Items.RemoveAt(i);
                favoriteList.Items.Insert(i - 1, row);
                favoriteList.SelectedIndex = i - 1;
            };
            favoriteActions.Children.Add(favoriteUp);
            Button favoriteDown = ActionButton("↓ Ниже", false);
            favoriteDown.Click += delegate
            {
                int i = favoriteList.SelectedIndex;
                if (i < 0 || i >= favoriteList.Items.Count - 1) return;
                object row = favoriteList.Items[i];
                favoriteList.Items.RemoveAt(i);
                favoriteList.Items.Insert(i + 1, row);
                favoriteList.SelectedIndex = i + 1;
            };
            favoriteActions.Children.Add(favoriteDown);
            layout.Children.Add(favoriteActions);
            Action updateFavoriteAvailability = delegate
            {
                string mode = ((ComboBoxItem)panelMode.SelectedItem).Tag.ToString();
                bool compact = mode == "compact";
                favoriteList.IsEnabled = compact;
                favoriteUp.IsEnabled = compact;
                favoriteDown.IsEnabled = compact;
                // Keep inactive selections in memory and on disk; they are only
                // hidden from the editor until their display mode is selected.
                favoritesLabel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                favoriteList.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                favoriteActions.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                categorySettings.Visibility = mode == "categories"
                    ? Visibility.Visible : Visibility.Collapsed;
                categorySettings.IsEnabled = mode == "categories";
                int selected = 0;
                foreach (CheckBox check in categoryOptions.Values)
                    if (check.IsChecked == true) selected++;
                foreach (CheckBox check in categoryOptions.Values)
                    check.IsEnabled = mode == "categories" &&
                        (check.IsChecked != true || selected > 1);
            };
            foreach (CheckBox categoryCheck in categoryOptions.Values)
            {
                categoryCheck.Checked += delegate { updateFavoriteAvailability(); };
                categoryCheck.Unchecked += delegate { updateFavoriteAvailability(); };
            }
            panelMode.SelectionChanged += delegate { updateFavoriteAvailability(); };
            updateFavoriteAvailability();
            CheckBox network = new CheckBox
            {
                Content = "Показывать сетевые проверки в нижней панели", IsChecked = _config.ShowNetwork,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 12)
            };
            StackPanel networkChecksSettings = new StackPanel();
            TextBlock networkDescription = Text(
                "Две проверки вместо стандартных 1.1.1.1 и ya.ru. Выбери тип и укажи адрес. Дополнительных фоновых запросов не будет.",
                11, MutedBrush);
            networkDescription.TextWrapping = TextWrapping.Wrap;
            networkChecksSettings.Children.Add(networkDescription);
            ComboBox networkMode1, networkMode2;
            TextBox networkAddress1, networkAddress2;
            AddNetworkTargetRow(networkChecksSettings, 1,
                _config.NetworkCheck1Mode, _config.NetworkCheck1Target,
                out networkMode1, out networkAddress1);
            AddNetworkTargetRow(networkChecksSettings, 2,
                _config.NetworkCheck2Mode, _config.NetworkCheck2Target,
                out networkMode2, out networkAddress2);
            // Reuse the existing dark ComboBox template and scoped dropdown styles.
            // Without both, Windows renders these new network controls in system light theme.
            foreach (ComboBox networkMode in new ComboBox[] { networkMode1, networkMode2 })
            {
                networkMode.Style = panelMode.Style;
                foreach (ResourceDictionary resources in panelMode.Resources.MergedDictionaries)
                    networkMode.Resources.MergedDictionaries.Add(resources);
            }
            TextBlock networkHint = Text(
                "ICMP: host/IP  ·  HTTPS: https://site.example/path  ·  TCP: host:port",
                11, MutedBrush);
            networkHint.TextWrapping = TextWrapping.Wrap;
            networkChecksSettings.Children.Add(networkHint);
            if (_config.NetworkPluginMigrated)
            {
                network.Content = "Сетевые проверки перенесены в плагин «Сеть»";
                network.IsEnabled = false;
                networkChecksSettings.Visibility = Visibility.Collapsed;
            }

            CheckBox weather = new CheckBox
            {
                Content = "Показывать погоду", IsChecked = _config.ShowWeather,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 10)
            };
            layout.Children.Add(network);
            layout.Children.Add(weather);
            CheckBox pomodoro = new CheckBox
            {
                Content = "Показывать таймер Помодоро", IsChecked = _config.ShowPomodoro,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 11)
            };
            layout.Children.Add(pomodoro);
            CheckBox countdown = new CheckBox
            {
                Content = "Показывать обычный таймер", IsChecked = _config.ShowCountdownTimer != false,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 11)
            };
            layout.Children.Add(countdown);
            TextBlock sessionTitle = Text("УДАЛЁННЫЙ СЕАНС", 11, AccentBrush);
            sessionTitle.FontWeight = FontWeights.SemiBold;
            sessionTitle.Margin = new Thickness(0, 7, 0, 10);
            layout.Children.Add(sessionTitle);
            CheckBox rdp = new CheckBox
            {
                Content = "Не давать простаивать RDP-сеансу (только внутри RDP)",
                IsChecked = _config.RdpKeepAwake, Foreground = WhiteBrush,
                Margin = new Thickness(0, 0, 0, 5)
            };
            layout.Children.Add(rdp);
            TextBlock rdpNote = Text("Режим отправляет F15 при бездействии раз в ~50 с. Может не отменять политики сервера.", 11, MutedBrush);
            rdpNote.TextWrapping = TextWrapping.Wrap;
            rdpNote.Margin = new Thickness(16, 0, 0, 10);
            layout.Children.Add(rdpNote);
            TextBlock weatherTitle = Text("ПОГОДА", 11, AccentBrush);
            weatherTitle.FontWeight = FontWeights.SemiBold;
            weatherTitle.Margin = new Thickness(0, 2, 0, 9);
            layout.Children.Add(weatherTitle);
            layout.Children.Add(Text("Город для погоды:", 12, MutedBrush));
            TextBox city = new TextBox
            {
                Text = _config.WeatherCity ?? "", Height = 29,
                Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 5, 0, 12)
            };
            layout.Children.Add(city);
            layout.Children.Add(Text("Источник прогноза:", 12, MutedBrush));
            ComboBox weatherProvider = new ComboBox { Height = 30, Margin = new Thickness(0, 5, 0, 8) };
            weatherProvider.Style = panelMode.Style;
            foreach (ResourceDictionary styles in panelMode.Resources.MergedDictionaries)
                weatherProvider.Resources.MergedDictionaries.Add(styles);
            weatherProvider.Items.Add(new ComboBoxItem { Content = "MET Norway — бесплатно, без ключа", Tag = "metno" });
            weatherProvider.Items.Add(new ComboBoxItem { Content = "Open-Meteo — бесплатно, без ключа", Tag = "openmeteo" });
            for (int i = 0; i < weatherProvider.Items.Count; i++)
            {
                if ((string)((ComboBoxItem)weatherProvider.Items[i]).Tag == _config.WeatherProvider)
                    weatherProvider.SelectedIndex = i;
            }
            if (weatherProvider.SelectedIndex < 0) weatherProvider.SelectedIndex = 0;
            layout.Children.Add(weatherProvider);
            TextBlock providerNote = Text("MET Norway и Open-Meteo не требуют API-ключа для личного некоммерческого использования. Данные имеют отдельную лицензию CC BY 4.0; источники и ссылки на лицензии указаны в окне прогноза. Для коммерческого использования Open-Meteo нужен соответствующий тариф.", 11, MutedBrush);
            providerNote.TextWrapping = TextWrapping.Wrap;
            providerNote.Margin = new Thickness(0, 0, 0, 12);
            layout.Children.Add(providerNote);
            CheckBox tray = new CheckBox
            {
                Content = "При закрытии окна сворачивать в трей", IsChecked = _config.CloseToTrayEnabled,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 12)
            };
            layout.Children.Add(tray);
            TextBlock startupTitle = Text("ЗАПУСК WINDOWS", 11, AccentBrush);
            startupTitle.FontWeight = FontWeights.SemiBold;
            startupTitle.Margin = new Thickness(0, 5, 0, 10);
            layout.Children.Add(startupTitle);
            CheckBox launchAtLogin = new CheckBox
            {
                Content = "Запускать AppShelf при входе в Windows",
                IsChecked = startupState.Enabled,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 9)
            };
            layout.Children.Add(launchAtLogin);
            CheckBox launchHidden = new CheckBox
            {
                Content = "При автозапуске сразу скрывать в трей",
                IsChecked = startupState.InTray, IsEnabled = startupState.Enabled,
                Foreground = WhiteBrush, Margin = new Thickness(16, 0, 0, 8)
            };
            launchAtLogin.Checked += delegate { launchHidden.IsEnabled = true; };
            launchAtLogin.Unchecked += delegate { launchHidden.IsEnabled = false; };
            layout.Children.Add(launchHidden);
            TextBlock startupNote = Text(
                "Автозапуск действует для текущего пользователя Windows и привязан к расположению EXE. " +
                "После перемещения программы нажми «Сохранить», чтобы обновить путь.",
                11, MutedBrush);
            startupNote.TextWrapping = TextWrapping.Wrap;
            startupNote.Margin = new Thickness(0, 0, 0, 13);
            layout.Children.Add(startupNote);

            TextBlock soundsTitle = Text("ЗВУКОВЫЕ УВЕДОМЛЕНИЯ", 11, AccentBrush);
            soundsTitle.FontWeight = FontWeights.SemiBold;
            soundsTitle.Margin = new Thickness(0, 0, 0, 10);
            layout.Children.Add(soundsTitle);
            CheckBox sounds = new CheckBox
            {
                Content = "Включить звуки AppShelf",
                IsChecked = _config.SoundsEnabled != false,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 9)
            };
            layout.Children.Add(sounds);
            CheckBox pomodoroSound = new CheckBox
            {
                Content = "Звук окончания этапа Помодоро",
                IsChecked = _config.PomodoroSoundEnabled != false,
                Foreground = WhiteBrush, Margin = new Thickness(16, 0, 0, 8),
                IsEnabled = sounds.IsChecked == true
            };
            CheckBox reminderSound = new CheckBox
            {
                Content = "Звук напоминаний",
                IsChecked = _config.RemindersSoundEnabled == true,
                Foreground = WhiteBrush, Margin = new Thickness(16, 0, 0, 8),
                IsEnabled = sounds.IsChecked == true
            };
            CheckBox countdownSound = new CheckBox
            {
                Content = "Звук завершения обычного таймера",
                IsChecked = _config.CountdownSoundEnabled != false,
                Foreground = WhiteBrush, Margin = new Thickness(16, 0, 0, 8),
                IsEnabled = sounds.IsChecked == true
            };
            sounds.Checked += delegate { pomodoroSound.IsEnabled = true; reminderSound.IsEnabled = true; countdownSound.IsEnabled = true; };
            sounds.Unchecked += delegate { pomodoroSound.IsEnabled = false; reminderSound.IsEnabled = false; countdownSound.IsEnabled = false; };
            layout.Children.Add(pomodoroSound);
            layout.Children.Add(reminderSound);
            layout.Children.Add(countdownSound);
            TextBlock soundNote = Text(
                "Используются звуки Windows. Громкость и звуки системных уведомлений в трее зависят от настроек Windows.",
                11, MutedBrush);
            soundNote.TextWrapping = TextWrapping.Wrap;
            soundNote.Margin = new Thickness(0, 0, 0, 16);
            layout.Children.Add(soundNote);
            TextBlock note = Text("Ctrl+Alt+Space — показать/скрыть окно. Крестик и Alt+F4 действуют согласно настройке сворачивания в трей. Команда «Выход» в трее всегда завершает AppShelf.", 11, MutedBrush);
            note.TextWrapping = TextWrapping.Wrap;
            layout.Children.Add(note);
            CheckBox groupingSetting = new CheckBox
            {
                Content = "Группировка приложений", IsChecked = _config.GroupingEnabled,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 9)
            };
            TextBlock tileSettingLabel = Text("Режим плиток:", 12, MutedBrush);
            ComboBox tileSetting = new ComboBox { MinHeight = 33, Margin = new Thickness(0, 4, 0, 10) };
            tileSetting.Items.Add(new ComboBoxItem { Content = "Компактный", Tag = "compact" });
            tileSetting.Items.Add(new ComboBoxItem { Content = "Обычный", Tag = "normal" });
            tileSetting.Items.Add(new ComboBoxItem { Content = "Обычный с подписями", Tag = "labeled" });
            string activeTileMode = CurrentTileDisplayMode();
            tileSetting.SelectedIndex = activeTileMode == "compact" ? 0 : activeTileMode == "normal" ? 1 : 2;
            tileSetting.Style = panelMode.Style;
            foreach (ResourceDictionary styles in panelMode.Resources.MergedDictionaries)
                tileSetting.Resources.MergedDictionaries.Add(styles);
            TextBlock tileScaleLabel = Text("Размер плиток:", 12, MutedBrush);
            tileScaleLabel.Margin = new Thickness(0, 0, 0, 2);
            Grid tileScaleRow = new Grid { Height = 27, Margin = new Thickness(0, 0, 0, 9) };
            tileScaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tileScaleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            Slider tileScaleSlider = new Slider
            {
                Minimum = 75, Maximum = 125, TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = FluentUi.NormalizeTileScale(_config.TileScalePercent),
                Height = 26, Margin = new Thickness(3, 0, 11, 0)
            };
            FluentUi.StyleGroupOpacitySlider(tileScaleSlider);
            tileScaleRow.Children.Add(tileScaleSlider);
            TextBlock tileScaleValue = Text(FluentUi.NormalizeTileScale(_config.TileScalePercent)
                + "%", 12, WhiteBrush);
            tileScaleValue.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(tileScaleValue, 1);
            tileScaleRow.Children.Add(tileScaleValue);
            tileScaleSlider.ValueChanged += delegate
            {
                tileScaleValue.Text = FluentUi.NormalizeTileScale((int)Math.Round(tileScaleSlider.Value)) + "%";
            };
            // Keep the existing JSON settings and layout logic; only their editor changes.
            TextBlock groupLayoutLabel = Text("Размещение групп:", 12, MutedBrush);
            groupLayoutLabel.Margin = new Thickness(0, 4, 0, 5);
            StackPanel groupLayoutChoices = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
            string activeLayoutMode = NormalizeGroupLayoutMode(_config.GroupLayoutMode);
            RadioButton groupLayoutList = new RadioButton
            {
                Content = "Список — группы одна под другой", Tag = "GroupLayoutMode:vertical",
                GroupName = "AppShelfSettingsGroupLayout", IsChecked = activeLayoutMode == "vertical",
                Foreground = WhiteBrush, Margin = new Thickness(0, 3, 0, 5)
            };
            RadioButton groupLayoutGrid = new RadioButton
            {
                Content = "Автоматическая сетка — с переносом", Tag = "GroupLayoutMode:grid",
                GroupName = "AppShelfSettingsGroupLayout", IsChecked = activeLayoutMode == "grid",
                Foreground = WhiteBrush, Margin = new Thickness(0, 3, 0, 5)
            };
            RadioButton groupLayoutFree = new RadioButton
            {
                Content = "Свободное размещение — девять зон", Tag = "GroupLayoutMode:free",
                GroupName = "AppShelfSettingsGroupLayout", IsChecked = activeLayoutMode == "free",
                Foreground = WhiteBrush, Margin = new Thickness(0, 3, 0, 5)
            };
            groupLayoutChoices.Children.Add(groupLayoutList);
            groupLayoutChoices.Children.Add(groupLayoutGrid);
            groupLayoutChoices.Children.Add(groupLayoutFree);
            RadioButton groupLayoutCanvas = new RadioButton
            {
                Content = "Свободное полотно", Tag = "GroupLayoutMode:canvas",
                GroupName = "AppShelfSettingsGroupLayout", IsChecked = activeLayoutMode == "canvas",
                Foreground = WhiteBrush, Margin = new Thickness(0, 3, 0, 5)
            };
            groupLayoutChoices.Children.Add(groupLayoutCanvas);
            TextBlock groupModeDescription = Text("", 11, MutedBrush);
            groupModeDescription.TextWrapping = TextWrapping.Wrap;
            groupModeDescription.Margin = new Thickness(3, 0, 0, 11);
            TextBlock canvasStepLabel = Text("Шаг сетки:", 12, MutedBrush);
            ComboBox canvasStepSetting = new ComboBox { MinHeight = 33, Margin = new Thickness(0, 4, 0, 10),
                Style = tileSetting.Style };
            foreach (ResourceDictionary styles in tileSetting.Resources.MergedDictionaries)
                canvasStepSetting.Resources.MergedDictionaries.Add(styles);
            foreach (int step in new[] { 10, 20, 30, 40 })
                canvasStepSetting.Items.Add(new ComboBoxItem { Content = step + " DIP", Tag = step,
                    IsSelected = step == FreeCanvasLayout.NormalizeStep(_config.CanvasGridStep) });
            TextBlock canvasViewportLabel = Text("При уменьшении окна:", 12, MutedBrush);
            ComboBox canvasViewportSetting = new ComboBox { MinHeight = 33, Margin = new Thickness(0, 4, 0, 10),
                Style = tileSetting.Style };
            foreach (ResourceDictionary styles in tileSetting.Resources.MergedDictionaries)
                canvasViewportSetting.Resources.MergedDictionaries.Add(styles);
            string viewportMode = FreeCanvasLayout.NormalizeViewportMode(_config.CanvasViewportMode);
            string[] viewportKeys = { "adaptive", "scale", "collapse", "scroll" };
            string[] viewportTitles = { "Адаптивная расстановка", "Масштабирование (не ниже 75%)", "Сворачивание групп", "Прокрутка полотна" };
            for (int i = 0; i < viewportKeys.Length; i++)
                canvasViewportSetting.Items.Add(new ComboBoxItem { Content = viewportTitles[i], Tag = viewportKeys[i], IsSelected = viewportMode == viewportKeys[i] });

            TextBlock groupWidthLabel = Text("Ширина групп:", 12, MutedBrush);
            WrapPanel groupWidthChoices = new WrapPanel { Margin = new Thickness(4, 5, 0, 9) };
            bool contentWidthSelected = NormalizeGroupWidthMode(_config.GroupWidthMode) == "content";
            RadioButton groupWidthFull = new RadioButton
            {
                Content = "На всю ширину", Tag = "GroupWidthMode:full",
                GroupName = "AppShelfSettingsGroupWidth", IsChecked = !contentWidthSelected,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 16, 6)
            };
            RadioButton groupWidthContent = new RadioButton
            {
                Content = "По содержимому", Tag = "GroupWidthMode:content",
                GroupName = "AppShelfSettingsGroupWidth", IsChecked = contentWidthSelected,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 12, 6)
            };
            groupWidthChoices.Children.Add(groupWidthFull);
            groupWidthChoices.Children.Add(groupWidthContent);

            TextBlock groupAlignmentLabel = Text("Выравнивание групп:", 12, MutedBrush);
            ComboBox groupAlignmentSetting = new ComboBox
            {
                MinHeight = 33, Margin = new Thickness(0, 4, 0, 10),
                Style = tileSetting.Style
            };
            groupAlignmentSetting.Items.Add(new ComboBoxItem { Content = "По левому краю", Tag = "left" });
            groupAlignmentSetting.Items.Add(new ComboBoxItem { Content = "По центру", Tag = "center" });
            groupAlignmentSetting.Items.Add(new ComboBoxItem { Content = "По правому краю", Tag = "right" });
            groupAlignmentSetting.Items.Add(new ComboBoxItem { Content = "Чередование слева и справа", Tag = "alternate" });
            string activeGroupAlignment = NormalizeGroupAlignment(_config.GroupAlignment);
            groupAlignmentSetting.SelectedIndex = activeGroupAlignment == "center" ? 1 :
                activeGroupAlignment == "right" ? 2 : activeGroupAlignment == "alternate" ? 3 : 0;
            foreach (ResourceDictionary styles in tileSetting.Resources.MergedDictionaries)
                groupAlignmentSetting.Resources.MergedDictionaries.Add(styles);
            groupAlignmentSetting.Background = FluentUi.Brush("#101A2A");
            groupAlignmentSetting.BorderBrush = FluentUi.Brush("#34465F");
            groupAlignmentSetting.FontWeight = FontWeights.Normal;
            TextBlock groupLayoutHint = Text(
                "Для отдельных групп выравнивание задаётся через правую кнопку на заголовке.",
                11, MutedBrush);
            groupLayoutHint.TextWrapping = TextWrapping.Wrap;
            groupLayoutHint.Margin = new Thickness(0, 0, 0, 10);

            TextBlock groupDefaultZoneLabel = Text("Положение новых групп по умолчанию:", 12, MutedBrush);
            groupDefaultZoneLabel.Margin = new Thickness(0, 0, 0, 5);
            System.Windows.Controls.Primitives.UniformGrid groupZonePicker =
                new System.Windows.Controls.Primitives.UniformGrid
                {
                    Columns = 3, Rows = 3, MaxWidth = 255,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 5), Tag = "GroupZonePicker"
                };
            string selectedGroupZone = NormalizeGroupZone(_config.GroupDefaultZone);
            string[] zoneSymbols = { "↖", "↑", "↗", "←", "●", "→", "↙", "↓", "↘" };
            List<Button> zoneButtons = new List<Button>();
            for (int i = 0; i < FreeGroupZones.Length; i++)
            {
                string chosenZone = FreeGroupZones[i];
                Button zoneButton = ActionButton(zoneSymbols[i], false);
                zoneButton.Tag = "GroupZone:" + chosenZone;
                zoneButton.ToolTip = FreeGroupZoneLabel(chosenZone);
                zoneButton.FontSize = 18;
                zoneButton.Margin = new Thickness(3);
                zoneButton.Padding = new Thickness(0);
                zoneButton.MinHeight = 38;
                zoneButton.Height = 40;
                zoneButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                System.Windows.Automation.AutomationProperties.SetName(zoneButton,
                    "Зона размещения: " + FreeGroupZoneLabel(chosenZone));
                zoneButtons.Add(zoneButton);
                groupZonePicker.Children.Add(zoneButton);
            }
            TextBlock groupZoneSelection = Text("", 11, WhiteBrush);
            groupZoneSelection.Margin = new Thickness(3, 0, 0, 8);
            Action refreshZonePicker = delegate
            {
                foreach (Button button in zoneButtons)
                {
                    bool selected = String.Equals(
                        ((string)button.Tag).Substring("GroupZone:".Length),
                        selectedGroupZone, StringComparison.Ordinal);
                    button.Background = selected ? FluentUi.Brush("#244B65") : TileBrush;
                    button.BorderBrush = selected ? AccentBrush : StrokeBrush;
                    button.BorderThickness = new Thickness(selected ? 2 : 1);
                }
                groupZoneSelection.Text = "Выбрана зона: " + FreeGroupZoneLabel(selectedGroupZone);
            };
            foreach (Button button in zoneButtons)
            {
                Button selectedButton = button;
                selectedButton.Click += delegate
                {
                    selectedGroupZone = ((string)selectedButton.Tag).Substring("GroupZone:".Length);
                    refreshZonePicker();
                };
            }
            refreshZonePicker();
            TextBlock freeGroupHint = Text(
                "Перетаскивай группы мышью или выбирай зону через меню заголовка. " +
                "Общее положение также действует для групп с правилом «По общему правилу».",
                11, MutedBrush);
            freeGroupHint.TextWrapping = TextWrapping.Wrap;
            freeGroupHint.Margin = new Thickness(0, 0, 0, 10);

            Action refreshGroupLayoutControls = delegate
            {
                bool grouped = groupingSetting.IsChecked == true;
                bool list = groupLayoutList.IsChecked == true;
                bool grid = groupLayoutGrid.IsChecked == true;
                bool free = groupLayoutFree.IsChecked == true;
                bool contentWidth = groupWidthContent.IsChecked == true;
                Visibility basic = grouped ? Visibility.Visible : Visibility.Collapsed;
                Visibility listOnly = grouped && list ? Visibility.Visible : Visibility.Collapsed;
                Visibility alignmentOnly = grouped && list && contentWidth
                    ? Visibility.Visible : Visibility.Collapsed;
                Visibility freeOnly = grouped && free ? Visibility.Visible : Visibility.Collapsed;
                groupLayoutLabel.Visibility = basic;
                groupLayoutChoices.Visibility = basic;
                groupModeDescription.Visibility = basic;
                canvasViewportLabel.Visibility = canvasViewportSetting.Visibility = canvasStepLabel.Visibility = canvasStepSetting.Visibility = grouped && groupLayoutCanvas.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                groupModeDescription.Text = groupLayoutCanvas.IsChecked == true
                    ? "Общая сетка групп и виджетов; свободное перемещение без пересечений."
                    : free
                    ? "Размещай группы независимо по девяти зонам рабочего пространства."
                    : grid
                        ? "Ширина рассчитывается автоматически; группы переносятся на следующую строку."
                        : "Группы располагаются последовательно сверху вниз.";
                groupWidthLabel.Visibility = listOnly;
                groupWidthChoices.Visibility = listOnly;
                groupAlignmentLabel.Visibility = alignmentOnly;
                groupAlignmentSetting.Visibility = alignmentOnly;
                groupLayoutHint.Visibility = alignmentOnly;
                groupDefaultZoneLabel.Visibility = freeOnly;
                groupZonePicker.Visibility = freeOnly;
                groupZoneSelection.Visibility = freeOnly;
                freeGroupHint.Visibility = freeOnly;
            };
            groupingSetting.Checked += delegate { refreshGroupLayoutControls(); };
            groupingSetting.Unchecked += delegate { refreshGroupLayoutControls(); };
            groupLayoutList.Checked += delegate { refreshGroupLayoutControls(); };
            groupLayoutGrid.Checked += delegate { refreshGroupLayoutControls(); };
            groupLayoutFree.Checked += delegate { refreshGroupLayoutControls(); };
            groupLayoutCanvas.Checked += delegate { refreshGroupLayoutControls(); };
            groupWidthFull.Checked += delegate { refreshGroupLayoutControls(); };
            groupWidthContent.Checked += delegate { refreshGroupLayoutControls(); };
            refreshGroupLayoutControls();

            TextBlock backgroundSettingLabel = Text("Фон главного окна:", 12, MutedBrush);
            ComboBox backgroundSetting = new ComboBox { MinHeight = 33, Margin = new Thickness(0, 4, 0, 10) };
            backgroundSetting.Items.Add(new ComboBoxItem { Content = "Синие дуги", Tag = "blue-arcs" });
            backgroundSetting.Items.Add(new ComboBoxItem { Content = "Тёмная ночь", Tag = "midnight" });
            backgroundSetting.Items.Add(new ComboBoxItem { Content = "Графит", Tag = "graphite" });
            backgroundSetting.Items.Add(new ComboBoxItem { Content = "Классический", Tag = "classic" });
            foreach (string customPreset in CustomBackgroundStore.ListPresets())
                backgroundSetting.Items.Add(new ComboBoxItem
                {
                    Content = CustomBackgroundStore.DisplayName(customPreset),
                    Tag = customPreset
                });
            string activeBackgroundPreset = FluentUi.NormalizeMainBackground(_config.MainBackgroundPreset);
            backgroundSetting.SelectedIndex = 0;
            for (int option = 0; option < backgroundSetting.Items.Count; option++)
            {
                ComboBoxItem backgroundOption = backgroundSetting.Items[option] as ComboBoxItem;
                if (backgroundOption != null &&
                    String.Equals(backgroundOption.Tag as string, activeBackgroundPreset, StringComparison.Ordinal))
                {
                    backgroundSetting.SelectedIndex = option;
                    break;
                }
            }
            backgroundSetting.Style = panelMode.Style;
            foreach (ResourceDictionary styles in panelMode.Resources.MergedDictionaries)
                backgroundSetting.Resources.MergedDictionaries.Add(styles);
            // Match the subdued controls in the selected design B, scoped to this section.
            foreach (ComboBox field in new ComboBox[] { tileSetting, backgroundSetting })
            {
                field.Background = FluentUi.Brush("#101A2A");
                field.BorderBrush = FluentUi.Brush("#34465F");
                field.FontWeight = FontWeights.Normal;
            }
            // Imported images are kept outside the EXE. Until Save succeeds, all
            // additions/deletions are staged so Cancel cannot change the library.
            List<string> addedBackgrounds = new List<string>();
            List<string> removedBackgrounds = new List<string>();
            StackPanel backgroundActions = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10)
            };
            Button addBackgroundButton = ActionButton("Добавить фон…", false);
            Button removeBackgroundButton = ActionButton("Удалить фон", false);
            addBackgroundButton.IsEnabled = _canSave;
            removeBackgroundButton.IsEnabled = _canSave &&
                CustomBackgroundStore.IsCustomPreset(activeBackgroundPreset);
            removeBackgroundButton.Margin = new Thickness(9, 0, 0, 0);
            backgroundActions.Children.Add(addBackgroundButton);
            backgroundActions.Children.Add(removeBackgroundButton);
            Action refreshBackgroundActions = delegate
            {
                ComboBoxItem selected = backgroundSetting.SelectedItem as ComboBoxItem;
                removeBackgroundButton.IsEnabled = _canSave && selected != null &&
                    CustomBackgroundStore.IsCustomPreset(selected.Tag as string);
            };
            addBackgroundButton.Click += delegate
            {
                OpenFileDialog picker = new OpenFileDialog
                {
                    Title = "Добавить собственный фон AppShelf",
                    Filter = "Изображения (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
                    Multiselect = false, CheckFileExists = true
                };
                if (picker.ShowDialog(dialog) != true) return;
                try
                {
                    string preset = CustomBackgroundStore.Import(picker.FileName);
                    addedBackgrounds.Add(preset);
                    ComboBoxItem choice = new ComboBoxItem
                    {
                        Content = CustomBackgroundStore.DisplayName(preset),
                        Tag = preset
                    };
                    backgroundSetting.Items.Add(choice);
                    backgroundSetting.SelectedItem = choice;
                    refreshBackgroundActions();
                }
                catch (Exception ex) { ShowError("Не удалось добавить фон.", ex); }
            };
            removeBackgroundButton.Click += delegate
            {
                ComboBoxItem selected = backgroundSetting.SelectedItem as ComboBoxItem;
                if (selected == null) return;
                string preset = selected.Tag as string;
                if (!CustomBackgroundStore.IsCustomPreset(preset)) return;
                if (MessageBox.Show(dialog,
                    "Удалить пользовательский фон «" + CustomBackgroundStore.DisplayName(preset) +
                    "»? Изменение вступит в силу после сохранения настроек.",
                    "AppShelf — удалить фон", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (addedBackgrounds.Contains(preset))
                {
                    try
                    {
                        CustomBackgroundStore.Delete(preset);
                        addedBackgrounds.Remove(preset);
                    }
                    catch (Exception ex)
                    {
                        ShowError("Не удалось удалить фон.", ex);
                        return;
                    }
                }
                else removedBackgrounds.Add(preset);
                backgroundSetting.Items.Remove(selected);
                backgroundSetting.SelectedIndex = 0;
                refreshBackgroundActions();
            };
            backgroundSetting.SelectionChanged += delegate { refreshBackgroundActions(); };
            refreshBackgroundActions();

            TextBlock bottomOpacityLabel = Text("Непрозрачность нижней панели:", 12, MutedBrush);
            bottomOpacityLabel.Margin = new Thickness(0, 0, 0, 2);
            Grid bottomOpacityRow = new Grid { Height = 27, Margin = new Thickness(0, 0, 0, 11) };
            bottomOpacityRow.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomOpacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            Slider bottomOpacitySlider = new Slider
            {
                Minimum = 0, Maximum = 100, TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = NormalizeBottomPanelOpacity(_config.BottomPanelOpacity),
                Height = 26, Margin = new Thickness(3, 0, 11, 0)
            };
            FluentUi.StyleGroupOpacitySlider(bottomOpacitySlider);
            bottomOpacityRow.Children.Add(bottomOpacitySlider);
            TextBlock bottomOpacityValue = Text(
                NormalizeBottomPanelOpacity(_config.BottomPanelOpacity) + "%", 12, WhiteBrush);
            bottomOpacityValue.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(bottomOpacityValue, 1);
            bottomOpacityRow.Children.Add(bottomOpacityValue);
            bottomOpacitySlider.ValueChanged += delegate
            {
                int value = (int)Math.Round(bottomOpacitySlider.Value);
                bottomOpacityValue.Text = value + "%";
                if (dialog.IsVisible) ApplyBottomPanelOpacity(value);
            };

            TextBlock groupOpacityLabel = Text("Непрозрачность групп:", 12, MutedBrush);
            groupOpacityLabel.Margin = new Thickness(0, 0, 0, 2);
            Grid opacityRow = new Grid { Height = 27, Margin = new Thickness(0, 0, 0, 6) };
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            Slider groupOpacitySlider = new Slider
            {
                Minimum = 50, Maximum = 100, TickFrequency = 1,
                IsSnapToTickEnabled = true, Value = FluentUi.NormalizeGroupOpacity(_config.GroupOpacity),
                Height = 26, Margin = new Thickness(3, 0, 11, 0)
            };
            FluentUi.StyleGroupOpacitySlider(groupOpacitySlider);
            opacityRow.Children.Add(groupOpacitySlider);
            TextBlock opacityValue = Text(
                FluentUi.NormalizeGroupOpacity(_config.GroupOpacity) + "%", 12, WhiteBrush);
            opacityValue.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(opacityValue, 1);
            opacityRow.Children.Add(opacityValue);

            // Compact in-dialog preview: real application icons and the exact group
            // brushes. This remains visible even when Settings covers the main window.
            TextBlock previewCaption = Text("Предпросмотр группы", 11, MutedBrush);
            previewCaption.Margin = new Thickness(0, 4, 0, 5);
            Border previewBackdrop = new Border
            {
                Tag = "GroupOpacityPreviewBackdrop",
                Height = 104, CornerRadius = new CornerRadius(10),
                ClipToBounds = true, Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 9)
            };
            Border previewGroup = new Border
            {
                Tag = "GroupOpacityPreviewCard",
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1), Padding = new Thickness(8)
            };
            previewBackdrop.Child = previewGroup;
            StackPanel previewContents = new StackPanel();
            previewGroup.Child = previewContents;
            string sampleGroup = _config.Groups.Count > 0 ? _config.Groups[0] : "Приложения";
            TextBlock previewTitle = Text(sampleGroup, 11, WhiteBrush);
            previewTitle.FontWeight = FontWeights.Medium;
            previewContents.Children.Add(previewTitle);
            StackPanel previewIcons = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0)
            };
            previewContents.Children.Add(previewIcons);
            int samples = 0;
            foreach (AppItem app in _config.Items)
            {
                if (samples >= 3) break;
                if (_config.Groups.Count > 0 &&
                    !String.Equals(app.Group ?? "", sampleGroup, StringComparison.OrdinalIgnoreCase))
                    continue;
                ImageSource appIcon = GetIcon(app.Path);
                if (appIcon == null) continue;
                Border sampleTile = new Border
                {
                    Width = 39, Height = 39, Margin = new Thickness(0, 0, 5, 0),
                    Background = TileBrush, BorderBrush = StrokeBrush,
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                    Child = new Image { Source = appIcon, Width = 30, Height = 30,
                        Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center }
                };
                previewIcons.Children.Add(sampleTile);
                samples++;
            }
            if (samples == 0)
                previewIcons.Children.Add(FluentUi.MenuGlyph("folder"));
            Action refreshTilePreview = delegate
            {
                double factor = FluentUi.NormalizeTileScale((int)Math.Round(tileScaleSlider.Value)) / 100.0;
                foreach (UIElement entry in previewIcons.Children)
                {
                    Border tile = entry as Border;
                    if (tile == null) continue;
                    tile.Width = Math.Round(39 * factor);
                    tile.Height = Math.Round(39 * factor);
                    Image img = tile.Child as Image;
                    if (img != null)
                    {
                        img.Width = Math.Round(30 * factor);
                        img.Height = Math.Round(30 * factor);
                    }
                }
            };
            tileScaleSlider.ValueChanged += delegate { refreshTilePreview(); };
            refreshTilePreview();

            // Also update the actual groups; no mock-only preview or settings writes.
            // Original appearance is restored on close, including Cancel/failed Save.
            Brush previousMainBackground = _mainBackgroundHost == null
                ? null : _mainBackgroundHost.Background;
            Action refreshGroupPreview = delegate
            {
                int opacity = FluentUi.NormalizeGroupOpacity((int)Math.Round(groupOpacitySlider.Value));
                opacityValue.Text = opacity + "%";
                ComboBoxItem selectedBackground = backgroundSetting.SelectedItem as ComboBoxItem;
                string preset = selectedBackground == null ? activeBackgroundPreset :
                    (string)selectedBackground.Tag;
                previewBackdrop.Background = FluentUi.MainWindowBackground(preset, 360, 104);
                previewGroup.Background = FluentUi.MainGroupBackground(preset, opacity);
                previewGroup.BorderBrush = FluentUi.MainGroupBorder(preset);
                if (!dialog.IsVisible) return;
                ApplyMainBackground(preset);
                ApplyGroupAppearancePreview(preset, opacity);
            };
            groupOpacitySlider.ValueChanged += delegate { refreshGroupPreview(); };
            backgroundSetting.SelectionChanged += delegate { refreshGroupPreview(); };
            refreshGroupPreview();
            CheckBox topmostSetting = new CheckBox
            {
                Content = "Поверх остальных окон", IsChecked = _config.AlwaysOnTop,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 5)
            };
            CheckBox collectionsSetting = new CheckBox
            {
                Content = "Включить модуль «Подборки»",
                IsChecked = _config.ShowCollections != false,
                Foreground = WhiteBrush, Margin = new Thickness(0, 0, 0, 10)
            };
            string pendingZipRestore = null;
            Button backupSettings = ActionButton("Экспорт / импорт данных…", false);
            backupSettings.HorizontalAlignment = HorizontalAlignment.Left;
            backupSettings.Click += delegate
            {
                ShowBackupMenuWindow(dialog, delegate(string filename)
                {
                    pendingZipRestore = filename;
                    dialog.DialogResult = false;
                });
            };

            List<UIElement> originalSettings = new List<UIElement>();
            foreach (UIElement control in layout.Children) originalSettings.Add(control);
            layout.Children.Clear();
            layout.Children.Add(settingsTitle);
            layout.Children.Add(settingsHint);
            StackPanel expandActions = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 10) };
            layout.Children.Add(expandActions);
            StackPanel accordionRows = new StackPanel();
            Border accordionFrame = new Border
            {
                Tag = "SettingsAccordionFrame",
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                BorderBrush = FluentUi.Brush("#344A66"),
                Background = FluentUi.Brush("#111A29"),
                Padding = new Thickness(6),
                Child = accordionRows
            };
            layout.Children.Add(accordionFrame);
            Dictionary<string, Border> sections = new Dictionary<string, Border>(StringComparer.Ordinal);
            Dictionary<string, System.Windows.Shapes.Path> arrows =
                new Dictionary<string, System.Windows.Shapes.Path>(StringComparer.Ordinal);
            Dictionary<string, Border> headers = new Dictionary<string, Border>(StringComparer.Ordinal);
            LinearGradientBrush selectedHeading = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#2B4162"),
                (Color)ColorConverter.ConvertFromString("#263A59"), 0);
            selectedHeading.Freeze();
            Action refreshHeadings = delegate
            {
                foreach (KeyValuePair<string, Border> part in sections)
                    headers[part.Key].Background = part.Value.Visibility == Visibility.Visible
                        ? (Brush)selectedHeading : Brushes.Transparent;
            };
            // Old profiles used a separate main "panel" section. Keep the
            // user's last destination, now nested under "appearance".
            string savedSection = _config.SettingsLastSection;
            string lastAppearance = savedSection == "panel" || savedSection == "appearance-panel"
                ? "appearance-panel" : savedSection == "appearance-background"
                ? "appearance-background" : "appearance-tiles";
            string lastSection = savedSection == "panel" ||
                savedSection == "appearance-panel" ||
                savedSection == "appearance-background" || savedSection == "appearance-tiles"
                ? "appearance" : savedSection;
            if (lastSection != "network" && lastSection != "weather" &&
                lastSection != "time" && lastSection != "windows" &&
                lastSection != "appearance" && lastSection != "data" && lastSection != "collections" && lastSection != "plugins")
                lastSection = "appearance";
            if (requestedSection == "appearance-panel")
            {
                lastSection = "appearance";
                lastAppearance = "appearance-panel";
            }
            else if (requestedSection == "network" || requestedSection == "weather" ||
                requestedSection == "time" || requestedSection == "windows" ||
                requestedSection == "appearance" || requestedSection == "data" || requestedSection == "collections" || requestedSection == "plugins")
                lastSection = requestedSection;
            Action<string> toggleSection = delegate(string key)
            {
                bool expand = sections[key].Visibility != Visibility.Visible;
                foreach (KeyValuePair<string, Border> part in sections)
                {
                    part.Value.Visibility = Visibility.Collapsed;
                    SetSettingsArrow(arrows[part.Key], false);
                }
                if (expand)
                {
                    sections[key].Visibility = Visibility.Visible;
                    SetSettingsArrow(arrows[key], true);
                    lastSection = key;
                }
                refreshHeadings();
            };

            // A separate accordion inside "Appearance" keeps controls easy to
            // scan without restoring the old standalone main "Bottom panel".
            Dictionary<string, Border> appearanceBodies =
                new Dictionary<string, Border>(StringComparer.Ordinal);
            Dictionary<string, System.Windows.Shapes.Path> appearanceArrows =
                new Dictionary<string, System.Windows.Shapes.Path>(StringComparer.Ordinal);
            Dictionary<string, Border> appearanceHeaders =
                new Dictionary<string, Border>(StringComparer.Ordinal);
            LinearGradientBrush selectedSubheading = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#223751"),
                (Color)ColorConverter.ConvertFromString("#1D314A"), 0);
            selectedSubheading.Freeze();
            Action refreshAppearanceHeadings = delegate
            {
                foreach (KeyValuePair<string, Border> part in appearanceBodies)
                    appearanceHeaders[part.Key].Background =
                        part.Value.Visibility == Visibility.Visible
                            ? (Brush)selectedSubheading : Brushes.Transparent;
            };
            Action<string> toggleAppearance = delegate(string key)
            {
                bool expand = appearanceBodies[key].Visibility != Visibility.Visible;
                foreach (KeyValuePair<string, Border> part in appearanceBodies)
                {
                    part.Value.Visibility = Visibility.Collapsed;
                    SetSettingsArrow(appearanceArrows[part.Key], false);
                }
                if (expand)
                {
                    appearanceBodies[key].Visibility = Visibility.Visible;
                    SetSettingsArrow(appearanceArrows[key], true);
                    lastAppearance = key;
                }
                refreshAppearanceHeadings();
            };
            StackPanel appearanceRows = new StackPanel();
            Border appearanceFrame = new Border
            {
                Tag = "SettingsAppearanceSubsectionsFrame",
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = FluentUi.Brush("#344A66"),
                Background = FluentUi.Brush("#152135"),
                Padding = new Thickness(4),
                Child = appearanceRows
            };

            AddSettingsSection(appearanceRows, "appearance-tiles", "Плитки и группы",
                new List<UIElement> { groupingSetting, tileSettingLabel, tileSetting,
                    tileScaleLabel, tileScaleRow,
                    groupLayoutLabel, groupLayoutChoices, groupModeDescription, canvasStepLabel, canvasStepSetting, canvasViewportLabel, canvasViewportSetting,
                    groupWidthLabel, groupWidthChoices, groupAlignmentLabel, groupAlignmentSetting,
                    groupLayoutHint, groupDefaultZoneLabel, groupZonePicker, groupZoneSelection,
                    freeGroupHint,
                    groupOpacityLabel, opacityRow, previewCaption, previewBackdrop },
                appearanceBodies, appearanceArrows, appearanceHeaders, toggleAppearance);
            AddSettingsSection(appearanceRows, "appearance-background", "Фон главного окна",
                new List<UIElement> { backgroundSettingLabel, backgroundSetting, backgroundActions },
                appearanceBodies, appearanceArrows, appearanceHeaders, toggleAppearance);

            // The previous section title is redundant under the nested heading.
            // The same controls are moved, not recreated, to preserve handlers.
            List<UIElement> bottomPanelItems =
                SettingsRange(originalSettings, panelTitle, favoriteActions);
            bottomPanelItems.RemoveAt(0);
            bottomPanelItems.Add(bottomOpacityLabel);
            bottomPanelItems.Add(bottomOpacityRow);
            AddSettingsSection(appearanceRows, "appearance-panel", "Нижняя панель",
                bottomPanelItems, appearanceBodies, appearanceArrows,
                appearanceHeaders, toggleAppearance);

            appearanceBodies[lastAppearance].Visibility = Visibility.Visible;
            SetSettingsArrow(appearanceArrows[lastAppearance], true);
            refreshAppearanceHeadings();

            Button expandAll = ActionButton("Развернуть всё", false);
            expandAll.Click += delegate
            {
                foreach (KeyValuePair<string, Border> item in sections)
                {
                    item.Value.Visibility = Visibility.Visible;
                    SetSettingsArrow(arrows[item.Key], true);
                }
                foreach (KeyValuePair<string, Border> item in appearanceBodies)
                {
                    item.Value.Visibility = Visibility.Visible;
                    SetSettingsArrow(appearanceArrows[item.Key], true);
                }
                refreshHeadings();
                refreshAppearanceHeadings();
            };
            expandActions.Children.Add(expandAll);
            Button collapseAll = ActionButton("Свернуть всё", false);
            collapseAll.Click += delegate
            {
                foreach (KeyValuePair<string, Border> item in sections)
                {
                    item.Value.Visibility = Visibility.Collapsed;
                    SetSettingsArrow(arrows[item.Key], false);
                }
                foreach (KeyValuePair<string, Border> item in appearanceBodies)
                {
                    item.Value.Visibility = Visibility.Collapsed;
                    SetSettingsArrow(appearanceArrows[item.Key], false);
                }
                refreshHeadings();
                refreshAppearanceHeadings();
            };
            expandActions.Children.Add(collapseAll);

            // Appearance replaces the former first-level Bottom panel entry.
            AddSettingsSection(accordionRows, "appearance", "Внешний вид",
                new List<UIElement> { appearanceFrame },
                sections, arrows, headers, toggleSection);
            AddSettingsSection(accordionRows, "network", "Сеть",
                new List<UIElement> { network, networkChecksSettings },
                sections, arrows, headers, toggleSection);
            List<UIElement> weatherItems = new List<UIElement> { weather };
            weatherItems.AddRange(SettingsRange(originalSettings, weatherTitle, providerNote));
            AddSettingsSection(accordionRows, "weather", "Погода",
                weatherItems, sections, arrows, headers, toggleSection);
            List<UIElement> timeItems = new List<UIElement> { pomodoro, countdown };
            timeItems.AddRange(SettingsRange(originalSettings, soundsTitle, soundNote));
            AddSettingsSection(accordionRows, "time", "Таймеры и напоминания",
                timeItems, sections, arrows, headers, toggleSection);
            List<UIElement> windowsItems = SettingsRange(originalSettings, sessionTitle, rdpNote);
            windowsItems.Add(tray);
            windowsItems.AddRange(SettingsRange(originalSettings, startupTitle, startupNote));
            windowsItems.Add(topmostSetting);
            windowsItems.Add(note);
            AddSettingsSection(accordionRows, "windows", "Поведение Windows",
                windowsItems, sections, arrows, headers, toggleSection);
            AddSettingsSection(accordionRows, "collections", "Подборки",
                new List<UIElement> { collectionsSetting }, sections, arrows, headers, toggleSection);
            Button pluginSettings = ActionButton("Управление плагинами…", true);
            pluginSettings.Click += delegate { ShowPluginManager(dialog); };
            TextBlock pluginHint = Text("Установка, обновление, удаление и резервирование данных плагинов.", 12, MutedBrush);
            pluginHint.TextWrapping = TextWrapping.Wrap;
            AddSettingsSection(accordionRows, "plugins", "Плагины",
                new List<UIElement> { pluginSettings, pluginHint }, sections, arrows, headers, toggleSection);
            AddSettingsSection(accordionRows, "data", "Резервные копии",
                new List<UIElement> { backupSettings },
                sections, arrows, headers, toggleSection);
            sections[lastSection].Visibility = Visibility.Visible;
            SetSettingsArrow(arrows[lastSection], true);
            refreshHeadings();

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(22, 9, 13, 12)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            settingsRoot.Children.Insert(0, buttons);
            Button ok = ActionButton("Сохранить", true);
            ok.IsDefault = true;
            ok.IsEnabled = _canSave;
            ok.Click += delegate
            {
                string firstMode = (string)((ComboBoxItem)networkMode1.SelectedItem).Tag;
                string secondMode = (string)((ComboBoxItem)networkMode2.SelectedItem).Tag;
                NetworkChecks.Target validated; string error;
                if (!NetworkChecks.TryParse(firstMode, networkAddress1.Text, 1,
                    out validated, out error))
                {
                    MessageBox.Show(dialog, "Проверка 1: " + error,
                        "AppShelf — настройки сети", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!NetworkChecks.TryParse(secondMode, networkAddress2.Text, 2,
                    out validated, out error))
                {
                    MessageBox.Show(dialog, "Проверка 2: " + error,
                        "AppShelf — настройки сети", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                dialog.DialogResult = true;
            };
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            bool? savedSettings = dialog.ShowDialog();
            // The existing navigation preference holds the last Appearance
            // subsection too, without introducing another apps.json field.
            // "panel" remains the canonical value for the historical panel view.
            string selectedSettingsSection = lastSection == "appearance"
                ? lastAppearance == "appearance-panel" ? "panel"
                    : lastAppearance == "appearance-background" ? "appearance-background"
                    : "appearance"
                : lastSection;
            // Restore before applying the saved configuration; Cancel and failed saves
            // must never leave preview-only changes on the main window.
            if (_mainBackgroundHost != null)
                _mainBackgroundHost.Background = previousMainBackground;
            ApplyBottomPanelOpacity(NormalizeBottomPanelOpacity(_config.BottomPanelOpacity));
            ApplyGroupAppearancePreview(_config.MainBackgroundPreset,
                FluentUi.NormalizeGroupOpacity(_config.GroupOpacity));
            bool backgroundsSaved = false;
            try
            {
            if (savedSettings == true && pendingZipRestore == null)
            {
                bool changedCity = !String.Equals(_config.WeatherCity, city.Text.Trim(), StringComparison.Ordinal);
                string selectedProvider = (string)((ComboBoxItem)weatherProvider.SelectedItem).Tag;
                bool changedProvider = _config.WeatherProvider != selectedProvider;
                if (rdp.IsChecked == true && !_config.RdpKeepAwake)
                {
                    if (MessageBox.Show(this,
                        "AppShelf будет отправлять F15 при бездействии только если работает внутри RDP-сеанса. " +
                        "Это может влиять на приложения и не гарантирует отмену серверных ограничений. Включить?",
                        "AppShelf — антипростой RDP", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                        rdp.IsChecked = false;
                }
                AppConfig pending = _config.Copy();
                pending.WeatherProvider = selectedProvider;
                if (!pending.NetworkPluginMigrated)
                {
                pending.ShowNetwork = network.IsChecked == true;
                pending.NetworkCheck1Mode = (string)((ComboBoxItem)networkMode1.SelectedItem).Tag == "default"
                    ? null : (string)((ComboBoxItem)networkMode1.SelectedItem).Tag;
                pending.NetworkCheck1Target = pending.NetworkCheck1Mode == null
                    ? null : networkAddress1.Text.Trim();
                pending.NetworkCheck2Mode = (string)((ComboBoxItem)networkMode2.SelectedItem).Tag == "default"
                    ? null : (string)((ComboBoxItem)networkMode2.SelectedItem).Tag;
                pending.NetworkCheck2Target = pending.NetworkCheck2Mode == null
                    ? null : networkAddress2.Text.Trim();
                }
                pending.BottomPanelMode = (string)((ComboBoxItem)panelMode.SelectedItem).Tag;
                pending.BottomPanelCategories = new List<string>();
                foreach (string category in new string[] { "network", "weather", "time" })
                    if (categoryOptions[category].IsChecked == true)
                        pending.BottomPanelCategories.Add(category);
                pending.BottomPanelFavorites = new List<string>();
                foreach (ListBoxItem row in favoriteList.Items)
                {
                    CheckBox check = row.Content as CheckBox;
                    if (check != null && check.IsChecked == true)
                        pending.BottomPanelFavorites.Add((string)check.Tag);
                }
                pending.ShowWeather = weather.IsChecked == true;
                pending.WeatherCity = city.Text.Trim();
                pending.CloseToTray = tray.IsChecked == true;
                pending.ShowPomodoro = pomodoro.IsChecked == true;
                pending.ShowCountdownTimer = countdown.IsChecked == true;
                pending.ShowCollections = collectionsSetting.IsChecked == true;
                pending.RdpKeepAwake = rdp.IsChecked == true;
                pending.SettingsLastSection = selectedSettingsSection;
                pending.GroupingEnabled = groupingSetting.IsChecked == true;
                pending.GroupLayoutMode = groupLayoutCanvas.IsChecked == true ? "canvas" : groupLayoutFree.IsChecked == true ? "free" :
                    groupLayoutGrid.IsChecked == true ? "grid" : "vertical";
                pending.CanvasViewportMode = (string)((ComboBoxItem)canvasViewportSetting.SelectedItem).Tag;
                int newCanvasStep = (int)((ComboBoxItem)canvasStepSetting.SelectedItem).Tag;
                int previousCanvasStep = FreeCanvasLayout.NormalizeStep(pending.CanvasGridStep);
                if (newCanvasStep != previousCanvasStep)
                {
                    if (pending.CanvasPositions != null)
                        foreach (string key in new List<string>(pending.CanvasPositions.Keys))
                            pending.CanvasPositions[key] = FreeCanvasLayout.ConvertStep(pending.CanvasPositions[key], previousCanvasStep, newCanvasStep);
                    pending.CanvasGridStep = newCanvasStep;
                }
                MigrateCanvasLayout(pending);
                pending.GroupWidthMode = groupWidthContent.IsChecked == true ? "content" : "full";
                pending.GroupAlignment = (string)((ComboBoxItem)groupAlignmentSetting.SelectedItem).Tag;
                pending.GroupDefaultZone = selectedGroupZone;
                pending.AlwaysOnTop = topmostSetting.IsChecked == true;
                pending.TileDisplayMode = (string)((ComboBoxItem)tileSetting.SelectedItem).Tag;
                pending.TileScalePercent = FluentUi.NormalizeTileScale((int)Math.Round(tileScaleSlider.Value));
                pending.MainBackgroundPreset = (string)((ComboBoxItem)backgroundSetting.SelectedItem).Tag;
                pending.GroupOpacity = FluentUi.NormalizeGroupOpacity((int)Math.Round(groupOpacitySlider.Value));
                pending.BottomPanelOpacity = NormalizeBottomPanelOpacity(
                    (int)Math.Round(bottomOpacitySlider.Value));
                pending.Compact = pending.TileDisplayMode != "labeled";
                pending.SoundsEnabled = sounds.IsChecked == true;
                pending.PomodoroSoundEnabled = pomodoroSound.IsChecked == true;
                pending.RemindersSoundEnabled = reminderSound.IsChecked == true;
                pending.CountdownSoundEnabled = countdownSound.IsChecked == true;
                // Registry and apps.json must not be left half-applied.
                string previousRunValue = null;
                bool attemptedRegistryChange = false;
                try
                {
                    WindowsStartup.State currentRun = WindowsStartup.Read();
                    previousRunValue = currentRun.RawValue;
                    bool enable = launchAtLogin.IsChecked == true;
                    bool hidden = launchHidden.IsChecked == true;
                    // Avoid touching HKCU Run when only unrelated settings were edited.
                    if (enable != currentRun.Enabled ||
                        (enable && (hidden != currentRun.InTray || !currentRun.PointsToCurrentExe)))
                    {
                        attemptedRegistryChange = true;
                        WindowsStartup.Apply(enable, hidden);
                    }
                }
                catch (Exception ex)
                {
                    if (attemptedRegistryChange)
                    {
                        try { WindowsStartup.WriteRaw(previousRunValue); }
                        catch (Exception rollbackError) { ConfigStore.Log(rollbackError); }
                    }
                    ShowError("Не удалось настроить автозапуск Windows.", ex);
                    return;
                }
                bool targetsChanged =
                    !String.Equals(_config.NetworkCheck1Mode, pending.NetworkCheck1Mode, StringComparison.Ordinal) ||
                    !String.Equals(_config.NetworkCheck1Target, pending.NetworkCheck1Target, StringComparison.Ordinal) ||
                    !String.Equals(_config.NetworkCheck2Mode, pending.NetworkCheck2Mode, StringComparison.Ordinal) ||
                    !String.Equals(_config.NetworkCheck2Target, pending.NetworkCheck2Target, StringComparison.Ordinal);
                if (!SaveConfigChanges(pending))
                {
                    if (attemptedRegistryChange)
                    {
                        try { WindowsStartup.WriteRaw(previousRunValue); }
                        catch (Exception ex) { ShowError("Не удалось восстановить прежний автозапуск Windows.", ex); }
                    }
                    return;
                }
                backgroundsSaved = true;
                if (_weatherTimer != null) _weatherTimer.Interval = WeatherRefreshInterval();
                _groupMenuItem.IsChecked = _config.GroupingEnabled;
                _newGroupMenuItem.IsEnabled = _config.GroupingEnabled;
                _collectionsMenuItem.Visibility = _config.ShowCollections == false
                    ? Visibility.Collapsed : Visibility.Visible;
                if (_config.ShowCollections == false && _collectionsWindow != null)
                    _collectionsWindow.Close();
                _pinMenuItem.IsChecked = _config.AlwaysOnTop;
                Topmost = _config.AlwaysOnTop;
                UpdateTileModeMenu();
                ApplyMainBackground(_config.MainBackgroundPreset);
                ApplyBottomPanelOpacity(NormalizeBottomPanelOpacity(_config.BottomPanelOpacity));
                RenderTiles();
                UpdateMonitorVisibility();
                UpdateCountdownAvailability();
                UpdatePomodoroUi();
                UpdateCountdownUi();
                UpdateRdpStatus();
                if (targetsChanged)
                {
                    // Clear the previous targets immediately; an in-flight result
                    // is rejected and the new checks begin once it finishes.
                    _pingInternational.Text = "Проверка 1: обновление…";
                    _pingRussian.Text = "Проверка 2: обновление…";
                }
                if (targetsChanged && _happWindow != null)
                    _happWindow.UpdateNetworkConfig(_config.Copy());
                RefreshNetwork();
                if (changedCity || changedProvider) ResetWeatherSnapshot();
                if (changedCity || changedProvider || _config.ShowWeather != _weatherSettingsVisible || _config.ShowWeather)
                    RefreshWeather();
            }
            else if (pendingZipRestore == null && _canSave && _config.SettingsLastSection != selectedSettingsSection)
            {
                // A navigation preference is independent of edits cancelled in the dialog.
                AppConfig navigation = _config.Copy();
                navigation.SettingsLastSection = selectedSettingsSection;
                SaveConfigChanges(navigation, false);
            }
            }
            finally
            {
                // These files were copied before Save; roll them back on Cancel,
                // Save failure and early returns from the settings workflow.
                foreach (string preset in backgroundsSaved ? removedBackgrounds : addedBackgrounds)
                {
                    try { CustomBackgroundStore.Delete(preset); }
                    catch (Exception ex) { ConfigStore.Log(ex); }
                }
            }
            // Run only after the settings dialog and all preview cleanup have completed.
            if (pendingZipRestore != null) CompleteZipRestore(pendingZipRestore, this);
        }

        private bool SaveConfig()
        {
            return SaveConfigChanges(_config);
        }

        private bool SaveConfigChanges(AppConfig pending, bool reportError = true)
        {
            if (!_canSave) { if (reportError) ShowSaveDisabled(); return false; }
            try { ConfigStore.Save(pending); }
            catch (Exception ex)
            {
                if (reportError) ShowError("Не удалось сохранить настройки.", ex);
                else ConfigStore.Log(ex);
                return false;
            }
            pending.ApplySavedItemCopies();
            _config = pending;
            return true;
        }

        private void ShowSaveDisabled()
        {
            MessageBox.Show(this,
                "Сохранение отключено из-за ошибки чтения apps.json.\n" +
                "Проверь файл в %APPDATA%\\AppShelf и перезапусти AppShelf.",
                "AppShelf", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ShowError(string title, Exception error)
        {
            ConfigStore.Log(error);
            MessageBox.Show(this, title + "\n\n" + error.Message +
                "\n\nЖурнал: " + ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static class Program
    {
        private static int _renderThreadFailureReported;
        [STAThread]
        public static void Main()
        {
            // Keep the parameterless entry point: existing Windows startup and
            // process regression scenarios call Program.Main() directly.
            string[] commandLine = Environment.GetCommandLineArgs();
            if (commandLine.Length > 1 && commandLine[1] == "--apply-update")
            {
                string[] helperArgs = new string[commandLine.Length - 1];
                Array.Copy(commandLine, 1, helperArgs, 0, helperArgs.Length);
                try { AppUpdate.Apply(helperArgs); }
                catch (Exception error)
                {
                    ConfigStore.Log(error);
                    MessageBox.Show("Установка обновления не выполнена:\n" + error.Message +
                        "\n\nИсходный AppShelf.exe сохранён; подробности в " + ConfigStore.LogPath,
                        "AppShelf — обновление", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }
            // Global spans terminal sessions; the SID separates independent Windows profiles.
            // Fail closed, before even error logging can touch the user's data directory.
            Mutex instance = null;
            bool owned = false;
            try
            {
                string sid;
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
                instance = new Mutex(false, "Global\\AppShelf.Profile." + sid);
                try { owned = instance.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    // Windows logon must not show an unnecessary duplicate-instance dialog.
                    if (WindowsStartup.IsTrayLaunch()) return;
                    MessageBox.Show("AppShelf уже запущен для этого пользователя. Откройте его через значок в трее " +
                        "или проверьте другой сеанс Windows.", "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                RunApplication();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось запустить AppShelf:\n" + ex.Message, "AppShelf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { if (owned) instance.ReleaseMutex(); }
                finally { if (instance != null) instance.Dispose(); }
            }
        }

        private static void RunApplication()
        {
            try { NetworkTls.Configure(); }
            catch (Exception ex) { ConfigStore.Log(new InvalidOperationException("Ошибка настройки TLS", ex)); }
            Application app = new Application();
            try { FluentUi.Install(app); }
            catch (Exception ex) { ConfigStore.Log(new InvalidOperationException("Ошибка оформления AppShelf", ex)); }
            app.DispatcherUnhandledException += delegate(object sender,
                System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                COMException renderError = e.Exception as COMException;
                if (renderError != null && renderError.ErrorCode == unchecked((int)0x88980406))
                {
                    // WPF cannot reliably restore its render thread in-process.
                    // Repeated window messages used to produce repeated error
                    // dialogs and identical stacks after a laptop woke from sleep.
                    if (Interlocked.Exchange(ref _renderThreadFailureReported, 1) == 0)
                    {
                        ConfigStore.Log(e.Exception);
                        try
                        {
                            MessageBox.Show("Сбой графического движка WPF. " +
                                "AppShelf необходимо перезапустить.\n\n" +
                                "Подробности: " + ConfigStore.LogPath,
                                "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        catch { }
                    }
                    e.Handled = true;
                    app.Shutdown(-1);
                    return;
                }
                ConfigStore.Log(e.Exception);
                MessageBox.Show("Внутренняя ошибка AppShelf:\n" + e.Exception.Message +
                    "\nЖурнал: " + ConfigStore.LogPath, "AppShelf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            try { app.Run(new MainWindow(WindowsStartup.IsTrayLaunch())); }
            catch (Exception ex)
            {
                ConfigStore.Log(ex);
                MessageBox.Show("Не удалось открыть AppShelf:\n" + ex.Message +
                    "\nЖурнал: " + ConfigStore.LogPath, "AppShelf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
