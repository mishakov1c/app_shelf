// AppShelf 2.0 visual system. Pure WPF, no external themes/packages.
// Compatible with the in-box .NET Framework compiler used by Build-AppShelf.cmd.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AppShelf
{
    // User wallpapers live outside the EXE and remain available when the application updates.
    // A GUID prefix avoids collisions and prevents arbitrary paths in apps.json.
    internal static class CustomBackgroundStore
    {
        internal const long MaxFileBytes = 20L * 1024 * 1024;
        internal static readonly string DirectoryPath =
            Path.Combine(ConfigStore.DirectoryPath, "Backgrounds");

        internal static bool IsValidFileName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length < 38 || name.Length > 130 ||
                name[32] != '_' || name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                return false;
            Guid id;
            if (!Guid.TryParseExact(name.Substring(0, 32), "N", out id)) return false;
            string extension = Path.GetExtension(name).ToLowerInvariant();
            return extension == ".jpg" || extension == ".jpeg" || extension == ".png";
        }

        internal static bool IsCustomPreset(string preset)
        {
            return !String.IsNullOrEmpty(preset) &&
                preset.StartsWith("custom:", StringComparison.Ordinal) &&
                IsValidFileName(preset.Substring(7));
        }

        internal static string FilePath(string preset)
        {
            return IsCustomPreset(preset)
                ? Path.Combine(DirectoryPath, preset.Substring(7)) : null;
        }

        internal static string DisplayName(string preset)
        {
            if (!IsCustomPreset(preset)) return "";
            // Keep the original file name, including its extension, in the UI.
            // Only the GUID prefix is an internal implementation detail.
            return preset.Substring(7 + 33);
        }

        internal static List<string> ListPresets()
        {
            List<string> presets = new List<string>();
            if (!Directory.Exists(DirectoryPath)) return presets;
            foreach (string path in Directory.GetFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (IsValidFileName(name)) presets.Add("custom:" + name);
            }
            presets.Sort(delegate(string a, string b)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(DisplayName(a), DisplayName(b));
            });
            return presets;
        }

        internal static void ValidateImage(Stream stream, string extension, long maxBytes)
        {
            extension = extension.ToLowerInvariant();
            if (extension != ".jpg" && extension != ".jpeg" && extension != ".png")
                throw new InvalidDataException("Поддерживаются только JPG и PNG.");
            if (stream.Length == 0 || stream.Length > maxBytes)
                throw new InvalidDataException("Недопустимый размер изображения.");
            BitmapDecoder decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count < 1)
                throw new InvalidDataException("В файле нет изображения.");
            BitmapFrame frame = decoder.Frames[0];
            long pixelCount = (long)frame.PixelWidth * frame.PixelHeight;
            if (pixelCount == 0 || frame.PixelWidth > 8192 ||
                frame.PixelHeight > 8192 || pixelCount > 36000000)
                throw new InvalidDataException("Разрешение изображения слишком большое.");
            bool isJpeg = decoder is JpegBitmapDecoder;
            bool isPng = decoder is PngBitmapDecoder;
            if ((extension == ".png" && !isPng) || (extension != ".png" && !isJpeg))
                throw new InvalidDataException("Содержимое изображения не соответствует расширению.");
            // Reading metadata alone does not detect truncated pixel data.
            int stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
            byte[] row = new byte[stride];
            for (int y = 0; y < frame.PixelHeight; y++)
                frame.CopyPixels(new Int32Rect(0, y, frame.PixelWidth, 1), row, stride, 0);
        }

        // Publish only complete files. A failed copy never occupies a valid library name.
        internal static void CopyImageFile(string source, string destination, bool validate)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream input = File.OpenRead(source))
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    if (validate && (input.Length == 0 || input.Length > MaxFileBytes))
                        throw new InvalidDataException("Недопустимый размер изображения.");
                    input.CopyTo(output);
                    output.Flush(true);
                }
                if (validate)
                    using (FileStream image = File.OpenRead(temporary))
                        ValidateImage(image, Path.GetExtension(destination), MaxFileBytes);
                File.Move(temporary, destination);
            }
            catch (Exception copyError)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Не удалось удалить временный файл изображения.",
                        copyError, cleanupError);
                }
                throw;
            }
        }

        private const int MaxWallpaperWidth = 3840;
        private const int MaxWallpaperHeight = 2160;

        // Only the imported copy is resized; never rewrite the user's source.
        // Keep the same temporary-file and atomic-publish guarantees as CopyImageFile.
        private static void ImportResizedImage(string source, string destination,
            string extension, int sourceWidth, int sourceHeight)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                BitmapImage resized = new BitmapImage();
                using (FileStream input = File.OpenRead(source))
                {
                    resized.BeginInit();
                    resized.CacheOption = BitmapCacheOption.OnLoad;
                    resized.StreamSource = input;
                    // Decode at the limiting dimension rather than constructing
                    // a full-resolution bitmap and scaling it in WPF memory.
                    if ((long)MaxWallpaperWidth * sourceHeight <=
                        (long)MaxWallpaperHeight * sourceWidth)
                        resized.DecodePixelWidth = MaxWallpaperWidth;
                    else
                        resized.DecodePixelHeight = MaxWallpaperHeight;
                    resized.EndInit();
                    resized.Freeze();
                }
                if (resized.PixelWidth < 1 || resized.PixelHeight < 1 ||
                    resized.PixelWidth > MaxWallpaperWidth ||
                    resized.PixelHeight > MaxWallpaperHeight)
                    throw new InvalidDataException("Не удалось уменьшить изображение до 3840 × 2160.");

                BitmapEncoder encoder;
                if (extension == ".png")
                    encoder = new PngBitmapEncoder();
                else
                {
                    JpegBitmapEncoder jpeg = new JpegBitmapEncoder();
                    jpeg.QualityLevel = 90;
                    encoder = jpeg;
                }
                encoder.Frames.Add(BitmapFrame.Create(resized));
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    encoder.Save(output);
                    output.Flush(true);
                }
                // Validate the final encoded file before making it visible in
                // the library (including all pixels and the existing 20 MiB cap).
                using (FileStream image = File.OpenRead(temporary))
                    ValidateImage(image, extension, MaxFileBytes);
                File.Move(temporary, destination);
            }
            catch (Exception importError)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Не удалось удалить временный файл фона.",
                        importError, cleanupError);
                }
                throw;
            }
        }

        internal static string Import(string source)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Изображение не найдено.", source);
            FileInfo details = new FileInfo(source);
            string extension = details.Extension.ToLowerInvariant();
            if (extension != ".jpg" && extension != ".jpeg" && extension != ".png")
                throw new InvalidDataException("Поддерживаются только JPG и PNG.");
            if (details.Length == 0 || details.Length > MaxFileBytes)
                throw new InvalidDataException("Размер изображения должен быть не больше 20 МиБ.");

            int sourceWidth;
            int sourceHeight;
            using (FileStream input = File.OpenRead(source))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(input,
                    BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count < 1)
                    throw new InvalidDataException("В файле нет изображения.");
                bool isJpeg = decoder is JpegBitmapDecoder;
                bool isPng = decoder is PngBitmapDecoder;
                if ((extension == ".png" && !isPng) || (extension != ".png" && !isJpeg))
                    throw new InvalidDataException("Содержимое изображения не соответствует расширению.");
                BitmapFrame frame = decoder.Frames[0];
                sourceWidth = frame.PixelWidth;
                sourceHeight = frame.PixelHeight;
                // Even with decoder downsampling, reject pathological sources.
                if (sourceWidth < 1 || sourceHeight < 1 ||
                    sourceWidth > 16384 || sourceHeight > 16384 ||
                    (long)sourceWidth * sourceHeight > 160000000)
                    throw new InvalidDataException("Исходное изображение слишком большое для безопасной обработки.");
            }

            // FileInfo already points to a valid local filename. Preserve its
            // original characters (including spaces, punctuation and Cyrillic).
            // Limit only the stored basename length to keep backup paths bounded.
            string displayName = Path.GetFileNameWithoutExtension(source);
            if (displayName.Length > 90) displayName = displayName.Substring(0, 90);
            displayName = displayName.Trim(' ', '.');
            if (displayName.Length == 0) displayName = "Фон";
            Directory.CreateDirectory(DirectoryPath);
            string fileName = Guid.NewGuid().ToString("N") + "_" + displayName + extension;
            string destination = Path.Combine(DirectoryPath, fileName);
            if (sourceWidth > MaxWallpaperWidth || sourceHeight > MaxWallpaperHeight)
                ImportResizedImage(source, destination, extension, sourceWidth, sourceHeight);
            else
                CopyImageFile(source, destination, true);
            return "custom:" + fileName;
        }

        internal static void Delete(string preset)
        {
            string path = FilePath(preset);
            if (path == null) return;
            if (File.Exists(path)) File.Delete(path);
            FluentUi.ForgetCustomBackground(preset);
        }
    }

    // Only secondary resizable windows use this store. The main window already
    // persists its own size and position in apps.json; no window positions are kept here.
    internal static class WindowSizeStore
    {
        [DataContract]
        private sealed class SavedSize
        {
            [DataMember(Name = "key")] public string Key;
            [DataMember(Name = "width")] public double Width;
            [DataMember(Name = "height")] public double Height;
        }

        private static readonly string FileName =
            Path.Combine(ConfigStore.DirectoryPath, "window-sizes.json");
        private static readonly Dictionary<string, SavedSize> Sizes =
            new Dictionary<string, SavedSize>(StringComparer.Ordinal);
        private static bool _loaded;
        private static bool _maySave = true;

        private static bool ValidSize(double width, double height)
        {
            return !Double.IsNaN(width) && !Double.IsInfinity(width) &&
                !Double.IsNaN(height) && !Double.IsInfinity(height) &&
                width >= 200 && width <= 10000 && height >= 150 && height <= 10000;
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            if (!File.Exists(FileName)) return;
            try
            {
                FileInfo file = new FileInfo(FileName);
                if (file.Length > 65536) throw new InvalidDataException("Файл размеров окон слишком большой.");
                using (FileStream input = File.OpenRead(FileName))
                {
                    DataContractJsonSerializer serializer =
                        new DataContractJsonSerializer(typeof(List<SavedSize>));
                    List<SavedSize> restored = (List<SavedSize>)serializer.ReadObject(input);
                    if (restored == null || restored.Count > 100)
                        throw new InvalidDataException("Повреждён список размеров окон.");
                    foreach (SavedSize size in restored)
                        if (size != null && !String.IsNullOrWhiteSpace(size.Key) &&
                            size.Key.Length <= 150 && ValidSize(size.Width, size.Height))
                            Sizes[size.Key] = size;
                }
            }
            catch (Exception error)
            {
                // Never overwrite damaged user settings automatically.
                _maySave = false;
                ConfigStore.Log(error);
            }
        }

        private static void Save()
        {
            if (!_maySave) return;
            string temporary = FileName + ".tmp";
            try
            {
                Directory.CreateDirectory(ConfigStore.DirectoryPath);
                List<SavedSize> entries = new List<SavedSize>(Sizes.Values);
                using (FileStream output = new FileStream(temporary, FileMode.Create,
                    FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(List<SavedSize>))
                        .WriteObject(output, entries);
                    output.Flush(true);
                }
                if (File.Exists(FileName))
                    File.Replace(temporary, FileName, FileName + ".bak", true);
                else
                    File.Move(temporary, FileName);
            }
            catch (Exception error) { ConfigStore.Log(error); }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) { ConfigStore.Log(error); }
            }
        }

        internal static void Attach(Window window)
        {
            if (window == null || window is MainWindow ||
                (window.ResizeMode != ResizeMode.CanResize &&
                 window.ResizeMode != ResizeMode.CanResizeWithGrip) ||
                window.SizeToContent != SizeToContent.Manual)
                return;
            // Two different reminder dialogs share the title "Напоминания".
            // Their initial (designer) dimensions distinguish their saved sizes.
            // The collection-fields editor title contains a user collection name,
            // which must not create a different setting for every collection.
            if (!ValidSize(window.Width, window.Height)) return;
            string key;
            if (window.GetType() == typeof(Window))
            {
                string title = window.Title ?? "";
                if (title.StartsWith("AppShelf — Поля — ", StringComparison.Ordinal))
                    title = "AppShelf — Поля подборки";
                key = title + "#" + Math.Round(window.Width) + "x" + Math.Round(window.Height);
            }
            else key = window.GetType().FullName;
            if (String.IsNullOrWhiteSpace(key) || key.Length > 150 ||
                !key.StartsWith("AppShelf", StringComparison.Ordinal))
                return;

            Load();
            SavedSize previous;
            if (Sizes.TryGetValue(key, out previous))
            {
                double maximumWidth = Math.Max(window.MinWidth,
                    SystemParameters.WorkArea.Width - 24);
                double maximumHeight = Math.Max(window.MinHeight,
                    SystemParameters.WorkArea.Height - 24);
                window.Width = Math.Max(window.MinWidth,
                    Math.Min(previous.Width, Math.Min(window.MaxWidth, maximumWidth)));
                window.Height = Math.Max(window.MinHeight,
                    Math.Min(previous.Height, Math.Min(window.MaxHeight, maximumHeight)));
            }
            double startingWidth = window.Width;
            double startingHeight = window.Height;
            Rect closingBounds = Rect.Empty;
            window.Closing += delegate
            {
                // RestoreBounds is cleared once WPF destroys the native window.
                // Capture it now, but save only after an uncancelled close.
                closingBounds = window.WindowState == WindowState.Normal
                    ? new Rect(0, 0, window.ActualWidth, window.ActualHeight)
                    : window.RestoreBounds;
            };
            window.Closed += delegate
            {
                if (!_maySave) return;
                Rect bounds = closingBounds;
                if (!ValidSize(bounds.Width, bounds.Height)) return;
                if (Math.Abs(bounds.Width - startingWidth) < 1 &&
                    Math.Abs(bounds.Height - startingHeight) < 1)
                    return;
                SavedSize old;
                if (Sizes.TryGetValue(key, out old) &&
                    Math.Abs(old.Width - bounds.Width) < 1 &&
                    Math.Abs(old.Height - bounds.Height) < 1)
                    return;
                Sizes[key] = new SavedSize
                {
                    Key = key, Width = Math.Round(bounds.Width), Height = Math.Round(bounds.Height)
                };
                Save();
            };
        }
    }


    // Shared modal replacement for ordinary in-app WPF message boxes.
    // Startup and WPF-render-failure paths deliberately keep native MessageBox.Show.
    internal static class AppDialog
    {
        internal static MessageBoxResult Show(Window owner, string message, string title,
            MessageBoxButton buttons, MessageBoxImage image)
        {
            double width = Math.Min(540, Math.Max(320, SystemParameters.WorkArea.Width - 40));
            Window dialog = new Window
            {
                Title = String.IsNullOrWhiteSpace(title) ? "AppShelf" : title,
                Width = width,
                MinWidth = Math.Min(350, width),
                SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height - 60),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = owner != null && owner.IsVisible
                    ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };
            if (owner != null && owner.IsVisible) dialog.Owner = owner;
            FluentUi.PrepareWindow(dialog);

            DockPanel layout = new DockPanel { Margin = new Thickness(22, 20, 22, 20) };
            dialog.Content = layout;

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 19, 0, 0)
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);

            Border card = new Border
            {
                Background = FluentUi.Surface,
                BorderBrush = FluentUi.BorderColor,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(16)
            };
            layout.Children.Add(card);

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(135, Math.Min(350, SystemParameters.WorkArea.Height * 0.46))
            };
            FluentUi.StyleSettingsScroll(scroll);
            card.Child = scroll;

            Grid content = new Grid();
            bool withIcon = image != MessageBoxImage.None;
            if (withIcon)
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (withIcon)
            {
                bool isError = image == MessageBoxImage.Error;
                bool isWarning = image == MessageBoxImage.Warning;
                Brush tone = isError ? FluentUi.Brush("#FF9298")
                    : isWarning ? FluentUi.Amber : FluentUi.Accent;
                string symbol = isError ? "×" : isWarning ? "!" :
                    image == MessageBoxImage.Question ? "?" : "i";
                Border mark = new Border
                {
                    Width = 34, Height = 34,
                    Background = FluentUi.SurfaceRaised,
                    BorderBrush = tone,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(17),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = symbol, FontSize = 21, FontWeight = FontWeights.SemiBold,
                        Foreground = tone,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                content.Children.Add(mark);
            }

            TextBlock text = new TextBlock
            {
                Text = message ?? "",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = FluentUi.Foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = withIcon ? new Thickness(5, 1, 0, 1) : new Thickness(0)
            };
            Grid.SetColumn(text, withIcon ? 1 : 0);
            content.Children.Add(text);
            scroll.Content = content;

            string[] captions;
            MessageBoxResult[] values;
            switch (buttons)
            {
                case MessageBoxButton.OK:
                    captions = new[] { "ОК" };
                    values = new[] { MessageBoxResult.OK };
                    break;
                case MessageBoxButton.OKCancel:
                    captions = new[] { "ОК", "Отмена" };
                    values = new[] { MessageBoxResult.OK, MessageBoxResult.Cancel };
                    break;
                case MessageBoxButton.YesNo:
                    captions = new[] { "Да", "Нет" };
                    values = new[] { MessageBoxResult.Yes, MessageBoxResult.No };
                    break;
                case MessageBoxButton.YesNoCancel:
                    captions = new[] { "Да", "Нет", "Отмена" };
                    values = new[] { MessageBoxResult.Yes, MessageBoxResult.No,
                        MessageBoxResult.Cancel };
                    break;
                default:
                    throw new ArgumentOutOfRangeException("buttons");
            }

            // X and Esc never count as affirmative confirmation.
            MessageBoxResult selected = buttons == MessageBoxButton.OK
                ? MessageBoxResult.OK : buttons == MessageBoxButton.YesNo
                    ? MessageBoxResult.No : MessageBoxResult.Cancel;
            for (int i = 0; i < captions.Length; i++)
            {
                MessageBoxResult answer = values[i];
                bool primary = i == 0;
                Button button = new Button
                {
                    Content = captions[i],
                    MinWidth = 90, MinHeight = 36,
                    Padding = new Thickness(16, 7, 16, 7),
                    Margin = new Thickness(i == 0 ? 0 : 9, 0, 0, 0),
                    Background = primary ? FluentUi.Brush("#29486A") : FluentUi.SurfaceRaised,
                    Foreground = FluentUi.Foreground,
                    BorderBrush = primary ? FluentUi.Accent : FluentUi.BorderColor,
                    BorderThickness = new Thickness(1),
                    FontWeight = FontWeights.SemiBold,
                    Cursor = Cursors.Hand,
                    IsDefault = primary,
                    IsCancel = answer == MessageBoxResult.Cancel ||
                        (buttons == MessageBoxButton.YesNo && answer == MessageBoxResult.No)
                };
                button.Click += delegate { selected = answer; dialog.Close(); };
                actions.Children.Add(button);
            }

            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                selected = buttons == MessageBoxButton.OK ? MessageBoxResult.OK :
                    buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
                e.Handled = true;
                dialog.Close();
            };
            dialog.ShowDialog();
            return selected;
        }
    }

    internal static class FluentUi
    {
        public static readonly Brush Background = Brush("#0E1420");
        public static readonly Brush Surface = Brush("#192334");
        public static readonly Brush SurfaceRaised = Brush("#222E43");
        public static readonly Brush SurfaceHover = Brush("#30415C");
        public static readonly Brush BorderColor = Brush("#34445B");
        public static readonly Brush Foreground = Brush("#F4F7FD");
        public static readonly Brush Secondary = Brush("#AABAD0");
        public static readonly Brush Accent = Brush("#88B8FF");
        public static readonly Brush Green = Brush("#59D9B4");
        public static readonly Brush Amber = Brush("#FFC77B");

        public static Brush Brush(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        // Background presets affect only the main app-list area, not the shared control palette.
        // Vector drawings scale with the window; no raster files, network calls or animations.
        public static string NormalizeMainBackground(string preset)
        {
            if (preset == "classic" || preset == "blue-arcs" ||
                preset == "midnight" || preset == "graphite" ||
                CustomBackgroundStore.IsCustomPreset(preset)) return preset;
            return "blue-arcs";
        }

        public static Brush MainWindowBackground(string preset)
        {
            preset = NormalizeMainBackground(preset);
            if (preset == "classic") return Background;
            if (preset == "blue-arcs") return BlueArcBackground(850, 440);
            if (CustomBackgroundStore.IsCustomPreset(preset)) return UserBackground(preset);

            string baseStart = "#09142C";
            string baseEnd = "#142850";
            string glowColor = "#223C6AB0";
            if (preset == "midnight")
            {
                baseStart = "#090F1E";
                baseEnd = "#14263D";
                glowColor = "#1B3E709A";
            }
            else if (preset == "graphite")
            {
                baseStart = "#131921";
                baseEnd = "#1E2A39";
                glowColor = "#18375977";
            }

            Geometry rectangle = new RectangleGeometry(new Rect(0, 0, 1000, 700));
            LinearGradientBrush baseGradient = new LinearGradientBrush();
            baseGradient.StartPoint = new Point(0, 0);
            baseGradient.EndPoint = new Point(1, 1);
            baseGradient.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString(baseStart), 0));
            baseGradient.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString(baseEnd), 1));

            DrawingGroup artwork = new DrawingGroup();
            artwork.Children.Add(new GeometryDrawing(baseGradient, null, rectangle));
            RadialGradientBrush glow = new RadialGradientBrush();
            glow.Center = new Point(0.9, 0.8);
            glow.GradientOrigin = glow.Center;
            glow.RadiusX = 0.75;
            glow.RadiusY = 0.82;
            glow.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString(glowColor), 0));
            glow.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            artwork.Children.Add(new GeometryDrawing(glow, null, rectangle));

            if (preset == "graphite")
            {
                artwork.Children.Add(new GeometryDrawing(
                    Brush("#203147"), null, Geometry.Parse(
                    "M 0,0 L 285,0 C 231,55 144,93 0,130 Z")));
                artwork.Children.Add(new GeometryDrawing(
                    Brush("#1D344C"), null, Geometry.Parse(
                    "M 1000,520 C 939,560 898,624 866,700 L 1000,700 Z")));
            }

            DrawingBrush background = new DrawingBrush(artwork);
            background.Viewbox = new Rect(0, 0, 1000, 700);
            background.ViewboxUnits = BrushMappingMode.Absolute;
            background.Viewport = new Rect(0, 0, 1, 1);
            background.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
            background.Stretch = Stretch.Fill;
            background.TileMode = TileMode.None;
            background.Freeze();
            return background;
        }

        // Keep only one decoded custom image cached. Cycling through large 4K
        // wallpapers in Settings must not retain every preview in memory.
        private static string _cachedCustomPreset;
        private static Brush _cachedCustomBrush;

        internal static void ForgetCustomBackground(string preset)
        {
            if (_cachedCustomPreset != preset) return;
            _cachedCustomPreset = null;
            _cachedCustomBrush = null;
        }

        private static Brush UserBackground(string preset)
        {
            if (_cachedCustomPreset == preset && _cachedCustomBrush != null)
                return _cachedCustomBrush;
            string path = CustomBackgroundStore.FilePath(preset);
            if (path == null || !File.Exists(path)) return MainWindowBackground("midnight");
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    ImageBrush brush = new ImageBrush(bitmap);
                    brush.Stretch = Stretch.UniformToFill;
                    brush.AlignmentX = AlignmentX.Center;
                    brush.AlignmentY = AlignmentY.Center;
                    brush.Freeze();
                    _cachedCustomPreset = preset;
                    _cachedCustomBrush = brush;
                    return brush;
                }
            }
            catch (Exception ex)
            {
                ConfigStore.Log(ex);
                return MainWindowBackground("midnight");
            }
        }

        // Use unscaled, corner-anchored artwork: previously the same SVG-like paths
        // were stretched to the aspect ratio of the window, turning curves into wedges.
        public static Brush MainWindowBackground(string preset, double width, double height)
        {
            if (NormalizeMainBackground(preset) != "blue-arcs")
                return MainWindowBackground(preset);
            return BlueArcBackground(width, height);
        }

        private static Brush BlueArcBackground(double width, double height)
        {
            if (Double.IsNaN(width) || Double.IsInfinity(width) || width < 1)
                width = 850;
            if (Double.IsNaN(height) || Double.IsInfinity(height) || height < 1)
                height = 440;

            Rect bounds = new Rect(0, 0, width, height);
            Geometry rectangle = new RectangleGeometry(bounds);
            LinearGradientBrush baseGradient = new LinearGradientBrush();
            baseGradient.StartPoint = new Point(0, 0);
            baseGradient.EndPoint = new Point(1, 1);
            baseGradient.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#0C1934"), 0));
            baseGradient.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#13274B"), 1));

            DrawingGroup artwork = new DrawingGroup();
            artwork.Children.Add(new GeometryDrawing(baseGradient, null, rectangle));

            RadialGradientBrush ambient = new RadialGradientBrush();
            ambient.Center = new Point(0.75, 0.78);
            ambient.GradientOrigin = ambient.Center;
            ambient.RadiusX = 0.88;
            ambient.RadiusY = 0.9;
            ambient.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#183B68A0"), 0));
            ambient.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            artwork.Children.Add(new GeometryDrawing(ambient, null, rectangle));

            Geometry topCorner = Geometry.Parse(
                "M 0,0 L 320,0 C 291,55 231,101 170,125 C 103,159 48,176 0,191 Z").Clone();
            double topScale = Math.Min(width * 0.31 / 320, height * 0.39 / 191);
            topCorner.Transform = new ScaleTransform(topScale, topScale);
            // Keep a clean silhouette; soften the colour inside, not the curved edge.
            RadialGradientBrush topPaint = new RadialGradientBrush();
            topPaint.Center = new Point(0, 0);
            topPaint.GradientOrigin = topPaint.Center;
            topPaint.RadiusX = 1.1;
            topPaint.RadiusY = 1.1;
            topPaint.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#142B55"), 0));
            topPaint.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#234A8D"), 0.65));
            topPaint.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#2A5AA6"), 1));
            artwork.Children.Add(new GeometryDrawing(topPaint, null, topCorner));

            Geometry lowerCorner = Geometry.Parse(
                "M 440,0 C 397,49 344,91 292,127 C 229,168 192,216 177,270 L 440,270 Z").Clone();
            double lowerScale = Math.Min(width * 0.38 / 440, height * 0.53 / 270);
            TransformGroup anchor = new TransformGroup();
            anchor.Children.Add(new ScaleTransform(lowerScale, lowerScale));
            anchor.Children.Add(new TranslateTransform(
                width - 440 * lowerScale, height - 270 * lowerScale));
            lowerCorner.Transform = anchor;
            RadialGradientBrush lowerPaint = new RadialGradientBrush();
            lowerPaint.Center = new Point(1, 1);
            lowerPaint.GradientOrigin = lowerPaint.Center;
            lowerPaint.RadiusX = 1;
            lowerPaint.RadiusY = 1;
            lowerPaint.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#122340"), 0));
            lowerPaint.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#172F58"), 0.45));
            lowerPaint.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString("#2859A6"), 1));
            artwork.Children.Add(new GeometryDrawing(lowerPaint, null, lowerCorner));

            DrawingBrush result = new DrawingBrush(artwork);
            result.Viewbox = bounds;
            result.ViewboxUnits = BrushMappingMode.Absolute;
            result.Viewport = new Rect(0, 0, 1, 1);
            result.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
            result.Stretch = Stretch.Fill;
            result.TileMode = TileMode.None;
            result.Freeze();
            return result;
        }

        // Dark, slightly translucent group cards let the selected backdrop show through,
        // while preserving contrast for group headings and app tiles.
        public const int DefaultTileScalePercent = 100;

        public static int NormalizeTileScale(int? percent)
        {
            return !percent.HasValue || percent.Value < 75 || percent.Value > 125
                ? DefaultTileScalePercent : percent.Value;
        }

        public const int DefaultGroupOpacity = 88;

        public static int NormalizeGroupOpacity(int? opacity)
        {
            if (!opacity.HasValue || opacity.Value < 50 || opacity.Value > 100)
                return DefaultGroupOpacity;
            return opacity.Value;
        }

        private static Color GroupColor(string hex, int opacity)
        {
            Color color = (Color)ColorConverter.ConvertFromString(hex);
            // At 88% retain the exact 2.12.0 alpha; at 100% the group is opaque.
            int alpha = opacity <= DefaultGroupOpacity
                ? (int)Math.Round(color.A * opacity / (double)DefaultGroupOpacity)
                : (int)Math.Round(color.A + (255 - color.A) *
                    (opacity - DefaultGroupOpacity) / (double)(100 - DefaultGroupOpacity));
            return Color.FromArgb((byte)Math.Max(0, Math.Min(255, alpha)),
                color.R, color.G, color.B);
        }

        public static Brush MainGroupBackground(string preset)
        {
            return MainGroupBackground(preset, DefaultGroupOpacity);
        }

        public static Brush MainGroupBackground(string preset, int opacity)
        {
            preset = NormalizeMainBackground(preset);
            opacity = NormalizeGroupOpacity(opacity);
            if (preset == "classic")
            {
                if (opacity == DefaultGroupOpacity) return Surface;
                SolidColorBrush surface = Surface as SolidColorBrush;
                if (surface == null) return Surface;
                SolidColorBrush adjusted = new SolidColorBrush(GroupColor(
                    surface.Color.ToString(), opacity));
                adjusted.Freeze();
                return adjusted;
            }
            string from = "#E11B2C49";
            string to = "#D3182943";
            if (preset == "midnight")
            {
                from = "#E2172B45";
                to = "#DB17243B";
            }
            else if (preset == "graphite")
            {
                from = "#E223303E";
                to = "#D91C2938";
            }
            LinearGradientBrush gradient = new LinearGradientBrush();
            gradient.StartPoint = new Point(0, 0);
            gradient.EndPoint = new Point(1, 1);
            gradient.GradientStops.Add(new GradientStop(GroupColor(from, opacity), 0));
            gradient.GradientStops.Add(new GradientStop(GroupColor(to, opacity), 1));
            gradient.Freeze();
            return gradient;
        }

        public static Brush MainGroupBorder(string preset)
        {
            preset = NormalizeMainBackground(preset);
            if (preset == "classic") return BorderColor;
            return Brush(preset == "graphite" ? "#63798B9E" : "#56738FAF");
        }

        private static ImageSource _brandImage;

        // PNG is embedded in both the .NET Framework and .NET 8 build paths.
        // OnLoad makes the frozen image independent of its disposed resource stream.
        public static ImageSource BrandImageSource()
        {
            if (_brandImage != null) return _brandImage;
            using (Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("AppShelf.Brand.png"))
            {
                if (stream == null) return null;
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                _brandImage = image;
                return image;
            }
        }

        public static Brush HeaderGradient()
        {
            LinearGradientBrush gradient = new LinearGradientBrush();
            gradient.StartPoint = new Point(0, 0);
            gradient.EndPoint = new Point(1, 1);
            gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1C2940"), 0));
            gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#151D2C"), 1));
            gradient.Freeze();
            return gradient;
        }

        public static void Install(Application app)
        {
            app.Resources[typeof(Button)] = ButtonStyle();
            app.Resources[typeof(TextBox)] = TextBoxStyle();
            app.Resources[typeof(CheckBox)] = CheckBoxStyle();
            app.Resources[typeof(Expander)] = ExpanderStyle();
            app.Resources.MergedDictionaries.Add(ContextMenuResources());
            app.Resources.MergedDictionaries.Add(SelectionResources());
            app.Resources[typeof(ScrollBar)] = (Style)SettingsScrollResources()[typeof(ScrollBar)];
        }

        private static ResourceDictionary ContextMenuResources()
        {
            // Keep native MenuItem input, commands, check states and popup navigation.
            // Only WPF controls are styled; NotifyIcon uses a separate WinForms menu.
            ResourceDictionary resources = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='ContextMenu'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Background' Value='#192334'/>
  <Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='BorderBrush' Value='#34445B'/>
  <Setter Property='BorderThickness' Value='1'/>
  <Setter Property='FontFamily' Value='Segoe UI'/>
  <Setter Property='FontSize' Value='12.5'/>
  <Setter Property='Padding' Value='5'/>
  <Setter Property='HasDropShadow' Value='False'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContextMenu'>
   <Border x:Name='MenuSurface' Background='{TemplateBinding Background}'
    BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
    CornerRadius='9' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
    <ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto'
     HorizontalScrollBarVisibility='Disabled' Focusable='False'>
     <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle'
      KeyboardNavigation.TabNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
    </ScrollViewer>
   </Border>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='MenuItem'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='FontFamily' Value='Segoe UI'/>
  <Setter Property='FontSize' Value='12.5'/>
  <Setter Property='Padding' Value='9,7'/>
  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
   <Grid SnapsToDevicePixels='True'>
    <Border x:Name='ItemSurface' Background='{TemplateBinding Background}'
     CornerRadius='5' Padding='{TemplateBinding Padding}'>
     <Grid>
      <Grid.ColumnDefinitions>
       <ColumnDefinition Width='22'/>
       <ColumnDefinition Width='*' SharedSizeGroup='MenuHeader'/>
       <ColumnDefinition Width='Auto' SharedSizeGroup='MenuGesture'/>
       <ColumnDefinition Width='17'/>
      </Grid.ColumnDefinitions>
      <ContentPresenter x:Name='Icon' ContentSource='Icon'
       Width='17' Height='17' HorizontalAlignment='Left' VerticalAlignment='Center'/>
      <Path x:Name='Check' Data='M 2,8 L 6,12 L 14,3' Stroke='#88B8FF' StrokeThickness='1.8'
       StrokeStartLineCap='Round' StrokeEndLineCap='Round'
       Width='16' Height='16' VerticalAlignment='Center' HorizontalAlignment='Left' Visibility='Collapsed'/>
      <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True'
       Margin='3,0,12,0' VerticalAlignment='Center'/>
      <TextBlock x:Name='Gesture' Grid.Column='2' Text='{TemplateBinding InputGestureText}' Foreground='#AABAD0'
       Margin='8,0,4,0' VerticalAlignment='Center'/>
      <Path x:Name='Arrow' Grid.Column='3' Data='M 1,1 L 5,5 L 1,9'
       Stroke='#AABAD0' StrokeThickness='1.5' HorizontalAlignment='Right'
       VerticalAlignment='Center' Visibility='Collapsed'/>
     </Grid>
    </Border>
    <Popup x:Name='PART_Popup' IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'
     Placement='Right' HorizontalOffset='-1' VerticalOffset='-5'
     AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
     <Border x:Name='SubmenuSurface' Background='#192334' BorderBrush='#34445B'
      BorderThickness='1' CornerRadius='9' Padding='5'
      MaxHeight='{Binding Source={x:Static SystemParameters.WorkArea}, Path=Height}'>
      <ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto'
       HorizontalScrollBarVisibility='Disabled' Focusable='False'>
       <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle'
        KeyboardNavigation.TabNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
      </ScrollViewer>
     </Border>
    </Popup>
   </Grid>
   <ControlTemplate.Triggers>
    <Trigger Property='HasItems' Value='True'>
     <Setter TargetName='Arrow' Property='Visibility' Value='Visible'/>
    </Trigger>
    <Trigger Property='IsChecked' Value='True'>
     <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
     <Setter TargetName='Icon' Property='Visibility' Value='Collapsed'/>
    </Trigger>
    <Trigger Property='IsHighlighted' Value='True'>
     <Setter TargetName='ItemSurface' Property='Background' Value='#30415C'/>
    </Trigger>
    <Trigger Property='IsSubmenuOpen' Value='True'>
     <Setter TargetName='ItemSurface' Property='Background' Value='#30415C'/>
    </Trigger>
    <Trigger Property='IsEnabled' Value='False'>
     <Setter Property='Foreground' Value='#AABAD0'/>
     <Setter TargetName='ItemSurface' Property='Opacity' Value='0.55'/>
    </Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Separator'>
   <Border Height='1' Background='#34445B' Margin='9,5'/>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='Separator' BasedOn='{StaticResource {x:Static MenuItem.SeparatorStyleKey}}'/>
</ResourceDictionary>");
            ResourceDictionary scrollResources = SettingsScrollResources();
            ((Style)resources[typeof(ContextMenu)]).Resources.MergedDictionaries.Add(scrollResources);
            ((Style)resources[typeof(MenuItem)]).Resources.MergedDictionaries.Add(scrollResources);
            return resources;
        }

        public static void PrepareWindow(Window window)
        {
            window.Background = Background;
            window.Foreground = Foreground;
            window.FontFamily = new FontFamily("Segoe UI");
            window.SourceInitialized += delegate { EnableDarkCaption(window); };
            WindowSizeStore.Attach(window);
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static void EnableDarkCaption(Window window)
        {
            try
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                int enabled = 1;
                if (DwmSetWindowAttribute(handle, 20, ref enabled, 4) != 0)
                    DwmSetWindowAttribute(handle, 19, ref enabled, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (Exception ex) { ConfigStore.Log(ex); }
        }

        private static ControlTemplate ButtonTemplate(bool stretchContent = false)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty,
                new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty,
                new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                stretchContent ? HorizontalAlignment.Stretch : HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(content);
            template.VisualTree = border;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, SurfaceHover, "Chrome"));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.OpacityProperty, 0.77, "Chrome"));
            template.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.45, "Chrome"));
            template.Triggers.Add(disabled);
            return template;
        }

        private static Style ButtonStyle()
        {
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceRaised));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderColor));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13, 8, 13, 8)));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34.0));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, ButtonTemplate()));
            return style;
        }

        public static Style BottomCategoryButtonStyle()
        {
            Style style = new Style(typeof(Button), ButtonStyle());
            style.Setters.Add(new Setter(Control.TemplateProperty, ButtonTemplate(true)));
            Trigger focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Control.BorderBrushProperty, Accent));
            style.Triggers.Add(focused);
            return style;
        }

        // Vector icons keep the overflow menu readable without icon fonts or external assets.
        public static FrameworkElement MenuGlyph(string kind)
        {
            string geometry = kind == "bell" ?
                "M 9,2 C 6,2 5,4 5,7 L 5,11 L 3,13 L 15,13 L 13,11 L 13,7 C 13,4 12,2 9,2 Z M 7,15 L 11,15" :
                kind == "folder" ?
                "M 2,5 L 7,5 L 9,7 L 17,7 L 17,16 L 2,16 Z M 12,10 L 12,14 M 10,12 L 14,12" :
                kind == "sliders" ?
                "M 3,4 L 15,4 M 3,9 L 15,9 M 3,14 L 15,14 M 7,2 L 7,6 M 12,7 L 12,11 M 6,12 L 6,16" :
                kind == "tools" ?
                "M 2,16 L 12,6 M 10,3 L 15,8 M 12,2 L 16,6 M 3,12 L 7,16" :
                kind == "puzzle" ?
                "M 2,5 L 6,5 C 5,3 6,1 8,1 C 10,1 11,3 10,5 L 15,5 L 15,9 C 17,8 18,9 18,11 C 18,13 17,14 15,13 L 15,17 L 10,17 C 11,15 10,13 8,13 C 6,13 5,15 6,17 L 2,17 Z" :
                kind == "settings" ?
                "M 9,2 L 11,4 L 14,4 L 14,7 L 16,9 L 14,11 L 14,14 L 11,14 L 9,16 L 7,14 L 4,14 L 4,11 L 2,9 L 4,7 L 4,4 L 7,4 Z M 12,9 A 3,3 0 1 1 6,9 A 3,3 0 1 1 12,9" :
                "M 9,2 A 7,7 0 1 1 9,16 A 7,7 0 1 1 9,2 M 9,8 L 9,13 M 9,5 L 9,6";
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(geometry), Width = 17, Height = 17,
                Stretch = Stretch.Uniform, Stroke = Secondary, StrokeThickness = 1.55,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }

        public static FrameworkElement BottomCategoryIcon(string category)
        {
            // Vector geometry avoids missing glyphs on older Windows font versions.
            string geometry = category == "network"
                ? "M 9,6 L 9,10 M 3,12 L 3,10 L 15,10 L 15,12 M 7,1 L 11,1 L 11,5 L 7,5 Z M 1,13 L 5,13 L 5,17 L 1,17 Z M 13,13 L 17,13 L 17,17 L 13,17 Z"
                : category == "weather"
                ? "M 13,9 A 4,4 0 1 1 5,9 A 4,4 0 1 1 13,9 M 9,0 L 9,3 M 9,15 L 9,18 M 0,9 L 3,9 M 15,9 L 18,9 M 2.5,2.5 L 4.5,4.5 M 13.5,13.5 L 15.5,15.5 M 2.5,15.5 L 4.5,13.5 M 13.5,4.5 L 15.5,2.5"
                : "M 17,9 A 8,8 0 1 1 1,9 A 8,8 0 1 1 17,9 M 9,4 L 9,9 L 13,9";
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(geometry), Width = 18, Height = 18,
                Stretch = Stretch.Uniform, StrokeThickness = 1.3,
                Stroke = category == "network" ? Green : Accent,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        private static ControlTemplate TextBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "InputChrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(Border.BorderThicknessProperty,
                new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty,
                new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            FrameworkElementFactory host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            // TextBoxView already applies TextBox.Padding. A second margin clips the glyphs.
            host.SetBinding(Control.VerticalContentAlignmentProperty,
                new Binding("VerticalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(host);
            template.VisualTree = border;
            Trigger focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "InputChrome"));
            template.Triggers.Add(focused);
            return template;
        }

        private static Style TextBoxStyle()
        {
            Style style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderColor));
            style.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Accent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
            style.Setters.Add(new Setter(Control.TemplateProperty, TextBoxTemplate()));
            return style;
        }

        private static Style CheckBoxStyle(bool readableDisabled = false)
        {
            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory line = new FrameworkElementFactory(typeof(StackPanel));
            line.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "CheckFrame";
            frame.SetValue(FrameworkElement.WidthProperty, 18.0);
            frame.SetValue(FrameworkElement.HeightProperty, 18.0);
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            frame.SetValue(Border.BackgroundProperty, Surface);
            frame.SetValue(Border.BorderBrushProperty, BorderColor);
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            FrameworkElementFactory tick = new FrameworkElementFactory(typeof(TextBlock));
            tick.Name = "CheckTick";
            tick.SetValue(TextBlock.TextProperty, "✓");
            tick.SetValue(TextBlock.FontSizeProperty, 13.0);
            tick.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            tick.SetValue(TextBlock.ForegroundProperty, Accent);
            tick.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            tick.SetValue(FrameworkElement.VisibilityProperty, Visibility.Collapsed);
            frame.AppendChild(tick);
            line.AppendChild(frame);
            FrameworkElementFactory label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            label.SetBinding(ContentPresenter.ContentProperty,
                new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            line.AppendChild(label);
            template.VisualTree = line;
            Trigger checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckTick"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "CheckFrame"));
            template.Triggers.Add(checkedTrigger);
            Trigger disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(readableDisabled
                ? new Setter(Control.ForegroundProperty, Secondary)
                : new Setter(UIElement.OpacityProperty, 0.5));
            template.Triggers.Add(disabledTrigger);
            Style style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        // These resources are opt-in: do not replace controls in other windows.
        // Dedicated slider used only by the appearance settings. The colored part of
        // the native WPF Track reflects the value; keyboard/mouse/drag work normally.
        public static void StyleGroupOpacitySlider(Slider slider)
        {
            ResourceDictionary resources = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 xmlns:p='clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework'>
 <Style x:Key='OpacitySlider' TargetType='Slider'>
  <Setter Property='Focusable' Value='True'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='Slider'>
    <Grid Height='22' Background='Transparent'>
     <p:Track x:Name='PART_Track' IsDirectionReversed='False'
       VerticalAlignment='Center' Height='22'
       Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
       Value='{TemplateBinding Value}'>
      <p:Track.DecreaseRepeatButton>
       <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
        <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>
         <Border Background='#67B8FF' Height='4' CornerRadius='2' VerticalAlignment='Center'/>
        </ControlTemplate></RepeatButton.Template>
       </RepeatButton>
      </p:Track.DecreaseRepeatButton>
      <p:Track.Thumb>
       <Thumb Width='16' Height='16'>
        <Thumb.Template><ControlTemplate TargetType='Thumb'>
         <Ellipse x:Name='Dot' Fill='#FAFCFF' Stroke='#6BB7FF' StrokeThickness='2'/>
         <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
           <Setter TargetName='Dot' Property='Stroke' Value='#B5D9FF'/>
          </Trigger>
          <Trigger Property='IsDragging' Value='True'>
           <Setter TargetName='Dot' Property='Fill' Value='#D8EBFF'/>
          </Trigger>
         </ControlTemplate.Triggers>
        </ControlTemplate></Thumb.Template>
       </Thumb>
      </p:Track.Thumb>
      <p:Track.IncreaseRepeatButton>
       <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
        <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>
         <Border Background='#34465F' Height='4' CornerRadius='2' VerticalAlignment='Center'/>
        </ControlTemplate></RepeatButton.Template>
       </RepeatButton>
      </p:Track.IncreaseRepeatButton>
     </p:Track>
    </Grid>
    <ControlTemplate.Triggers>
     <Trigger Property='IsEnabled' Value='False'>
      <Setter Property='Opacity' Value='0.5'/>
     </Trigger>
    </ControlTemplate.Triggers>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
</ResourceDictionary>");
            slider.Style = (Style)resources["OpacitySlider"];
        }

        public static void StyleSettingsScroll(ScrollViewer viewer)
        {
            viewer.Resources.MergedDictionaries.Add(SettingsScrollResources());
        }

        public static void StyleDiagnosticReportScroll(TextBox report)
        {
            // The diagnostic report needs both vertical and horizontal scrolling.
            report.Resources.MergedDictionaries.Add(SettingsScrollResources());
            // The native ScrollViewer uses this system brush for the two-bar corner.
            report.Resources[SystemColors.ControlBrushKey] = Brush("#151D2C");
        }

        private static ResourceDictionary SettingsScrollResources()
        {
            // Scoped to settings/menu resources. Built-in ScrollViewer keeps wheel, keys,
            // touchpad gestures and keyboard focus handling.
            ResourceDictionary resources = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 xmlns:sys='clr-namespace:System;assembly=mscorlib'
 xmlns:p='clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework'>
 <!-- The system ScrollViewer template sets its own dynamic Width on the bar. -->
 <sys:Double x:Key='{x:Static SystemParameters.VerticalScrollBarWidthKey}'>12</sys:Double>
 <sys:Double x:Key='{x:Static SystemParameters.HorizontalScrollBarHeightKey}'>12</sys:Double>
 <Style TargetType='ScrollBar'>
  <Setter Property='Width' Value='12'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
   <Border Background='#151D2C' CornerRadius='6'>
    <p:Track x:Name='PART_Track' IsDirectionReversed='True'
      Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
      Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
     <p:Track.DecreaseRepeatButton>
      <RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' Opacity='0'/>
     </p:Track.DecreaseRepeatButton>
     <p:Track.Thumb><Thumb>
      <Thumb.Template><ControlTemplate TargetType='Thumb'>
       <Border x:Name='Grip' Background='#607590' CornerRadius='5' Margin='2,2'/>
       <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'>
         <Setter TargetName='Grip' Property='Background' Value='#88B8FF'/>
        </Trigger>
        <Trigger Property='IsDragging' Value='True'>
         <Setter TargetName='Grip' Property='Background' Value='#B7D3FF'/>
        </Trigger>
       </ControlTemplate.Triggers>
      </ControlTemplate></Thumb.Template>
     </Thumb></p:Track.Thumb>
     <p:Track.IncreaseRepeatButton>
      <RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' Opacity='0'/>
     </p:Track.IncreaseRepeatButton>
    </p:Track>
   </Border>
  </ControlTemplate></Setter.Value></Setter>
  <Style.Triggers>
   <Trigger Property='Orientation' Value='Horizontal'>
    <Setter Property='Width' Value='Auto'/>
    <Setter Property='Height' Value='12'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
     <Border Background='#151D2C' CornerRadius='6'>
      <p:Track x:Name='PART_Track' Orientation='Horizontal'
       Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
       Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
       <p:Track.DecreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False' Opacity='0'/>
       </p:Track.DecreaseRepeatButton>
       <p:Track.Thumb><Thumb>
        <Thumb.Template><ControlTemplate TargetType='Thumb'>
         <Border x:Name='Grip' Background='#607590' CornerRadius='5' Margin='2,2'/>
         <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
           <Setter TargetName='Grip' Property='Background' Value='#88B8FF'/>
          </Trigger>
          <Trigger Property='IsDragging' Value='True'>
           <Setter TargetName='Grip' Property='Background' Value='#B7D3FF'/>
          </Trigger>
         </ControlTemplate.Triggers>
        </ControlTemplate></Thumb.Template>
       </Thumb></p:Track.Thumb>
       <p:Track.IncreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False' Opacity='0'/>
       </p:Track.IncreaseRepeatButton>
      </p:Track>
     </Border>
    </ControlTemplate></Setter.Value></Setter>
   </Trigger>
  </Style.Triggers>
 </Style>
</ResourceDictionary>");
            return resources;
        }

        private static ResourceDictionary SelectionResources()
        {
            ResourceDictionary resources = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 xmlns:p='clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework'>
 <Style TargetType='ComboBoxItem'>
  <Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='Background' Value='#192334'/>
  <Setter Property='Padding' Value='10,7'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>
    <ContentPresenter VerticalAlignment='Center'/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Chrome' Property='Opacity' Value='0.55'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ComboBox'>
  <Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='BorderBrush' Value='#34445B'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'>
   <Grid>
    <ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
     <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>
      <Border x:Name='Chrome' Background='#192334' BorderBrush='{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=ComboBox}}' BorderThickness='1' CornerRadius='8'>
       <TextBlock Text='&#x25BE;' Foreground='#AABAD0' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0'/>
      </Border><ControlTemplate.Triggers>
       <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
      </ControlTemplate.Triggers>
     </ControlTemplate></ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
     Margin='10,4,30,4' VerticalAlignment='Center' IsHitTestVisible='False'/>
    <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'>
     <Border Background='#192334' BorderBrush='#34445B' BorderThickness='1' CornerRadius='6'
      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
      <ScrollViewer MaxHeight='{TemplateBinding MaxDropDownHeight}' CanContentScroll='True'
       HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>
     </Border>
    </Popup>
   </Grid><ControlTemplate.Triggers>
    <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.55'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ListBoxItem'>
  <Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='Background' Value='#192334'/>
  <Setter Property='Padding' Value='7,4'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='4' Padding='{TemplateBinding Padding}'>
    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#222E43'/></Trigger>
    <Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#AABAD0'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ListBox'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='BorderBrush' Value='#34445B'/><Setter Property='BorderThickness' Value='1'/>
  <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBox'>
   <Border x:Name='ListChrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' Padding='3'>
    <ScrollViewer Focusable='False' CanContentScroll='True' HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>
   </Border>
   <ControlTemplate.Triggers>
    <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='ListChrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='ListChrome' Property='Opacity' Value='0.55'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>");
            return resources;
        }

        public static void StyleBottomPanelInputs(ComboBox mode, ListBox favorites)
        {
            ResourceDictionary resources = SelectionResources();
            mode.Resources.MergedDictionaries.Add(resources);
            mode.Style = (Style)resources[typeof(ComboBox)];
            favorites.Resources.MergedDictionaries.Add(resources);
            favorites.Style = (Style)resources[typeof(ListBox)];
            // Keep the existing check mark, but replace its opacity-based disabled state.
            favorites.Resources[typeof(CheckBox)] = CheckBoxStyle(true);
        }

        private static ResourceDictionary _reminderInputs;

        public static void StyleReminderInputs(Window window)
        {
            if (_reminderInputs == null)
            {
                // Scoped to reminder windows: keep the standard controls and their PART contracts.
                _reminderInputs = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 xmlns:p='clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework'>
 <Style x:Key='CalendarHeader' TargetType='Button'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='BorderBrush' Value='#34445B'/><Setter Property='Padding' Value='5,3'/>
  <Setter Property='MinHeight' Value='28'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='4' Padding='{TemplateBinding Padding}'>
    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
   </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key='CalendarDay' TargetType='p:CalendarDayButton'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='MinWidth' Value='32'/><Setter Property='MinHeight' Value='28'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='p:CalendarDayButton'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='4'>
    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property='IsInactive' Value='True'><Setter Property='Foreground' Value='#AABAD0'/></Trigger>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsToday' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#88B8FF'/><Setter Property='Foreground' Value='#0E1420'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#F4F7FD'/></Trigger>
    <Trigger Property='IsBlackedOut' Value='True'><Setter Property='Opacity' Value='0.3'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.3'/></Trigger>
   </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key='CalendarMonth' TargetType='p:CalendarButton'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='MinWidth' Value='56'/><Setter Property='MinHeight' Value='38'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='p:CalendarButton'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='4'>
    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
   </Border><ControlTemplate.Triggers>
    <Trigger Property='IsInactive' Value='True'><Setter Property='Foreground' Value='#AABAD0'/></Trigger>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='HasSelectedDays' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#F4F7FD'/></Trigger>
   </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key='CalendarItem' TargetType='p:CalendarItem'>
  <Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='p:CalendarItem'>
   <ControlTemplate.Resources>
    <DataTemplate x:Key='{x:Static p:CalendarItem.DayTitleTemplateResourceKey}'><TextBlock Text='{Binding}' Foreground='#AABAD0' HorizontalAlignment='Center' VerticalAlignment='Center'/></DataTemplate>
   </ControlTemplate.Resources>
   <Grid x:Name='PART_Root'>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/></Grid.RowDefinitions>
    <DockPanel Margin='0,0,0,5'>
     <Button x:Name='PART_PreviousButton' DockPanel.Dock='Left' Content='&#x2039;' Width='32' Style='{StaticResource CalendarHeader}'/>
     <Button x:Name='PART_NextButton' DockPanel.Dock='Right' Content='&#x203A;' Width='32' Style='{StaticResource CalendarHeader}'/>
     <Button x:Name='PART_HeaderButton' Style='{StaticResource CalendarHeader}'/>
    </DockPanel>
    <Grid x:Name='PART_MonthView' Grid.Row='1'>
     <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
     <Grid.RowDefinitions><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
    </Grid>
    <Grid x:Name='PART_YearView' Grid.Row='1' Visibility='Collapsed'>
     <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
     <Grid.RowDefinitions><RowDefinition/><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
    </Grid>
   </Grid><ControlTemplate.Triggers>
    <DataTrigger Binding='{Binding DisplayMode, RelativeSource={RelativeSource AncestorType={x:Type Calendar}}}' Value='Year'><Setter TargetName='PART_MonthView' Property='Visibility' Value='Collapsed'/><Setter TargetName='PART_YearView' Property='Visibility' Value='Visible'/></DataTrigger>
    <DataTrigger Binding='{Binding DisplayMode, RelativeSource={RelativeSource AncestorType={x:Type Calendar}}}' Value='Decade'><Setter TargetName='PART_MonthView' Property='Visibility' Value='Collapsed'/><Setter TargetName='PART_YearView' Property='Visibility' Value='Visible'/></DataTrigger>
   </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key='ReminderCalendar' TargetType='Calendar'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/>
  <Setter Property='BorderBrush' Value='#34445B'/><Setter Property='CalendarDayButtonStyle' Value='{StaticResource CalendarDay}'/>
  <Setter Property='CalendarButtonStyle' Value='{StaticResource CalendarMonth}'/><Setter Property='CalendarItemStyle' Value='{StaticResource CalendarItem}'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Calendar'>
   <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='8'>
    <p:CalendarItem x:Name='PART_CalendarItem' Style='{TemplateBinding CalendarItemStyle}' Width='258' Height='230'/>
   </Border></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='DatePicker'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='BorderBrush' Value='#34445B'/>
  <Setter Property='VerticalContentAlignment' Value='Center'/><Setter Property='CalendarStyle' Value='{StaticResource ReminderCalendar}'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='DatePicker'>
   <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8'>
    <DockPanel>
     <Button x:Name='PART_Button' Content='&#x25A6;' AutomationProperties.Name='Открыть календарь' ToolTip='Открыть календарь (Alt+↓)' DockPanel.Dock='Right' Width='30' Margin='0,2,2,2' MinHeight='0' Padding='0' Focusable='False'/>
     <Popup x:Name='PART_Popup' Placement='Bottom' PlacementTarget='{Binding RelativeSource={RelativeSource TemplatedParent}}' StaysOpen='False' AllowsTransparency='True'/>
     <p:DatePickerTextBox x:Name='PART_TextBox' Background='Transparent' Foreground='{TemplateBinding Foreground}' BorderThickness='0' Padding='8,4' VerticalContentAlignment='Center'/>
    </DockPanel>
   </Border></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ComboBoxItem'>
  <Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='Background' Value='#192334'/><Setter Property='Padding' Value='10,6'/><Setter Property='MinHeight' Value='30'/>
  <Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'>
   <Border x:Name='Chrome' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter VerticalAlignment='Center'/></Border>
   <ControlTemplate.Triggers>
    <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
    <Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#30415C'/></Trigger>
   </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ComboBox'>
  <Setter Property='Background' Value='#192334'/><Setter Property='Foreground' Value='#F4F7FD'/><Setter Property='BorderBrush' Value='#34445B'/>
  <Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'>
   <Grid>
    <ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
     <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>
      <Border x:Name='Chrome' Background='#192334' BorderBrush='#34445B' BorderThickness='1' CornerRadius='8'><TextBlock Text='&#x25BE;' Foreground='#AABAD0' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0'/></Border>
      <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88B8FF'/></Trigger></ControlTemplate.Triggers>
     </ControlTemplate></ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' Margin='10,4,30,4' VerticalAlignment='Center' IsHitTestVisible='False'/>
    <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'>
     <Border Background='#192334' BorderBrush='#34445B' BorderThickness='1' CornerRadius='6' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
      <ScrollViewer MaxHeight='{TemplateBinding MaxDropDownHeight}' CanContentScroll='True' VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>
     </Border>
    </Popup>
   </Grid></ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>");
            }
            window.Resources.MergedDictionaries.Add(_reminderInputs);
            Style dateText = new Style(typeof(DatePickerTextBox), TextBoxStyle());
            dateText.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            window.Resources[typeof(DatePickerTextBox)] = dateText;
        }

        private static Style ExpanderStyle()
        {
            Style style = new Style(typeof(Expander));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            return style;
        }

        public static Border Chip(TextBlock text, Brush dotColor, bool interactive)
        {
            StackPanel stack = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            Border dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = dotColor, Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(dot);
            text.Margin = new Thickness(0);
            text.FontSize = 11.5;
            stack.Children.Add(text);
            Border chip = new Border { Child = stack,
                Background = SurfaceRaised, BorderBrush = BorderColor,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 8, 11, 8), Margin = new Thickness(0, 0, 8, 8),
                Cursor = interactive ? Cursors.Hand : Cursors.Arrow };
            return chip;
        }
    }
}
