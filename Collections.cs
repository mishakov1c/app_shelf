// Local user collections. No cloud, preloaded user entries, or dependency on AppShelf launcher data.
// Written for the Windows .NET Framework C# compiler as well as the .NET 8 project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace AppShelf
{
    [DataContract]
    public sealed class CollectionField
    {
        [DataMember(Name = "key")] public string Key;
        [DataMember(Name = "name")] public string Name;
        // text, multiline, url
        [DataMember(Name = "type")] public string Type = "text";

        public CollectionField Copy()
        {
            return new CollectionField { Key = Key, Name = Name, Type = Type };
        }
    }

    [DataContract]
    public sealed class CollectionValue
    {
        [DataMember(Name = "key")] public string Key;
        [DataMember(Name = "value")] public string Value;

        public CollectionValue Copy()
        {
            return new CollectionValue { Key = Key, Value = Value };
        }
    }

    [DataContract]
    public sealed class CollectionAction
    {
        // ISO 8601 UTC timestamp, the user must explicitly confirm to create a history event.
        [DataMember(Name = "atUtc")] public string AtUtc;
        [DataMember(Name = "label")] public string Label;

        public CollectionAction Copy()
        {
            return new CollectionAction { AtUtc = AtUtc, Label = Label };
        }
    }

    [DataContract]
    public sealed class CollectionEntry
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "description", EmitDefaultValue = false)] public string Description;
        // Optional source product page; unlike coverFile this is a shareable URL.
        [DataMember(Name = "sourceUrl", EmitDefaultValue = false)] public string SourceUrl;
        [DataMember(Name = "tags")] public List<string> Tags = new List<string>();
        [DataMember(Name = "values")] public List<CollectionValue> Values = new List<CollectionValue>();
        [DataMember(Name = "completed", EmitDefaultValue = false)] public bool Completed;
        // Filename in Collections/Images, not a machine-specific path or remote URL.
        [DataMember(Name = "coverFile", EmitDefaultValue = false)] public string CoverFile;
        [DataMember(Name = "history")] public List<CollectionAction> History = new List<CollectionAction>();

        public CollectionEntry Copy()
        {
            CollectionEntry next = new CollectionEntry
            {
                Id = Id, Title = Title, Description = Description, SourceUrl = SourceUrl,
                Completed = Completed, CoverFile = CoverFile
            };
            if (Tags != null) next.Tags.AddRange(Tags);
            if (Values != null) foreach (CollectionValue value in Values) next.Values.Add(value.Copy());
            if (History != null) foreach (CollectionAction action in History) next.History.Add(action.Copy());
            return next;
        }
    }

    [DataContract]
    public sealed class CollectionDefinition
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "name")] public string Name;
        // "custom" or a preset ID. Preset fields are copied, never loaded as user entries.
        [DataMember(Name = "template")] public string Template = "custom";
        [DataMember(Name = "confirmationLabel")] public string ConfirmationLabel = "Выбрано";
        [DataMember(Name = "fields")] public List<CollectionField> Fields = new List<CollectionField>();
        [DataMember(Name = "entries")] public List<CollectionEntry> Entries = new List<CollectionEntry>();

        public CollectionDefinition Copy()
        {
            CollectionDefinition next = new CollectionDefinition
            {
                Id = Id, Name = Name, Template = Template, ConfirmationLabel = ConfirmationLabel
            };
            if (Fields != null) foreach (CollectionField field in Fields) next.Fields.Add(field.Copy());
            if (Entries != null) foreach (CollectionEntry entry in Entries) next.Entries.Add(entry.Copy());
            return next;
        }
    }

    [DataContract]
    public sealed class CollectionDocument
    {
        [DataMember(Name = "version")] public int Version = 1;
        [DataMember(Name = "collections")] public List<CollectionDefinition> Collections = new List<CollectionDefinition>();

        public CollectionDocument Copy()
        {
            CollectionDocument next = new CollectionDocument { Version = Version };
            if (Collections != null)
                foreach (CollectionDefinition collection in Collections) next.Collections.Add(collection.Copy());
            return next;
        }
    }

    // Accept both a full exported document and a single AI-authored collection:
    // {"name":"...","fields":[...],"entries":[...]}.
    // These fields are assigned by DataContractJsonSerializer, not application code.
