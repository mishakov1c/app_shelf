// Collections workspace: separate from the launcher and from reminder scheduling.
// This is the first functional slice. JSON import, cover management and full-backup integration
// are deliberately separate slices before the Collections module is released.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
// Ordinary messages follow AppShelf's dark dialog; no native startup errors here.
using MessageBox = AppShelf.AppDialog;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShelf
{
    internal sealed class CollectionsWindow : Window
    {
        private CollectionDocument _document;
        private bool _canSave;
        private bool _refreshing;
        private string _selectedCollectionId;
        private string _selectedEntryId;
        private string _lastRandomEntryId;
        private readonly Random _random = new Random();
        private ListBox _collectionsList;
        private ListBox _entriesList;
        private TextBox _search;
        private TextBox _tags;
        private StackPanel _details;
        private TextBlock _entryHeading;
        private TextBlock _hint;
        private Button _addEntry;
        private Button _editCollection;
        private Button _fieldsButton;
        private Button _exportCollections;
        private Button _deleteCollection;
        private Button _editEntry;
        private Button _deleteEntry;
        private Button _choose;
        private Button _confirm;

        private static TextBlock Label(string text, double size, Brush color)
        {
            return new TextBlock
            {
                Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static Button ActionButton(string label, bool primary)
        {
            return new Button
            {
                Content = label, MinHeight = 34, Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Foreground = primary ? FluentUi.Background : FluentUi.Foreground,
                Background = primary ? FluentUi.Accent : FluentUi.SurfaceRaised,
                BorderBrush = primary ? FluentUi.Accent : FluentUi.BorderColor,
                BorderThickness = new Thickness(1)
            };
        }

        private static Border Card(UIElement content)
        {
            return new Border
            {
                Background = FluentUi.Surface, CornerRadius = new CornerRadius(12),
                BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1),
                Padding = new Thickness(13), Child = content
            };
        }

        private static TextBox Input(string value, bool multiline)
        {
            TextBox box = new TextBox
            {
                Text = value ?? "", MinHeight = multiline ? 90 : 34,
                Padding = new Thickness(9, 6, 9, 6),
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = multiline, VerticalScrollBarVisibility =
                    multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            };
            return box;
        }

        private static void FormRow(StackPanel panel, string caption, UIElement input)
        {
            TextBlock label = Label(caption, 12, FluentUi.Secondary);
            label.Margin = new Thickness(0, 8, 0, 5);
            panel.Children.Add(label);
            panel.Children.Add(input);
        }

        private static Window Dialog(Window owner, string title, double width, double height)
        {
            Window dialog = new Window
            {
                Title = "AppShelf — " + title, Owner = owner, Width = width, Height = height,
                MinWidth = 390, MinHeight = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            FluentUi.StyleReminderInputs(dialog);
            dialog.Background = FluentUi.Background;
            dialog.FontFamily = new FontFamily("Segoe UI");
            dialog.Foreground = FluentUi.Foreground;
            return dialog;
        }

        public CollectionsWindow(Window owner)
        {
            Owner = owner;
            Title = "AppShelf — Подборки";
            Width = 1080; Height = 700;
            MinWidth = 760; MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = FluentUi.Background;
            Foreground = FluentUi.Foreground;
            FontFamily = new FontFamily("Segoe UI");
            FluentUi.PrepareWindow(this);
            FluentUi.StyleReminderInputs(this);

            string warning;
            _document = CollectionStore.Load(out warning, out _canSave);
            BuildLayout();
            RefreshCollections();
            if (!String.IsNullOrWhiteSpace(warning))
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    MessageBox.Show(this, warning, "AppShelf — подборки",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
        }

        private void BuildLayout()
        {
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            Content = root;
            StackPanel heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            TextBlock title = Label("Подборки", 24, FluentUi.Foreground);
            title.FontWeight = FontWeights.Bold;
            heading.Children.Add(title);
            _hint = Label("Собственные коллекции без предустановленных записей · все данные хранятся локально", 12,
                FluentUi.Secondary);
            _hint.Margin = new Thickness(0, 3, 0, 0);
            heading.Children.Add(_hint);

            Grid workspace = new Grid();
            workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
            root.Children.Add(workspace);

            DockPanel sidebar = new DockPanel();
            Border left = Card(sidebar);
            left.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(left, 0);
            workspace.Children.Add(left);
            StackPanel sidebarHeader = new StackPanel();
            DockPanel.SetDock(sidebarHeader, Dock.Top);
            sidebar.Children.Add(sidebarHeader);
            TextBlock collectionsTitle = Label("МОИ ПОДБОРКИ", 12, FluentUi.Accent);
            collectionsTitle.FontWeight = FontWeights.SemiBold;
            sidebarHeader.Children.Add(collectionsTitle);
            Button createCollection = ActionButton("+ Новая подборка", true);
            createCollection.Margin = new Thickness(0, 12, 0, 6);
            createCollection.IsEnabled = _canSave;
            createCollection.Click += delegate { EditCollection(true); };
            sidebarHeader.Children.Add(createCollection);
            WrapPanel exchange = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
            Button import = ActionButton("Импорт JSON…", false);
            import.IsEnabled = _canSave;
            import.Click += delegate { ImportCollections(); };
            exchange.Children.Add(import);
            _exportCollections = ActionButton("Экспорт JSON…", false);
            _exportCollections.Click += delegate { ExportSelectedCollection(); };
            exchange.Children.Add(_exportCollections);
            sidebarHeader.Children.Add(exchange);
            _collectionsList = new ListBox
            {
                Background = FluentUi.Surface, Foreground = FluentUi.Foreground,
                BorderThickness = new Thickness(0), MinHeight = 135
            };
            _collectionsList.SelectionChanged += delegate
            {
                if (_refreshing) return;
                ListBoxItem selected = _collectionsList.SelectedItem as ListBoxItem;
                _selectedCollectionId = selected == null ? null : selected.Tag as string;
                _selectedEntryId = null;
                _lastRandomEntryId = null;
                RefreshEntries();
            };
            WrapPanel collectionActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            _editCollection = ActionButton("Изменить", false);
            _editCollection.Click += delegate { EditCollection(false); };
            collectionActions.Children.Add(_editCollection);
            _fieldsButton = ActionButton("Поля", false);
            _fieldsButton.Click += delegate { ManageFields(); };
            collectionActions.Children.Add(_fieldsButton);
            _deleteCollection = ActionButton("Удалить", false);
            _deleteCollection.Click += delegate { DeleteCollection(); };
            collectionActions.Children.Add(_deleteCollection);
            DockPanel.SetDock(collectionActions, Dock.Bottom);
            sidebar.Children.Add(collectionActions);
            sidebar.Children.Add(_collectionsList);

            DockPanel middle = new DockPanel();
            Border center = Card(middle);
            center.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(center, 1);
            workspace.Children.Add(center);
            StackPanel filters = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(filters, Dock.Top);
            middle.Children.Add(filters);
            _entryHeading = Label("Записи", 17, FluentUi.Foreground);
            _entryHeading.FontWeight = FontWeights.SemiBold;
            filters.Children.Add(_entryHeading);
            _search = Input("", false);
            _search.Margin = new Thickness(0, 9, 0, 6);
            _search.ToolTip = "Поиск по названию и описанию";
            filters.Children.Add(_search);
            _tags = Input("", false);
            _tags.ToolTip = "Укажи несколько тегов через запятую. Записи должны содержать каждый тег.";
            filters.Children.Add(_tags);
            TextBlock tagHint = Label("Поиск · теги через запятую (совпадение всех)", 10.5, FluentUi.Secondary);
            tagHint.Margin = new Thickness(0, 4, 0, 8);
            filters.Children.Add(tagHint);
            WrapPanel entryActions = new WrapPanel();
            filters.Children.Add(entryActions);
            _addEntry = ActionButton("+ Добавить запись", true);
            _addEntry.Click += delegate { EditEntry(true); };
            entryActions.Children.Add(_addEntry);
            _choose = ActionButton("Случайный вариант", false);
            _choose.Click += delegate { ChooseRandom(); };
            entryActions.Children.Add(_choose);

            _entriesList = new ListBox
            {
                Background = FluentUi.Surface, Foreground = FluentUi.Foreground,
                BorderThickness = new Thickness(0)
            };
            _entriesList.SelectionChanged += delegate
            {
                if (_refreshing) return;
                ListBoxItem selected = _entriesList.SelectedItem as ListBoxItem;
                _selectedEntryId = selected == null ? null : selected.Tag as string;
                _lastRandomEntryId = null;
                _choose.Content = "Случайный вариант";
                RefreshDetails();
            };
            middle.Children.Add(_entriesList);
            _search.TextChanged += delegate
            { _selectedEntryId = null; _lastRandomEntryId = null; RefreshEntries(); };
            _tags.TextChanged += delegate
            { _selectedEntryId = null; _lastRandomEntryId = null; RefreshEntries(); };

            DockPanel rightPanel = new DockPanel();
            Border right = Card(rightPanel);
            Grid.SetColumn(right, 2);
            workspace.Children.Add(right);
            TextBlock detailsTitle = Label("ВЫБРАННАЯ ЗАПИСЬ", 12, FluentUi.Accent);
            detailsTitle.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(detailsTitle, Dock.Top);
            detailsTitle.Margin = new Thickness(0, 0, 0, 12);
            rightPanel.Children.Add(detailsTitle);
            WrapPanel itemActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(itemActions, Dock.Bottom);
            rightPanel.Children.Add(itemActions);
            _editEntry = ActionButton("Изменить", false);
            _editEntry.Click += delegate { EditEntry(false); };
            itemActions.Children.Add(_editEntry);
            _deleteEntry = ActionButton("Удалить", false);
            _deleteEntry.Click += delegate { DeleteEntry(); };
            itemActions.Children.Add(_deleteEntry);
            _confirm = ActionButton("Подтвердить", true);
            _confirm.Click += delegate { ConfirmEntry(); };
            itemActions.Children.Add(_confirm);
            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            FluentUi.StyleSettingsScroll(scroll);
            _details = new StackPanel();
            scroll.Content = _details;
            rightPanel.Children.Add(scroll);
            RefreshDetails();
        }

        private CollectionDefinition CurrentCollection()
        {
            return _document.Collections.Find(delegate(CollectionDefinition item)
            { return item.Id == _selectedCollectionId; });
        }

        private CollectionEntry CurrentEntry(CollectionDefinition collection)
        {
            return collection == null ? null : collection.Entries.Find(delegate(CollectionEntry entry)
            { return entry.Id == _selectedEntryId; });
        }

        private bool SaveChanges(CollectionDocument pending)
        {
            if (!_canSave) return false;
            try
            {
                CollectionStore.Save(pending);
                _document = pending;
                return true;
            }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                MessageBox.Show(this, "Не удалось сохранить подборки:\n" + error.Message,
                    "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void RefreshCollections()
        {
            _refreshing = true;
            try
            {
                _collectionsList.Items.Clear();
                foreach (CollectionDefinition collection in _document.Collections)
                {
                    ListBoxItem row = new ListBoxItem
                    {
                        Content = collection.Name, Tag = collection.Id,
                        Padding = new Thickness(7, 9, 7, 9)
                    };
                    _collectionsList.Items.Add(row);
                    if (collection.Id == _selectedCollectionId) _collectionsList.SelectedItem = row;
                }
                if (_collectionsList.SelectedItem == null && _collectionsList.Items.Count > 0)
                    _collectionsList.SelectedIndex = 0;
                ListBoxItem selected = _collectionsList.SelectedItem as ListBoxItem;
                _selectedCollectionId = selected == null ? null : selected.Tag as string;
            }
            finally { _refreshing = false; }
            RefreshEntries();
        }

        private static bool HasTag(CollectionEntry entry, string tag)
        {
            foreach (string stored in entry.Tags)
                if (String.Equals(stored, tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private List<CollectionEntry> FilteredEntries(CollectionDefinition collection)
        {
            List<CollectionEntry> result = new List<CollectionEntry>();
            if (collection == null) return result;
            string search = (_search.Text ?? "").Trim();
            string[] tags = (_tags.Text ?? "").Split(',');
            foreach (CollectionEntry entry in collection.Entries)
            {
                if (search.Length > 0 &&
                    (entry.Title ?? "").IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                    (entry.Description ?? "").IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
                    continue;
                bool accepted = true;
                foreach (string raw in tags)
                {
                    string tag = raw.Trim();
                    if (tag.Length > 0 && !HasTag(entry, tag)) { accepted = false; break; }
                }
                if (accepted) result.Add(entry);
            }
            return result;
        }

        private void RefreshEntries()
        {
            CollectionDefinition collection = CurrentCollection();
            List<CollectionEntry> entries = FilteredEntries(collection);
            _refreshing = true;
            try
            {
                _entriesList.Items.Clear();
                foreach (CollectionEntry entry in entries)
                {
                    string text = entry.Completed ? "✓ " + entry.Title : entry.Title;
                    ListBoxItem row = new ListBoxItem
                    {
                        Content = text, Tag = entry.Id, Padding = new Thickness(8, 9, 8, 9)
                    };
                    _entriesList.Items.Add(row);
                    if (entry.Id == _selectedEntryId) _entriesList.SelectedItem = row;
                }
                if (_entriesList.SelectedItem == null && _entriesList.Items.Count > 0)
                    _entriesList.SelectedIndex = 0;
                ListBoxItem selected = _entriesList.SelectedItem as ListBoxItem;
                _selectedEntryId = selected == null ? null : selected.Tag as string;
            }
            finally { _refreshing = false; }
            _entryHeading.Text = collection == null ? "Записи" : collection.Name + " · " + entries.Count;
            bool canEditGroup = _canSave && collection != null;
            _editCollection.IsEnabled = canEditGroup;
            _fieldsButton.IsEnabled = canEditGroup;
            _deleteCollection.IsEnabled = canEditGroup;
            _exportCollections.IsEnabled = collection != null;
            _addEntry.IsEnabled = canEditGroup;
            _choose.IsEnabled = entries.Count > 0;
            _choose.Content = _lastRandomEntryId == null ? "Случайный вариант" : "Другой вариант";
            RefreshDetails();
        }

        private void DetailText(string value, double size, Brush foreground, Thickness margin)
        {
            TextBlock text = Label(value, size, foreground);
            text.Margin = margin;
            _details.Children.Add(text);
        }

        private void RefreshDetails()
        {
            if (_details == null) return;
            _details.Children.Clear();
            CollectionDefinition collection = CurrentCollection();
            CollectionEntry entry = CurrentEntry(collection);
            bool selected = collection != null && entry != null;
            if (!selected)
            {
                DetailText(collection == null
                    ? "Создай первую подборку. Шаблоны добавляют поля, но не создают записи."
                    : "Добавь запись или выбери вариант из списка.",
                    13, FluentUi.Secondary, new Thickness(0));
            }
            else
            {
                TextBlock title = Label(entry.Title, 18, FluentUi.Foreground);
                title.FontWeight = FontWeights.SemiBold;
                _details.Children.Add(title);
                if (!String.IsNullOrEmpty(entry.CoverFile))
                {
                    try
                    {
                        BitmapImage image = new BitmapImage();
                        using (FileStream input = File.OpenRead(CollectionStore.CoverPath(entry.CoverFile)))
                        {
                            image.BeginInit();
                            image.CacheOption = BitmapCacheOption.OnLoad;
                            image.DecodePixelWidth = 640;
                            image.StreamSource = input;
                            image.EndInit();
                            image.Freeze();
                        }
                        Border imageFrame = new Border
                        {
                            BorderBrush = FluentUi.BorderColor,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(8),
                            Margin = new Thickness(0, 12, 0, 4),
                            Padding = new Thickness(7),
                            Background = FluentUi.SurfaceRaised,
                            Child = new Image
                            {
                                Source = image, Height = 170, Stretch = Stretch.Uniform
                            }
                        };
                        _details.Children.Add(imageFrame);
                    }
                    catch (Exception error)
                    {
                        ConfigStore.Log(error);
                        DetailText("Обложка недоступна на этом компьютере.", 11,
                            FluentUi.Amber, new Thickness(0, 9, 0, 0));
                    }
                }
                if (entry.Completed)
                    DetailText("Отмечено как приобретённое / выполненное", 12, FluentUi.Accent,
                        new Thickness(0, 8, 0, 0));
                if (!String.IsNullOrWhiteSpace(entry.Description))
                    DetailText(entry.Description, 12, FluentUi.Foreground,
                        new Thickness(0, 12, 0, 3));
                if (!String.IsNullOrWhiteSpace(entry.SourceUrl))
                {
                    Button openProduct = ActionButton("Открыть ссылку на товар", false);
                    openProduct.HorizontalAlignment = HorizontalAlignment.Left;
                    openProduct.ToolTip = entry.SourceUrl;
                    string productAddress = entry.SourceUrl;
                    openProduct.Click += delegate
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                                productAddress) { UseShellExecute = true });
                        }
                        catch (Exception error)
                        {
                            ConfigStore.Log(error);
                            MessageBox.Show(this, "Не удалось открыть ссылку: " + error.Message,
                                "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    };
                    _details.Children.Add(openProduct);
                }
                if (entry.Tags.Count > 0)
                    DetailText("Теги: " + String.Join(", ", entry.Tags.ToArray()), 11,
                        FluentUi.Secondary, new Thickness(0, 11, 0, 8));
                foreach (CollectionField field in collection.Fields)
                {
                    CollectionValue value = entry.Values.Find(delegate(CollectionValue fieldValue)
                    { return fieldValue.Key == field.Key; });
                    if (value == null || String.IsNullOrWhiteSpace(value.Value)) continue;
                    DetailText(field.Name, 11, FluentUi.Secondary, new Thickness(0, 12, 0, 3));
                    DetailText(value.Value, 12, FluentUi.Foreground, new Thickness(0));
                }
                if (entry.History.Count > 0)
                {
                    DetailText("Последние подтверждения", 11, FluentUi.Accent,
                        new Thickness(0, 18, 0, 5));
                    for (int i = entry.History.Count - 1; i >= 0 && i >= entry.History.Count - 5; i--)
                    {
                        CollectionAction action = entry.History[i];
                        DateTime date;
                        string when = DateTime.TryParse(action.AtUtc, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out date)
                            ? date.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : action.AtUtc;
                        DetailText(when + " · " + action.Label, 11,
                            FluentUi.Secondary, new Thickness(0, 0, 0, 5));
                    }
                }
            }
            _editEntry.IsEnabled = _canSave && selected;
            _deleteEntry.IsEnabled = _canSave && selected;
            _confirm.IsEnabled = _canSave && selected;
            _confirm.Content = collection == null ? "Подтвердить"
                : collection.ConfirmationLabel;
        }

        private static bool ValidTitle(string value, int maxLength)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength;
        }

        private void EditCollection(bool isNew)
        {
            CollectionDefinition existing = isNew ? null : CurrentCollection();
            if (!_canSave || (!isNew && existing == null)) return;
            Window dialog = Dialog(this, isNew ? "Новая подборка" : "Настройки подборки", 470, 420);
            DockPanel dock = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = dock;
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom);
            dock.Children.Add(actions);
            StackPanel body = new StackPanel();
            ScrollViewer scroll = new ScrollViewer { Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FluentUi.StyleSettingsScroll(scroll);
            dock.Children.Add(scroll);
            TextBox name = Input(existing == null ? "" : existing.Name, false);
            name.MaxLength = 120;
            FormRow(body, "Название подборки", name);
            ComboBox template = new ComboBox { MinHeight = 35 };
            foreach (CollectionTemplate option in CollectionTemplates.All)
                template.Items.Add(new ComboBoxItem { Content = option.Title, Tag = option.Key });
            template.SelectedIndex = 0;
            if (existing != null)
            {
                for (int i = 0; i < template.Items.Count; i++)
                    if ((string)((ComboBoxItem)template.Items[i]).Tag == existing.Template)
                        template.SelectedIndex = i;
                template.IsEnabled = false; // Changing a template must not discard user fields/values.
            }
            FormRow(body, "Шаблон (создаёт поля, но не записи)", template);
            TextBox confirmation = Input(existing == null
                ? CollectionTemplates.All[0].Confirmation : existing.ConfirmationLabel, false);
            confirmation.MaxLength = 60;
            FormRow(body, "Название действия подтверждения", confirmation);
            if (isNew)
                template.SelectionChanged += delegate
                {
                    ComboBoxItem option = template.SelectedItem as ComboBoxItem;
                    if (option != null) confirmation.Text =
                        CollectionTemplates.Find((string)option.Tag).Confirmation;
                };
            TextBlock error = Label("", 11, FluentUi.Amber);
            error.Margin = new Thickness(0, 9, 0, 0);
            body.Children.Add(error);
            Button save = ActionButton("Сохранить", true);
            save.IsDefault = true;
            save.Click += delegate
            {
                if (!ValidTitle(name.Text, 120) || !ValidTitle(confirmation.Text, 60))
                {
                    error.Text = "Укажи название (до 120 символов) и действие подтверждения (до 60).";
                    return;
                }
                CollectionDocument pending = _document.Copy();
                CollectionDefinition target;
                if (isNew)
                {
                    string key = (string)((ComboBoxItem)template.SelectedItem).Tag;
                    target = CollectionTemplates.Create(key, name.Text.Trim());
                    pending.Collections.Add(target);
                }
                else
                    target = pending.Collections.Find(delegate(CollectionDefinition item)
                    { return item.Id == existing.Id; });
                if (target == null) return;
                target.Name = name.Text.Trim();
                target.ConfirmationLabel = confirmation.Text.Trim();
                if (!SaveChanges(pending)) return;
                _selectedCollectionId = target.Id;
                _selectedEntryId = null;
                RefreshCollections();
                dialog.DialogResult = true;
            };
            actions.Children.Add(save);
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            actions.Children.Add(cancel);
            dialog.Loaded += delegate { name.Focus(); name.SelectAll(); };
            dialog.ShowDialog();
        }

        private static bool IsValidWebAddress(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        // Changes only the in-memory draft; cancelling either dialog preserves the file.
        private static bool EditFieldDialog(Window owner, CollectionField field)
        {
            Window dialog = Dialog(owner, "Поле подборки", 450, 320);
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;
            WrapPanel buttons = new WrapPanel { Margin = new Thickness(0, 13, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            StackPanel form = new StackPanel();
            root.Children.Add(form);
            TextBox name = Input(field.Name, false);
            name.MaxLength = 120;
            FormRow(form, "Название поля", name);
            ComboBox type = new ComboBox { MinHeight = 36 };
            type.Items.Add(new ComboBoxItem { Content = "Текст", Tag = "text" });
            type.Items.Add(new ComboBoxItem { Content = "Многострочный текст", Tag = "multiline" });
            type.Items.Add(new ComboBoxItem { Content = "Ссылка", Tag = "url" });
            type.SelectedIndex = field.Type == "multiline" ? 1 : field.Type == "url" ? 2 : 0;
            FormRow(form, "Тип поля", type);
            TextBlock error = Label("", 11, FluentUi.Amber);
            error.Margin = new Thickness(0, 10, 0, 0);
            form.Children.Add(error);
            Button save = ActionButton("Сохранить", true);
            save.IsDefault = true;
            save.Click += delegate
            {
                if (!ValidTitle(name.Text, 120))
                {
                    error.Text = "Укажи название поля длиной до 120 символов.";
                    return;
                }
                field.Name = name.Text.Trim();
                field.Type = (string)((ComboBoxItem)type.SelectedItem).Tag;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(save);
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            buttons.Children.Add(cancel);
            dialog.Loaded += delegate { name.Focus(); name.SelectAll(); };
            return dialog.ShowDialog() == true;
        }

        private void ManageFields()
        {
            CollectionDefinition original = CurrentCollection();
            if (!_canSave || original == null) return;
            List<CollectionField> draft = new List<CollectionField>();
            foreach (CollectionField field in original.Fields) draft.Add(field.Copy());

            Window dialog = Dialog(this, "Поля — " + original.Name, 510, 525);
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;
            WrapPanel saveButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(saveButtons, Dock.Bottom);
            root.Children.Add(saveButtons);
            StackPanel header = new StackPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            TextBlock explanation = Label("Поля доступны каждой записи этой подборки. " +
                "Переименование сохраняет заполненные значения. Удаление поля удалит его значения во всех записях.",
                12, FluentUi.Secondary);
            explanation.Margin = new Thickness(0, 0, 0, 12);
            header.Children.Add(explanation);
            WrapPanel fieldActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
            header.Children.Add(fieldActions);
            ListBox list = new ListBox
            {
                Background = FluentUi.Surface, Foreground = FluentUi.Foreground,
                BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1)
            };
            root.Children.Add(list);
            Action<string> refresh = delegate(string selectedKey)
            {
                list.Items.Clear();
                foreach (CollectionField field in draft)
                {
                    string kind = field.Type == "url" ? "Ссылка" :
                        field.Type == "multiline" ? "Многострочный текст" : "Текст";
                    ListBoxItem row = new ListBoxItem
                    {
                        Content = field.Name + " · " + kind, Tag = field.Key,
                        Padding = new Thickness(8, 9, 8, 9)
                    };
                    list.Items.Add(row);
                    if (field.Key == selectedKey) list.SelectedItem = row;
                }
            };
            Func<CollectionField> selected = delegate
            {
                ListBoxItem row = list.SelectedItem as ListBoxItem;
                string key = row == null ? null : row.Tag as string;
                return draft.Find(delegate(CollectionField field) { return field.Key == key; });
            };

            Button add = ActionButton("+ Поле", true);
            add.Click += delegate
            {
                if (draft.Count >= 40)
                {
                    MessageBox.Show(dialog, "В подборке может быть не более 40 дополнительных полей.",
                        "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                CollectionField field = new CollectionField { Key = Guid.NewGuid().ToString("N"),
                    Name = "", Type = "text" };
                if (!EditFieldDialog(dialog, field)) return;
                draft.Add(field);
                refresh(field.Key);
            };
            fieldActions.Children.Add(add);
            Button edit = ActionButton("Изменить", false);
            edit.Click += delegate
            {
                CollectionField field = selected();
                if (field == null) return;
                CollectionField proposed = field.Copy();
                if (!EditFieldDialog(dialog, proposed)) return;
                field.Name = proposed.Name;
                field.Type = proposed.Type;
                refresh(field.Key);
            };
            fieldActions.Children.Add(edit);
            Button remove = ActionButton("Удалить", false);
            remove.Click += delegate
            {
                CollectionField field = selected();
                if (field == null) return;
                draft.Remove(field);
                refresh(null);
            };
            fieldActions.Children.Add(remove);
            Action updateSelection = delegate
            {
                edit.IsEnabled = selected() != null;
                remove.IsEnabled = selected() != null;
            };
            list.SelectionChanged += delegate { updateSelection(); };
            refresh(null);
            updateSelection();

            Button save = ActionButton("Сохранить поля", true);
            save.IsDefault = true;
            save.Click += delegate
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CollectionField field in draft)
                    if (!names.Add(field.Name.Trim()))
                    {
                        MessageBox.Show(dialog, "Названия полей должны быть уникальными: " + field.Name,
                            "AppShelf — поля", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                CollectionDocument pending = _document.Copy();
                CollectionDefinition group = pending.Collections.Find(
                    delegate(CollectionDefinition item) { return item.Id == original.Id; });
                if (group == null) return;
                HashSet<string> retained = new HashSet<string>(StringComparer.Ordinal);
                foreach (CollectionField field in draft) retained.Add(field.Key);
                int removedValues = 0;
                foreach (CollectionEntry entry in group.Entries)
                    foreach (CollectionValue value in entry.Values)
                        if (!retained.Contains(value.Key)) removedValues++;

                // A new URL type must not make already entered free-form data invalid.
                foreach (CollectionField field in draft)
                {
                    if (field.Type != "url") continue;
                    foreach (CollectionEntry entry in group.Entries)
                    {
                        CollectionValue value = entry.Values.Find(
                            delegate(CollectionValue item) { return item.Key == field.Key; });
                        if (value != null && !String.IsNullOrWhiteSpace(value.Value) &&
                            !IsValidWebAddress(value.Value))
                        {
                            MessageBox.Show(dialog, "Нельзя сделать поле «" + field.Name +
                                "» ссылкой: в некоторых записях сохранён обычный текст.",
                                "AppShelf", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                }
                if (removedValues > 0 &&
                    MessageBox.Show(dialog, "Будет удалено " + removedValues +
                        " сохранённых значений в удалённых полях. Продолжить?",
                        "AppShelf — поля", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

                group.Fields = new List<CollectionField>();
                foreach (CollectionField field in draft) group.Fields.Add(field.Copy());
                foreach (CollectionEntry entry in group.Entries)
                    entry.Values.RemoveAll(delegate(CollectionValue value)
                    { return !retained.Contains(value.Key); });

                if (!SaveChanges(pending)) return;
                RefreshEntries();
                dialog.DialogResult = true;
            };
            saveButtons.Children.Add(save);
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            saveButtons.Children.Add(cancel);
            dialog.ShowDialog();
        }

        private void DeleteCollection()
        {
            CollectionDefinition collection = CurrentCollection();
            if (!_canSave || collection == null) return;
            string question = "Удалить подборку «" + collection.Name + "» и все её записи (" +
                collection.Entries.Count + ")? Отменить удаление нельзя.";
            if (MessageBox.Show(this, question, "AppShelf — подборки",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            CollectionDocument pending = _document.Copy();
            pending.Collections.RemoveAll(delegate(CollectionDefinition item)
            { return item.Id == collection.Id; });
            if (!SaveChanges(pending)) return;
            _selectedCollectionId = null; _selectedEntryId = null;
            RefreshCollections();
        }

        private static Dictionary<string, string> MapImportFields(
            CollectionDefinition source, CollectionDefinition destination)
        {
            Dictionary<string, string> mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> targetKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (CollectionField field in source.Fields)
            {
                CollectionField matched = destination.Fields.Find(delegate(CollectionField candidate)
                {
                    return candidate.Key == field.Key;
                });
                if (matched == null)
                    matched = destination.Fields.Find(delegate(CollectionField candidate)
                    {
                        return String.Equals(candidate.Name, field.Name, StringComparison.OrdinalIgnoreCase);
                    });
                if (matched == null || matched.Type != field.Type || !targetKeys.Add(matched.Key))
                    throw new InvalidDataException("Поле «" + field.Name +
                        "» отсутствует, имеет другой тип или неоднозначно в выбранной подборке.");
                mapping.Add(field.Key, matched.Key);
            }
            return mapping;
        }

        private static bool SameEntryTitle(CollectionEntry entry, string title)
        {
            return String.Equals(entry.Title.Trim(), title.Trim(), StringComparison.CurrentCultureIgnoreCase);
        }

        private static void CountImport(CollectionDefinition source, CollectionDefinition target,
            out int added, out int existing)
        {
            added = 0; existing = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (CollectionEntry entry in source.Entries)
            {
                if (!seen.Add(entry.Title.Trim()))
                    throw new InvalidDataException("В файле повторяется название записи: " + entry.Title);
                if (target != null && target.Entries.Exists(
                    delegate(CollectionEntry item) { return SameEntryTitle(item, entry.Title); }))
                    existing++;
                else added++;
            }
        }

        private void ExportSelectedCollection()
        {
            CollectionDefinition group = CurrentCollection();
            if (group == null) return;
            SaveFileDialog picker = new SaveFileDialog
            {
                Title = "Экспорт подборки", Filter = "Подборка JSON (*.json)|*.json",
                FileName = "AppShelf-collection.json"
            };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                CollectionStore.ExportJson(picker.FileName, group);
                MessageBox.Show(this,
                    "Подборка экспортирована в JSON.\nОбложки не включены в JSON. " +
                    "Для переноса обложек используй полную ZIP-копию AppShelf.",
                    "AppShelf — подборки", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                MessageBox.Show(this, "Не удалось экспортировать JSON:\n" + error.Message,
                    "AppShelf — подборки", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportCollections()
        {
            if (!_canSave) return;
            OpenFileDialog picker = new OpenFileDialog
            {
                Title = "Импорт подборки из JSON", CheckFileExists = true,
                Filter = "Подборки (*.json)|*.json"
            };
            if (picker.ShowDialog(this) != true) return;
            List<CollectionDefinition> imported;
            try { imported = CollectionStore.ImportJson(picker.FileName); }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                MessageBox.Show(this, "Не удалось прочитать подборку:\n" + error.Message,
                    "AppShelf — импорт", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (imported.Count == 0) return;

            Window dialog = Dialog(this, "Импорт подборок", 540, 440);
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;
            WrapPanel bottom = new WrapPanel { Margin = new Thickness(0, 13, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            StackPanel form = new StackPanel();
            ScrollViewer scroll = new ScrollViewer
            {
                Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            FluentUi.StyleSettingsScroll(scroll);
            root.Children.Add(scroll);
            TextBlock note = Label("Проверь содержимое перед записью. Внешний JSON " +
                "не переносит файлы обложек и не меняет существующие данные без выбора назначения.",
                12, FluentUi.Secondary);
            note.Margin = new Thickness(0, 0, 0, 10);
            form.Children.Add(note);
            ComboBox sources = new ComboBox { MinHeight = 35 };
            foreach (CollectionDefinition group in imported)
                sources.Items.Add(new ComboBoxItem
                {
                    Content = group.Name + " · " + group.Entries.Count + " записей", Tag = group
                });
            sources.SelectedIndex = 0;
            FormRow(form, "Подборка из файла", sources);

            CollectionDefinition current = CurrentCollection();
            ComboBox destination = new ComboBox { MinHeight = 35 };
            destination.Items.Add(new ComboBoxItem { Content = "Создать новую подборку", Tag = "new" });
            if (current != null)
                destination.Items.Add(new ComboBoxItem
                {
                    Content = "Добавить записи в «" + current.Name + "»", Tag = "existing"
                });
            destination.SelectedIndex = 0;
            FormRow(form, "Куда импортировать", destination);

            ComboBox conflict = new ComboBox { MinHeight = 35 };
            conflict.Items.Add(new ComboBoxItem { Content = "Пропустить совпадающие записи", Tag = "skip" });
            conflict.Items.Add(new ComboBoxItem { Content = "Заменить совпадающие записи", Tag = "replace" });
            conflict.SelectedIndex = 0;
            FormRow(form, "Если название уже существует", conflict);

            TextBlock preview = Label("", 12, FluentUi.Foreground);
            preview.Margin = new Thickness(0, 15, 0, 8);
            form.Children.Add(preview);
            Button apply = ActionButton("Импортировать", true);
            apply.IsDefault = true;
            Action refresh = delegate
            {
                CollectionDefinition source =
                    (CollectionDefinition)((ComboBoxItem)sources.SelectedItem).Tag;
                bool existingTarget = (string)((ComboBoxItem)destination.SelectedItem).Tag == "existing";
                conflict.IsEnabled = existingTarget;
                try
                {
                    int added, duplicates;
                    if (existingTarget) MapImportFields(source, current);
                    CountImport(source, existingTarget ? current : null, out added, out duplicates);
                    preview.Foreground = FluentUi.Foreground;
                    preview.Text = (existingTarget ? "Добавление в «" + current.Name + "»"
                        : "Новая подборка «" + source.Name + "»") +
                        "\nНовых записей: " + added + "; совпадений по названию: " + duplicates +
                        "\nПолей: " + source.Fields.Count + ". Обложки не импортируются.";
                    apply.IsEnabled = true;
                }
                catch (Exception error)
                {
                    preview.Foreground = FluentUi.Amber;
                    preview.Text = error.Message;
                    apply.IsEnabled = false;
                }
            };
            sources.SelectionChanged += delegate { refresh(); };
            destination.SelectionChanged += delegate { refresh(); };
            conflict.SelectionChanged += delegate { refresh(); };
            refresh();

            apply.Click += delegate
            {
                CollectionDefinition source =
                    (CollectionDefinition)((ComboBoxItem)sources.SelectedItem).Tag;
                bool append = (string)((ComboBoxItem)destination.SelectedItem).Tag == "existing";
                bool replace = (string)((ComboBoxItem)conflict.SelectedItem).Tag == "replace";
                try
                {
                    CollectionDocument pending = _document.Copy();
                    CollectionDefinition target;
                    if (!append)
                    {
                        target = source.Copy();
                        pending.Collections.Add(target);
                    }
                    else
                    {
                        target = pending.Collections.Find(delegate(CollectionDefinition group)
                        { return group.Id == current.Id; });
                        if (target == null) throw new InvalidDataException("Подборка назначения не найдена.");
                        Dictionary<string, string> map = MapImportFields(source, target);
                        foreach (CollectionEntry incoming in source.Entries)
                        {
                            CollectionEntry known = target.Entries.Find(delegate(CollectionEntry item)
                            { return SameEntryTitle(item, incoming.Title); });
                            if (known != null && !replace) continue;
                            CollectionEntry copy = incoming.Copy();
                            foreach (CollectionValue value in copy.Values)
                                value.Key = map[value.Key];
                            if (known != null)
                            {
                                int position = target.Entries.IndexOf(known);
                                copy.Id = known.Id;
                                copy.CoverFile = known.CoverFile;
                                copy.History = known.History;
                                copy.Completed = known.Completed;
                                target.Entries[position] = copy;
                            }
                            else target.Entries.Add(copy);
                        }
                    }
                    if (!SaveChanges(pending)) return;
                    _selectedCollectionId = target.Id;
                    _selectedEntryId = null;
                    RefreshCollections();
                    dialog.DialogResult = true;
                }
                catch (Exception error)
                {
                    ConfigStore.Log(error);
                    preview.Foreground = FluentUi.Amber;
                    preview.Text = "Импорт не выполнен: " + error.Message;
                }
            };
            bottom.Children.Add(apply);
            Button cancel = ActionButton("Отмена", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            bottom.Children.Add(cancel);
            dialog.ShowDialog();
        }

        private static bool IsPurchaseCollection(CollectionDefinition collection)
        {
            return collection.Template == "shopping" || collection.Template == "wishlist";
        }

        private void EditEntry(bool isNew)
        {
            CollectionDefinition collection = CurrentCollection();
            CollectionEntry existing = isNew ? null : CurrentEntry(collection);
            if (!_canSave || collection == null || (!isNew && existing == null)) return;
            Window dialog = Dialog(this, isNew ? "Новая запись" : "Изменить запись", 540, 620);
            DockPanel root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
            StackPanel fields = new StackPanel();
            ScrollViewer scroll = new ScrollViewer { Content = fields,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FluentUi.StyleSettingsScroll(scroll);
            root.Children.Add(scroll);
            TextBox title = Input(existing == null ? "" : existing.Title, false);
            title.MaxLength = 200;
            FormRow(fields, "Название", title);
            TextBox productUrl = Input(existing == null ? "" : existing.SourceUrl, false);
            productUrl.MaxLength = 2048;
            FormRow(fields, "Ссылка на товар (необязательно)", productUrl);
            TextBox description = Input(existing == null ? "" : existing.Description, true);
            description.MaxLength = 5000;
            FormRow(fields, "Описание (необязательно)", description);
            TextBox tags = Input(existing == null ? "" : String.Join(", ", existing.Tags.ToArray()), false);
            FormRow(fields, "Теги (через запятую)", tags);
            Dictionary<string, TextBox> values = new Dictionary<string, TextBox>(StringComparer.Ordinal);
            foreach (CollectionField field in collection.Fields)
            {
                CollectionValue existingValue = existing == null ? null :
                    existing.Values.Find(delegate(CollectionValue value) { return value.Key == field.Key; });
                TextBox input = Input(existingValue == null ? "" : existingValue.Value, field.Type == "multiline");
                input.MaxLength = field.Type == "multiline" ? 10000 : 1000;
                FormRow(fields, field.Name + (field.Type == "url" ? " (URL)" : ""), input);
                values.Add(field.Key, input);
            }
            // A new file/URL is copied to the private Images folder only when saving.
            // The source path or URL itself is never persisted in collections.json.
            string coverChoice = null;
            string coverSource = null;
            TextBlock coverStatus = Label(existing != null && !String.IsNullOrEmpty(existing.CoverFile)
                ? "Сейчас используется сохранённая обложка."
                : "Обложка не выбрана.", 11, FluentUi.Secondary);
            coverStatus.Margin = new Thickness(0, 6, 0, 10);
            FormRow(fields, "Обложка записи (необязательно)", coverStatus);
            WrapPanel coverButtons = new WrapPanel();
            fields.Children.Add(coverButtons);
            Button coverFile = ActionButton("Выбрать файл…", false);
            coverFile.Click += delegate
            {
                OpenFileDialog picker = new OpenFileDialog
                {
                    Title = "Обложка для записи", CheckFileExists = true,
                    Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif"
                };
                if (picker.ShowDialog(dialog) != true) return;
                coverChoice = "file"; coverSource = picker.FileName;
                coverStatus.Text = "Новая обложка из файла: " + Path.GetFileName(picker.FileName);
            };
            coverButtons.Children.Add(coverFile);
            Button coverUrl = ActionButton("По ссылке…", false);
            coverUrl.Click += delegate
            {
                Window addressDialog = Dialog(dialog, "Обложка по ссылке", 470, 300);
                DockPanel addressRoot = new DockPanel { Margin = new Thickness(18) };
                addressDialog.Content = addressRoot;
                WrapPanel addressButtons = new WrapPanel { Margin = new Thickness(0, 13, 0, 0) };
                DockPanel.SetDock(addressButtons, Dock.Bottom);
                addressRoot.Children.Add(addressButtons);
                StackPanel addressForm = new StackPanel();
                addressRoot.Children.Add(addressForm);
                TextBox input = Input(coverChoice == "url" ? coverSource : "", false);
                FormRow(addressForm, "Прямая HTTP(S)-ссылка на изображение", input);
                TextBlock addressError = Label("", 11, FluentUi.Amber);
                addressError.Margin = new Thickness(0, 9, 0, 0);
                addressForm.Children.Add(addressError);
                Button apply = ActionButton("Использовать", true);
                apply.IsDefault = true;
                apply.Click += delegate
                {
                    if (!IsValidWebAddress(input.Text.Trim()))
                    {
                        addressError.Text = "Укажи полный HTTP(S)-адрес изображения.";
                        return;
                    }
                    coverChoice = "url"; coverSource = input.Text.Trim();
                    coverStatus.Text = "Новая обложка будет загружена при сохранении.";
                    addressDialog.DialogResult = true;
                };
                addressButtons.Children.Add(apply);
                Button close = ActionButton("Отмена", false);
                close.IsCancel = true;
                close.Click += delegate { addressDialog.DialogResult = false; };
                addressButtons.Children.Add(close);
                addressDialog.ShowDialog();
            };
            coverButtons.Children.Add(coverUrl);
            Button clearCover = ActionButton("Убрать обложку", false);
            clearCover.Click += delegate
            {
                coverChoice = "remove"; coverSource = null;
                coverStatus.Text = "Обложка будет удалена из записи после сохранения.";
            };
            coverButtons.Children.Add(clearCover);

            Button pasteProduct = ActionButton("Из скопированного текста…", false);
            TextBlock fillStatus = Label("Выдели на странице товара название, цену и описание, " +
                "нажми Ctrl+C и открой импорт. Сайт из приложения не запрашивается.", 11,
                FluentUi.Secondary);
            fillStatus.Margin = new Thickness(0, 5, 0, 10);
            fields.Children.Add(pasteProduct);
            fields.Children.Add(fillStatus);
            pasteProduct.Click += delegate
            {
                Window pasted = Dialog(dialog, "Импорт из текста страницы", 620, 660);
                DockPanel pasteRoot = new DockPanel { Margin = new Thickness(18) };
                pasted.Content = pasteRoot;
                WrapPanel pasteButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                DockPanel.SetDock(pasteButtons, Dock.Bottom);
                pasteRoot.Children.Add(pasteButtons);
                ScrollViewer pasteScroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                FluentUi.StyleSettingsScroll(pasteScroll);
                pasteRoot.Children.Add(pasteScroll);
                StackPanel pasteForm = new StackPanel();
                pasteScroll.Content = pasteForm;
                TextBlock howTo = Label(
                    "1. Открой товар в браузере и выдели название, цену и описание. " +
                    "2. Скопируй выделение (Ctrl+C). 3. Вставь сюда и нажми «Распознать». " +
                    "4. Исправь предложенные данные и нажми «Применить».", 12, FluentUi.Secondary);
                howTo.Margin = new Thickness(0, 0, 0, 10);
                pasteForm.Children.Add(howTo);
                TextBox copied = Input("", true);
                copied.Height = 140;
                copied.MaxLength = 120000;
                FormRow(pasteForm, "Текст страницы или выбранного блока", copied);
                WrapPanel clipboardActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                pasteForm.Children.Add(clipboardActions);
                Button fromClipboard = ActionButton("Вставить из буфера", false);
                fromClipboard.Click += delegate
                {
                    try
                    {
                        if (!Clipboard.ContainsText())
                        {
                            copied.Text = "";
                            return;
                        }
                        copied.Text = Clipboard.GetText();
                    }
                    catch (Exception clipboardError)
                    {
                        MessageBox.Show(pasted, "Не удалось прочитать буфер обмена: " +
                            clipboardError.Message, "AppShelf",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                clipboardActions.Children.Add(fromClipboard);
                Button recognize = ActionButton("Распознать", true);
                clipboardActions.Children.Add(recognize);

                TextBlock warning = Label("Это предварительные значения. " +
                    "При копировании целой страницы заголовок и цена могут определиться неверно; " +
                    "перед применением проверь их. Обложка добавляется отдельно.",
                    11, FluentUi.Secondary);
                warning.Margin = new Thickness(0, 9, 0, 12);
                pasteForm.Children.Add(warning);
                TextBox parsedTitle = Input("", false);
                parsedTitle.MaxLength = 200;
                FormRow(pasteForm, "Предполагаемое название", parsedTitle);
                TextBox parsedDescription = Input("", true);
                parsedDescription.Height = 95;
                parsedDescription.MaxLength = 5000;
                FormRow(pasteForm, "Описание", parsedDescription);
                TextBox parsedPrice = Input("", false);
                parsedPrice.MaxLength = 120;
                FormRow(pasteForm, "Цена (ориентировочная)", parsedPrice);
                TextBox parsedBrand = Input("", false);
                parsedBrand.MaxLength = 200;
                FormRow(pasteForm, "Бренд", parsedBrand);
                TextBox parsedSku = Input("", false);
                parsedSku.MaxLength = 200;
                FormRow(pasteForm, "Артикул", parsedSku);
                TextBox parsedUrl = Input("", false);
                parsedUrl.MaxLength = 2048;
                FormRow(pasteForm, "Ссылка, если была в тексте", parsedUrl);

                TextBlock result = Label("", 11, FluentUi.Amber);
                result.Margin = new Thickness(0, 10, 0, 0);
                pasteForm.Children.Add(result);
                Button apply = ActionButton("Применить к пустым полям", true);
                apply.IsEnabled = false;
                pasteButtons.Children.Add(apply);
                Button cancelPaste = ActionButton("Отмена", false);
                cancelPaste.IsCancel = true;
                cancelPaste.Click += delegate { pasted.DialogResult = false; };
                pasteButtons.Children.Add(cancelPaste);
                recognize.Click += delegate
                {
                    try
                    {
                        PastedProductPreview product = ProductTextReader.Parse(copied.Text);
                        parsedTitle.Text = product.Title ?? "";
                        parsedDescription.Text = product.Description ?? "";
                        parsedPrice.Text = product.Price ?? "";
                        parsedBrand.Text = product.Brand ?? "";
                        parsedSku.Text = product.Sku ?? "";
                        parsedUrl.Text = product.SourceUrl ?? "";
                        result.Foreground = FluentUi.Secondary;
                        result.Text = "Проверь распознанные значения. Незаполненные поля можно дописать вручную.";
                        apply.IsEnabled = true;
                    }
                    catch (Exception parseError)
                    {
                        ConfigStore.Log(parseError);
                        result.Foreground = FluentUi.Amber;
                        result.Text = "Не удалось распознать текст: " + parseError.Message;
                        apply.IsEnabled = false;
                    }
                };
                apply.Click += delegate
                {
                    if (!String.IsNullOrWhiteSpace(parsedUrl.Text) &&
                        !IsValidWebAddress(parsedUrl.Text.Trim()))
                    {
                        result.Foreground = FluentUi.Amber;
                        result.Text = "В поле ссылки нужен полный адрес HTTP(S).";
                        return;
                    }
                    List<string> applied = new List<string>();
                    if (String.IsNullOrWhiteSpace(title.Text) &&
                        !String.IsNullOrWhiteSpace(parsedTitle.Text))
                    {
                        title.Text = parsedTitle.Text.Trim();
                        applied.Add("название");
                    }
                    if (String.IsNullOrWhiteSpace(description.Text) &&
                        !String.IsNullOrWhiteSpace(parsedDescription.Text))
                    {
                        description.Text = parsedDescription.Text.Trim();
                        applied.Add("описание");
                    }
                    if (String.IsNullOrWhiteSpace(productUrl.Text) &&
                        !String.IsNullOrWhiteSpace(parsedUrl.Text))
                    {
                        productUrl.Text = parsedUrl.Text.Trim();
                        applied.Add("ссылка");
                    }
                    foreach (CollectionField field in collection.Fields)
                    {
                        TextBox target = values[field.Key];
                        if (!String.IsNullOrWhiteSpace(target.Text)) continue;
                        string key = (field.Key + " " + field.Name).ToLowerInvariant();
                        string supplied = null;
                        if (field.Type == "url" && (key.Contains("link") ||
                            key.Contains("url") || key.Contains("ссыл") ||
                            key.Contains("товар")))
                            supplied = !String.IsNullOrWhiteSpace(productUrl.Text)
                                ? productUrl.Text.Trim() : parsedUrl.Text.Trim();
                        else if (key.Contains("price") || key.Contains("cost") ||
                            key.Contains("цена") || key.Contains("стоимость"))
                            supplied = parsedPrice.Text.Trim();
                        else if (key.Contains("brand") || key.Contains("бренд") ||
                            key.Contains("производитель"))
                            supplied = parsedBrand.Text.Trim();
                        else if (key.Contains("sku") || key.Contains("артикул"))
                            supplied = parsedSku.Text.Trim();
                        if (String.IsNullOrWhiteSpace(supplied)) continue;
                        if (target.MaxLength > 0 && supplied.Length > target.MaxLength)
                            supplied = supplied.Substring(0, target.MaxLength);
                        target.Text = supplied;
                        applied.Add(field.Name);
                    }
                    fillStatus.Foreground = FluentUi.Secondary;
                    fillStatus.Text = applied.Count > 0
                        ? "Подставлено: " + String.Join(", ", applied.ToArray()) +
                          ". Проверь запись и сохрани её."
                        : "Ничего не перезаписано: подходящие поля уже заполнены или отсутствуют.";
                    pasted.DialogResult = true;
                };
                pasted.ShowDialog();
            };

            CheckBox completed = null;
            if (IsPurchaseCollection(collection))
            {
                completed = new CheckBox
                {
                    Content = collection.Template == "shopping" ? "Куплено" : "Приобретено",
                    IsChecked = existing != null && existing.Completed,
                    Foreground = FluentUi.Foreground, Margin = new Thickness(0, 15, 0, 8)
                };
                fields.Children.Add(completed);
            }
            TextBlock error = Label("", 11, FluentUi.Amber);
            error.Margin = new Thickness(0, 10, 0, 0);
            fields.Children.Add(error);
            Button save = ActionButton("Сохранить", true);
            Button cancel = ActionButton("Отмена", false);
            save.IsDefault = true;
            bool saving = false;
            dialog.Closing += delegate(object sender, CancelEventArgs args)
            { if (saving) args.Cancel = true; };
            save.Click += async delegate
            {
                if (!ValidTitle(title.Text, 200))
                {
                    error.Text = "Название должно содержать от 1 до 200 символов.";
                    return;
                }
                if (!String.IsNullOrWhiteSpace(productUrl.Text) &&
                    (productUrl.Text.Trim().Length > 2048 ||
                     !IsValidWebAddress(productUrl.Text.Trim())))
                {
                    error.Text = "Ссылка на товар должна быть полным адресом HTTP(S).";
                    return;
                }
                List<string> uniqueTags = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string rawTag in tags.Text.Split(','))
                {
                    string tag = rawTag.Trim();
                    if (tag.Length == 0) continue;
                    if (tag.Length > 60) { error.Text = "Тег не должен быть длиннее 60 символов."; return; }
                    if (seen.Add(tag)) uniqueTags.Add(tag);
                }
                foreach (CollectionField field in collection.Fields)
                {
                    string value = values[field.Key].Text.Trim();
                    if (field.Type == "url" && value.Length > 0)
                    {
                        Uri uri;
                        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            error.Text = "Поле «" + field.Name + "»: используй полный HTTP(S)-адрес.";
                            return;
                        }
                    }
                }
                CollectionDocument pending = _document.Copy();
                CollectionDefinition targetCollection = pending.Collections.Find(
                    delegate(CollectionDefinition item) { return item.Id == collection.Id; });
                if (targetCollection == null) return;
                CollectionEntry target;
                if (isNew)
                {
                    target = new CollectionEntry { Id = Guid.NewGuid().ToString("N") };
                    targetCollection.Entries.Add(target);
                }
                else
                    target = targetCollection.Entries.Find(
                        delegate(CollectionEntry entry) { return entry.Id == existing.Id; });
                if (target == null) return;
                target.Title = title.Text.Trim();
                target.SourceUrl = productUrl.Text.Trim();
                target.Description = description.Text.Trim();
                target.Tags = uniqueTags;
                target.Values.Clear();
                foreach (CollectionField field in collection.Fields)
                {
                    string value = values[field.Key].Text.Trim();
                    if (value.Length > 0)
                        target.Values.Add(new CollectionValue { Key = field.Key, Value = value });
                }
                if (completed != null) target.Completed = completed.IsChecked == true;
                string preparedCover = null;
                bool committed = false;
                saving = true;
                save.IsEnabled = false;
                cancel.IsEnabled = false;
                try
                {
                    if (coverChoice == "file")
                    {
                        string path = coverSource;
                        preparedCover = await Task.Run(delegate
                        { return CollectionStore.ImportCoverFile(path); });
                    }
                    else if (coverChoice == "url")
                    {
                        string url = coverSource;
                        preparedCover = await Task.Run(delegate
                        { return CollectionStore.ImportCoverUrl(url); });
                    }
                    if (coverChoice == "remove") target.CoverFile = null;
                    else if (preparedCover != null) target.CoverFile = preparedCover;
                    if (!SaveChanges(pending))
                    {
                        if (preparedCover != null) CollectionStore.DiscardPreparedCover(preparedCover);
                        return;
                    }
                    committed = true;
                    _selectedEntryId = target.Id;
                    RefreshEntries();
                    saving = false;
                    dialog.DialogResult = true;
                }
                catch (Exception imageError)
                {
                    ConfigStore.Log(imageError);
                    if (!committed && preparedCover != null)
                        CollectionStore.DiscardPreparedCover(preparedCover);
                    error.Text = committed ? "Запись сохранена, но обновление окна завершилось с ошибкой."
                        : "Не удалось сохранить обложку: " + imageError.Message;
                }
                finally
                {
                    saving = false;
                    save.IsEnabled = true;
                    cancel.IsEnabled = true;
                }
            };
            actions.Children.Add(save);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.DialogResult = false; };
            actions.Children.Add(cancel);
            dialog.Loaded += delegate { title.Focus(); title.CaretIndex = title.Text.Length; };
            dialog.ShowDialog();
        }

        private void DeleteEntry()
        {
            CollectionDefinition collection = CurrentCollection();
            CollectionEntry entry = CurrentEntry(collection);
            if (!_canSave || entry == null) return;
            if (MessageBox.Show(this, "Удалить запись «" + entry.Title + "»?",
                "AppShelf — подборки", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            CollectionDocument pending = _document.Copy();
            CollectionDefinition destination = pending.Collections.Find(
                delegate(CollectionDefinition item) { return item.Id == collection.Id; });
            if (destination == null) return;
            destination.Entries.RemoveAll(delegate(CollectionEntry item) { return item.Id == entry.Id; });
            if (!SaveChanges(pending)) return;
            _selectedEntryId = null;
            RefreshEntries();
        }

        private void ConfirmEntry()
        {
            CollectionDefinition group = CurrentCollection();
            CollectionEntry entry = CurrentEntry(group);
            if (!_canSave || entry == null) return;
            CollectionDocument pending = _document.Copy();
            CollectionDefinition selectedGroup = pending.Collections.Find(
                delegate(CollectionDefinition item) { return item.Id == group.Id; });
            CollectionEntry selectedEntry = selectedGroup.Entries.Find(
                delegate(CollectionEntry item) { return item.Id == entry.Id; });
            selectedEntry.History.Add(new CollectionAction
            {
                AtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Label = group.ConfirmationLabel
            });
            if (IsPurchaseCollection(group)) selectedEntry.Completed = true;
            if (!SaveChanges(pending)) return;
            RefreshEntries();
        }

        // All selected tags must match. Recent confirmations reduce the chance but
        // never remove an entry from the random draw.
        private static double Weight(CollectionEntry entry, DateTime nowUtc)
        {
            if (entry.History.Count == 0) return 1.0;
            DateTime last;
            if (!DateTime.TryParse(entry.History[entry.History.Count - 1].AtUtc,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last))
                return 1.0;
            double days = Math.Max(0.0, (nowUtc - last.ToUniversalTime()).TotalDays);
            return 0.25 + 0.75 * (1.0 - Math.Exp(-days / 14.0));
        }

        private void ChooseRandom()
        {
            CollectionDefinition group = CurrentCollection();
            List<CollectionEntry> choices = FilteredEntries(group);
            if (choices.Count == 0) return;
            List<CollectionEntry> eligible = new List<CollectionEntry>(choices);
            // Avoid showing the same item twice in a row when alternatives exist.
            if (eligible.Count > 1 && _lastRandomEntryId != null) eligible.RemoveAll(
                delegate(CollectionEntry item) { return item.Id == _lastRandomEntryId; });
            double total = 0;
            DateTime now = DateTime.UtcNow;
            foreach (CollectionEntry item in eligible) total += Weight(item, now);
            double target = _random.NextDouble() * total;
            CollectionEntry chosen = eligible[eligible.Count - 1];
            foreach (CollectionEntry item in eligible)
            {
                target -= Weight(item, now);
                if (target <= 0) { chosen = item; break; }
            }
            _selectedEntryId = chosen.Id;
            _lastRandomEntryId = chosen.Id;
            _choose.Content = "Другой вариант";
            _refreshing = true;
            try
            {
                foreach (ListBoxItem row in _entriesList.Items)
                    if ((string)row.Tag == chosen.Id) { _entriesList.SelectedItem = row; break; }
            }
            finally { _refreshing = false; }
            RefreshDetails();
        }
    }

    public sealed partial class MainWindow
    {
        private CollectionsWindow _collectionsWindow;

        private void ShowCollectionsWindow()
        {
            if (_config.ShowCollections == false) return;
            if (_collectionsWindow != null)
            {
                if (_collectionsWindow.WindowState == WindowState.Minimized)
                    _collectionsWindow.WindowState = WindowState.Normal;
                _collectionsWindow.Activate();
                return;
            }
            _collectionsWindow = new CollectionsWindow(this);
            _collectionsWindow.Closed += delegate { _collectionsWindow = null; };
            _collectionsWindow.Show();
        }
    }
}