#pragma warning disable 0649
    [DataContract]
    internal sealed class CollectionImportShape
    {
        [DataMember(Name = "version", EmitDefaultValue = false)] public int Version;
        [DataMember(Name = "collections", EmitDefaultValue = false)]
        public List<CollectionDefinition> Collections;
        [DataMember(Name = "name", EmitDefaultValue = false)] public string Name;
        [DataMember(Name = "template", EmitDefaultValue = false)] public string Template;
        [DataMember(Name = "confirmationLabel", EmitDefaultValue = false)] public string ConfirmationLabel;
        [DataMember(Name = "fields", EmitDefaultValue = false)] public List<CollectionField> Fields;
        [DataMember(Name = "entries", EmitDefaultValue = false)] public List<CollectionEntry> Entries;
    }
#pragma warning restore 0649

    internal sealed class CollectionTemplate
    {
        public readonly string Key;
        public readonly string Title;
        public readonly string Confirmation;
        public readonly List<CollectionField> Fields;

        public CollectionTemplate(string key, string title, string confirmation, params CollectionField[] fields)
        {
            Key = key; Title = title; Confirmation = confirmation;
            Fields = new List<CollectionField>(fields);
        }
    }

    internal static class CollectionTemplates
    {
        private static CollectionField Field(string key, string title, string type)
        {
            return new CollectionField { Key = key, Name = title, Type = type };
        }

        internal static readonly CollectionTemplate[] All = new CollectionTemplate[]
        {
            new CollectionTemplate("custom", "Пустая подборка", "Выбрано"),
            new CollectionTemplate("recipes", "Рецепты", "Приготовили",
                Field("ingredients", "Ингредиенты", "multiline"),
                Field("preparation", "Приготовление", "multiline"),
                Field("time", "Время приготовления", "text"),
                Field("link", "Ссылка", "url")),
            new CollectionTemplate("movies", "Фильмы и сериалы", "Посмотрели",
                Field("kind", "Тип", "text"), Field("genre", "Жанр", "text"),
                Field("year", "Год", "text"), Field("platform", "Платформа", "text"),
                Field("link", "Ссылка", "url")),
            new CollectionTemplate("games", "Игры", "Поиграли",
                Field("platform", "Платформа", "text"), Field("genre", "Жанр", "text"),
                Field("link", "Ссылка", "url"), Field("notes", "Заметки", "multiline")),
            new CollectionTemplate("places", "Места и досуг", "Посетили",
                Field("address", "Адрес", "text"), Field("price", "Стоимость", "text"),
                Field("link", "Ссылка", "url"), Field("notes", "Заметки", "multiline")),
            new CollectionTemplate("books", "Книги", "Прочитали",
                Field("author", "Автор", "text"), Field("genre", "Жанр", "text"),
                Field("link", "Ссылка", "url")),
            new CollectionTemplate("activities", "Идеи и занятия", "Выполнено",
                Field("category", "Категория", "text"), Field("requirements", "Необходимые условия", "multiline"),
                Field("notes", "Заметки", "multiline")),
            new CollectionTemplate("shopping", "Список покупок", "Куплено",
                Field("quantity", "Количество", "text"), Field("notes", "Заметки", "multiline")),
            new CollectionTemplate("wishlist", "Вишлист", "Приобретено",
                Field("link", "Ссылка", "url"), Field("price", "Ориентировочная цена", "text"),
                Field("priority", "Приоритет", "text"))
        };

        internal static CollectionTemplate Find(string key)
        {
            foreach (CollectionTemplate template in All)
                if (template.Key == key) return template;
            return All[0];
        }

        internal static CollectionDefinition Create(string template, string name)
        {
            CollectionTemplate preset = Find(template);
            CollectionDefinition collection = new CollectionDefinition
            {
                Id = Guid.NewGuid().ToString("N"), Name = name,
                Template = preset.Key, ConfirmationLabel = preset.Confirmation
            };
            foreach (CollectionField field in preset.Fields) collection.Fields.Add(field.Copy());
            return collection;
        }
    }

    internal sealed class PastedProductPreview
    {
        public string Title;
        public string Description;
        public string Price;
        public string Brand;
        public string Sku;
        public string SourceUrl;
    }

    // Parses only text explicitly pasted by the user. Does not access Ozon,
    // a browser profile or the network. Results are suggestions requiring review.
    internal static class ProductTextReader
    {
        private const int MaxInputLength = 120000;
        private static readonly Regex PricePattern = new Regex(
            @"(?<![\d])(?<amount>(?:\d{1,3}(?:[ \u00a0\u202f\u2009]\d{3})+|\d{2,8})(?:[.,]\d{1,2})?)\s*₽",
            RegexOptions.CultureInvariant);
        private static readonly Regex SiteLink = new Regex(
            @"https?://(?:www\.)?ozon\.ru/(?:product/[^/\s?]+/?|t/[a-z0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BrandLine = new Regex(
            @"^(?:бренд|производитель|brand)\s*[:—-]\s*(?<value>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ArticleLine = new Regex(
            @"^(?:артикул|код товара|sku)\s*(?:ozon)?\s*[:№#—-]\s*(?<value>[\w-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool LooksLikeNavigation(string line)
        {
            string value = line.ToLowerInvariant();
            if (line.Length < 12 || line.Length > 220) return true;
            string[] nav = {
                "каталог", "поиск", "войти", "корзина", "избранное",
                "доставка", "скидка", "оценка", "отзыв", "оплата", "бонус",
                "купить", "добавить", "покупателей", "продавец", "заказ",
                "выбрать", "рассрочка", "доступно", "баллы", "ozon карта"
            };
            foreach (string word in nav)
                if (value == word || value.StartsWith(word + " ") ||
                    value.StartsWith(word + ":")) return true;
            return PricePattern.IsMatch(line) || SiteLink.IsMatch(line) ||
                line.IndexOf("₽", StringComparison.Ordinal) >= 0;
        }

        private static string Clean(string text, int max)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string value = Regex.Replace(text.Replace('\u200b', ' ').Replace('\u00a0', ' ')
                .Replace('\u202f', ' ').Replace('\u2009', ' '), @"[ \t]+", " ").Trim();
            return value.Length > max ? value.Substring(0, max) : value;
        }

        internal static PastedProductPreview Parse(string clipboardText)
        {
            if (String.IsNullOrWhiteSpace(clipboardText))
                throw new InvalidDataException("Нет текста для распознавания. Скопируй название, цену и описание товара.");
            if (clipboardText.Length > MaxInputLength)
                throw new InvalidDataException("Скопировано слишком много текста. Выдели только блок товара.");

            string normalized = clipboardText.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] raw = normalized.Split('\n');
            List<string> lines = new List<string>();
            foreach (string line in raw)
            {
                string cleaned = Clean(line, 400);
                if (cleaned.Length > 0) lines.Add(cleaned);
            }

            PastedProductPreview result = new PastedProductPreview();
            int firstPriceLine = -1;
            int titleLine = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                Match price = PricePattern.Match(line);
                if (price.Success && result.Price == null)
                {
                    string amount = Clean(price.Groups["amount"].Value, 120);
                    result.Price = amount + " ₽";
                    firstPriceLine = i;
                }
                Match brand = BrandLine.Match(line);
                if (brand.Success && String.IsNullOrWhiteSpace(result.Brand))
                    result.Brand = Clean(brand.Groups["value"].Value, 200);
                Match article = ArticleLine.Match(line);
                if (article.Success && String.IsNullOrWhiteSpace(result.Sku))
                    result.Sku = Clean(article.Groups["value"].Value, 100);
                Match link = SiteLink.Match(line);
                if (link.Success && result.SourceUrl == null)
                    result.SourceUrl = link.Value;
                if (line.StartsWith("Название:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Наименование:", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    string explicitTitle = Clean(line.Substring(colon + 1), 200);
                    if (explicitTitle.Length > 0)
                    {
                        result.Title = explicitTitle;
                        titleLine = i;
                    }
                }
            }

            if (String.IsNullOrEmpty(result.Title))
            {
                // Prefer the heading close to the first price; otherwise fall back
                // to the first plausible line in the user's own selection.
                int start = firstPriceLine < 0 ? 0 : Math.Max(0, firstPriceLine - 8);
                int end = firstPriceLine < 0 ? lines.Count : Math.Min(lines.Count, firstPriceLine + 1);
                for (int i = start; i < end; i++)
                {
                    string line = lines[i];
                    if (LooksLikeNavigation(line)) continue;
                    if (line.Length > 200) continue;
                    if (titleLine < 0 || line.Length > lines[titleLine].Length)
                    {
                        titleLine = i;
                        result.Title = line;
                    }
                }
                if (String.IsNullOrEmpty(result.Title))
                    for (int i = 0; i < lines.Count; i++)
                        if (!LooksLikeNavigation(lines[i]) && lines[i].Length <= 200)
                        {
                            result.Title = lines[i];
                            titleLine = i;
                            break;
                        }
            }

            // Description is taken only from an explicitly marked section, not
            // guessed from arbitrary product-page navigation or seller offers.
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.StartsWith("Описание:", StringComparison.OrdinalIgnoreCase))
                {
                    string inlineValue = Clean(line.Substring(line.IndexOf(':') + 1), 5000);
                    if (inlineValue.Length > 0)
                    {
                        result.Description = inlineValue;
                        break;
                    }
                }
                if (!line.Equals("Описание", StringComparison.OrdinalIgnoreCase) &&
                    !line.Equals("Описание товара", StringComparison.OrdinalIgnoreCase))
                    continue;
                StringBuilder description = new StringBuilder();
                for (int j = i + 1; j < lines.Count && j < i + 20; j++)
                {
                    string next = lines[j];
                    string lowered = next.ToLowerInvariant();
                    if (lowered == "характеристики" || lowered == "отзывы" ||
                        lowered == "вопросы" || lowered == "доставка" ||
                        lowered == "похожие товары" || lowered == "о товаре")
                        break;
                    if (description.Length + next.Length > 1500) break;
                    if (description.Length > 0) description.AppendLine();
                    description.Append(next);
                }
                if (description.Length > 0) result.Description = description.ToString();
                break;
            }
            return result;
        }
    }

    internal static class CollectionStore
    {
        internal static readonly string DirectoryPath = Path.Combine(ConfigStore.DirectoryPath, "Collections");
        internal static readonly string FilePath = Path.Combine(DirectoryPath, "collections.json");
        internal static readonly string ImagesPath = Path.Combine(DirectoryPath, "Images");
        internal const long MaxDocumentBytes = 16L * 1024 * 1024;
        private const long MaxCoverSourceBytes = 6L * 1024 * 1024;
        private const long MaxStoredCoverBytes = 8L * 1024 * 1024;
        private static readonly Regex CoverNamePattern = new Regex(
            "^[a-fA-F0-9]{32}\\.png$", RegexOptions.CultureInvariant);

        internal static void Validate(CollectionDocument document)
        {
            if (document == null || document.Version != 1 || document.Collections == null)
                throw new InvalidDataException("Неподдерживаемый файл подборок.");
            if (document.Collections.Count > 200)
                throw new InvalidDataException("Слишком много подборок.");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (CollectionDefinition group in document.Collections)
            {
                if (group == null || String.IsNullOrWhiteSpace(group.Id) || !ids.Add(group.Id) ||
                    String.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 120 ||
                    String.IsNullOrWhiteSpace(group.ConfirmationLabel) || group.ConfirmationLabel.Length > 60 ||
                    group.Fields == null || group.Entries == null ||
                    group.Fields.Count > 40 || group.Entries.Count > 10000)
                    throw new InvalidDataException("Повреждена подборка или её описание.");

                HashSet<string> fieldKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (CollectionField field in group.Fields)
                {
                    if (field == null || String.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 80 ||
                        !fieldKeys.Add(field.Key) || String.IsNullOrWhiteSpace(field.Name) ||
                        field.Name.Length > 120 || (field.Type != "text" && field.Type != "multiline" &&
                        field.Type != "url"))
                        throw new InvalidDataException("Некорректное поле подборки.");
                }
                HashSet<string> entryIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (CollectionEntry entry in group.Entries)
                {
                    if (entry == null || String.IsNullOrWhiteSpace(entry.Id) || !entryIds.Add(entry.Id) ||
                        String.IsNullOrWhiteSpace(entry.Title) || entry.Title.Length > 200 ||
                        entry.Tags == null || entry.Values == null || entry.History == null)
                        throw new InvalidDataException("Некорректная запись подборки.");
                    if (entry.Tags.Count > 200 || entry.History.Count > 3000 ||
                        entry.Values.Count > group.Fields.Count)
                        throw new InvalidDataException("Слишком много тегов, полей или событий истории.");
                    if (entry.Description != null && entry.Description.Length > 5000)
                        throw new InvalidDataException("Слишком длинное описание записи.");
                    Uri sourceAddress;
                    if (!String.IsNullOrEmpty(entry.SourceUrl) &&
                        (entry.SourceUrl.Length > 2048 ||
                         !Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out sourceAddress) ||
                         (sourceAddress.Scheme != Uri.UriSchemeHttp &&
                          sourceAddress.Scheme != Uri.UriSchemeHttps)))
                        throw new InvalidDataException("Недопустимая ссылка на товар.");
                    if (entry.CoverFile != null && !ValidCoverFile(entry.CoverFile))
                        throw new InvalidDataException("Небезопасная ссылка на обложку.");
                    HashSet<string> valueKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (CollectionValue value in entry.Values)
                        if (value == null || !fieldKeys.Contains(value.Key) ||
                            !valueKeys.Add(value.Key) || value.Value == null || value.Value.Length > 10000)
                            throw new InvalidDataException("Некорректные значения дополнительных полей.");
                    foreach (string tag in entry.Tags)
                        if (String.IsNullOrWhiteSpace(tag) || tag.Length > 60)
                            throw new InvalidDataException("Некорректный тег записи.");
                    foreach (CollectionAction action in entry.History)
                    {
                        DateTime stamp;
                        if (action == null || !DateTime.TryParse(action.AtUtc,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out stamp) ||
                            String.IsNullOrWhiteSpace(action.Label) || action.Label.Length > 60)
                            throw new InvalidDataException("Некорректная история записи.");
                    }
                }
            }
        }

        internal static bool ValidCoverFile(string filename)
        {
            return !String.IsNullOrEmpty(filename) &&
                CoverNamePattern.IsMatch(filename);
        }

        internal static string CoverPath(string filename)
        {
            if (!ValidCoverFile(filename)) throw new InvalidDataException("Недопустимое имя обложки.");
            return Path.Combine(ImagesPath, filename);
        }

        // Produce a normalized local PNG instead of storing source paths or live web URLs.
        // Copy is created before the corresponding JSON update; callers clean up on write failure.
        private static string SaveCoverPng(Stream source)
        {
            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1200;
            image.StreamSource = source;
            image.EndInit();
            image.Freeze();

            Directory.CreateDirectory(ImagesPath);
            string filename = Guid.NewGuid().ToString("N") + ".png";
            string destination = CoverPath(filename);
            try
            {
                using (FileStream output = new FileStream(destination, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(output);
                    output.Flush(true);
                    if (output.Length == 0 || output.Length > MaxStoredCoverBytes)
                        throw new InvalidDataException("Размер сохранённой обложки слишком велик.");
                }
                return filename;
            }
            catch
            {
                try { if (File.Exists(destination)) File.Delete(destination); }
                catch (Exception error) { ConfigStore.Log(error); }
                throw;
            }
        }

        internal static string ImportCoverFile(string source)
        {
            FileInfo info = new FileInfo(source);
            if (!info.Exists) throw new FileNotFoundException("Не найден файл обложки.", source);
            if (info.Length == 0 || info.Length > MaxCoverSourceBytes)
                throw new InvalidDataException("Файл обложки должен быть меньше 6 МБ.");
            using (FileStream input = File.OpenRead(source))
                return SaveCoverPng(input);
        }

        internal static string ImportCoverUrl(string url)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) ||
                (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("Нужна ссылка HTTP(S) на изображение.");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.UserAgent = "AppShelf/2.17";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 4;
            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (MemoryStream bytes = new MemoryStream())
            {
                if (input == null || response.ContentLength > MaxCoverSourceBytes)
                    throw new InvalidDataException("Изображение слишком велико или недоступно.");
                byte[] chunk = new byte[16384];
                int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (bytes.Length + read > MaxCoverSourceBytes)
                        throw new InvalidDataException("Изображение превышает 6 МБ.");
                    bytes.Write(chunk, 0, read);
                }
                if (bytes.Length == 0) throw new InvalidDataException("Получен пустой файл.");
                bytes.Position = 0;
                return SaveCoverPng(bytes);
            }
        }

        // Safe only for files created during an uncommitted attempt. Committed
        // covers are preserved for the .bak document until backup policy is finalized.
        internal static void DiscardPreparedCover(string filename)
        {
            if (!ValidCoverFile(filename)) return;
            try
            {
                string path = CoverPath(filename);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception error) { ConfigStore.Log(error); }
        }

        internal static CollectionDocument ReadDocument(Stream stream)
        {
            CollectionDocument document = (CollectionDocument)new DataContractJsonSerializer(
                typeof(CollectionDocument)).ReadObject(stream);
            Validate(document);
            return document;
        }

        private static void NormalizeImported(CollectionDefinition group)
        {
            if (group == null) return;
            group.Id = Guid.NewGuid().ToString("N");
            if (String.IsNullOrWhiteSpace(group.Template)) group.Template = "custom";
            if (String.IsNullOrWhiteSpace(group.ConfirmationLabel)) group.ConfirmationLabel = "Выбрано";
            if (group.Fields == null) group.Fields = new List<CollectionField>();
            if (group.Entries == null) group.Entries = new List<CollectionEntry>();
            foreach (CollectionField field in group.Fields)
            {
                if (field == null) continue;
                if (String.IsNullOrWhiteSpace(field.Key))
                    field.Key = Guid.NewGuid().ToString("N");
                if (String.IsNullOrWhiteSpace(field.Type)) field.Type = "text";
            }
            foreach (CollectionEntry entry in group.Entries)
            {
                if (entry == null) continue;
                entry.Id = Guid.NewGuid().ToString("N");
                if (entry.Tags == null) entry.Tags = new List<string>();
                if (entry.Values == null) entry.Values = new List<CollectionValue>();
                if (entry.History == null) entry.History = new List<CollectionAction>();
                // JSON transfers records but not local binary covers. Full ZIP backups do.
                entry.CoverFile = null;
            }
        }

        internal static List<CollectionDefinition> ImportJson(string filename)
        {
            FileInfo info = new FileInfo(filename);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxDocumentBytes)
                throw new InvalidDataException("JSON должен быть непустым файлом размером до 16 МБ.");
            CollectionImportShape input;
            using (FileStream source = File.OpenRead(filename))
                input = (CollectionImportShape)new DataContractJsonSerializer(
                    typeof(CollectionImportShape)).ReadObject(source);
            if (input == null || (input.Version != 0 && input.Version != 1))
                throw new InvalidDataException("Неподдерживаемая версия подборок.");
            List<CollectionDefinition> imported = input.Collections;
            if (imported == null)
            {
                if (String.IsNullOrWhiteSpace(input.Name))
                    throw new InvalidDataException("В JSON нет названия подборки или массива collections.");
                imported = new List<CollectionDefinition>
                {
                    new CollectionDefinition
                    {
                        Name = input.Name, Template = input.Template,
                        ConfirmationLabel = input.ConfirmationLabel,
                        Fields = input.Fields, Entries = input.Entries
                    }
                };
            }
            CollectionDocument candidate = new CollectionDocument { Collections = imported };
            foreach (CollectionDefinition group in candidate.Collections) NormalizeImported(group);
            Validate(candidate);
            return candidate.Collections;
        }

        // For sharing / AI: export one complete text-only collection. Images are
        // intentionally not represented as broken local file references.
        internal static void ExportJson(string filename, CollectionDefinition collection)
        {
            if (collection == null) throw new ArgumentNullException("collection");
            CollectionDefinition portable = collection.Copy();
            foreach (CollectionEntry entry in portable.Entries) entry.CoverFile = null;
            CollectionDocument document = new CollectionDocument();
            document.Collections.Add(portable);
            Validate(document);
            string temporary = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(CollectionDocument)).WriteObject(output, document);
                    output.Flush(true);
                    if (output.Length > MaxDocumentBytes)
                        throw new InvalidDataException("JSON подборки превышает допустимые 16 МиБ.");
                }
                if (File.Exists(filename)) File.Replace(temporary, filename, null);
                else File.Move(temporary, filename);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) { ConfigStore.Log(error); }
            }
        }

        private static CollectionDocument Read(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > MaxDocumentBytes)
                throw new InvalidDataException("Файл подборок превышает допустимый размер.");
            using (FileStream stream = File.OpenRead(path))
                return ReadDocument(stream);
        }

        // Never silently overwrite damaged existing data with a newly created empty document.
        internal static CollectionDocument Load(out string warning, out bool canSave)
        {
            warning = null; canSave = true;
            if (!File.Exists(FilePath))
            {
                if (!File.Exists(FilePath + ".bak")) return new CollectionDocument();
                canSave = false;
                try
                {
                    warning = "Основной collections.json отсутствует. Загружена резервная копия; сохранение отключено.";
                    return Read(FilePath + ".bak");
                }
                catch (Exception ex)
                {
                    ConfigStore.Log(ex);
                    warning = "Файл подборок отсутствует, резервная копия повреждена. Сохранение отключено.";
                    return new CollectionDocument();
                }
            }
            try { return Read(FilePath); }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                canSave = false;
                warning = "Не удалось прочитать Collections\\collections.json. Файл не будет перезаписан. " +
                    "Проверь сохранённый файл и его копию .bak. " + error.Message;
                try
                {
                    if (File.Exists(FilePath + ".bak")) return Read(FilePath + ".bak");
                }
                catch (Exception backupError) { ConfigStore.Log(backupError); }
                return new CollectionDocument();
            }
        }

        internal static void Save(CollectionDocument document)
        {
            Validate(document);
            Directory.CreateDirectory(DirectoryPath);
            string temporary = FilePath + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(CollectionDocument)).WriteObject(stream, document);
                    stream.Flush(true);
                    // Enforce the same limit on save, reload and ZIP restore.
                    // On rejection only the temporary file is deleted; the
                    // current collections.json and its .bak remain untouched.
                    if (stream.Length > MaxDocumentBytes)
                        throw new InvalidDataException(
                            "Подборки занимают более 16 МиБ. Изменения не сохранены; " +
                            "уменьши размер данных или импортируемой подборки.");
                }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", true);
                else File.Move(temporary, FilePath);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) { ConfigStore.Log(ex); }
            }
        }
    }
}
