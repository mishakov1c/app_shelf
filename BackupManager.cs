// Portable AppShelf backup. Includes configuration, reminders and private shortcuts.
// No secrets, logs or absolute paths to AppShelf-owned shortcuts in the archive.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppShelf
{
    public sealed class BackupRestoreException : IOException
    {
        public IList<Exception> RollbackErrors { get; private set; }
        public string SafetyCopyDirectory { get; private set; }

        internal BackupRestoreException(Exception importError, List<Exception> rollbackErrors, string safetyCopy)
            : base(RestoreErrorText(importError, rollbackErrors, safetyCopy), importError)
        {
            RollbackErrors = rollbackErrors.AsReadOnly();
            SafetyCopyDirectory = safetyCopy;
        }

        private static string RestoreErrorText(Exception error, List<Exception> rollbackErrors, string safetyCopy)
        {
            string text = "Ошибка импорта: " + error.Message + "\n" +
                (rollbackErrors.Count == 0 ? "Откат выполнен." : "Откат выполнен не полностью. Сохранение отключено до восстановления данных.");
            foreach (Exception rollbackError in rollbackErrors) text += "\nОшибка отката: " + rollbackError.Message;
            return text + "\nСтраховочная копия сохранена: " + safetyCopy;
        }
    }

    public static class BackupStore
    {
        private const int MaxEntries = 2000;
        private const long MaxEntryBytes = 10L * 1024 * 1024;
        private const long MaxTotalBytes = 40L * 1024 * 1024;
        private const long MaxCoverTotalBytes = 256L * 1024 * 1024;
        private const long MaxBackgroundTotalBytes = 256L * 1024 * 1024;

        private const string ExecutableEntry = "Program/AppShelf.exe";
        private const long MaxExecutableBytes = 256L * 1024 * 1024;

        internal static string CurrentExecutable()
        {
            // Do not archive the PowerShell process hosting isolated test assemblies.
            if (System.Reflection.Assembly.GetEntryAssembly() != typeof(MainWindow).Assembly) return null;
            using (Process process = Process.GetCurrentProcess()) return process.MainModule.FileName;
        }

        private static string HashFile(string path)
        {
            using (FileStream input = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(input));
        }

        private static void CopyBounded(Stream input, Stream target, long expected, long limit)
        {
            if (expected <= 0 || expected > limit) throw new InvalidDataException("Недопустимый размер файла программы.");
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (read > expected - total) throw new InvalidDataException("Файл превышает заявленный размер.");
                target.Write(buffer, 0, read); total += read;
            }
            if (total != expected) throw new InvalidDataException("Неполный файл программы.");
        }

        private static void AddBinary(ZipArchive archive, string name, string path, long limit)
        {
            using (FileStream input = File.OpenRead(path))
            using (Stream target = archive.CreateEntry(name, CompressionLevel.Optimal).Open())
                CopyBounded(input, target, input.Length, limit);
        }

        private static void ReadBinary(ZipArchiveEntry entry, string name, string extraction, bool executable, bool packages)
        {
            bool core = name == ExecutableEntry;
            string scratch = Path.Combine(Path.GetTempPath(), "AppShelfBinary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                string file = Path.Combine(scratch, Path.GetFileName(name));
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(file, FileMode.CreateNew))
                    CopyBounded(input, output, entry.Length, core ? MaxExecutableBytes : PluginPackageStore.MaxArchiveBytes);
                if (core)
                {
                    using (FileStream input = File.OpenRead(file))
                        if (input.ReadByte() != 77 || input.ReadByte() != 90)
                            throw new InvalidDataException("В архиве нет заголовка EXE.");
                }
                else PluginPackageStore.ValidateArchivedPackage(file, Path.GetFileNameWithoutExtension(name));
                if (extraction != null && (core ? executable : packages))
                {
                    string folder = Path.Combine(extraction, core ? "Program" : "Plugins");
                    Directory.CreateDirectory(folder);
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(name)), false);
                }
            }
            finally { Directory.Delete(scratch, true); }
        }

        public static string ExtractPrograms(string filename, string destination, bool executable, bool packages)
        {
            if (!executable && !packages) throw new InvalidOperationException("Выберите компоненты.");
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
            string result = Path.Combine(destination, "AppShelf-programs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(result);
            try
            {
                ReminderDocument reminders;
                Dictionary<string, string> shortcuts, images;
                CollectionDocument collections;
                ReadBackup(filename, null, out reminders, out shortcuts, out collections, out images, result, executable, packages);
                if (Directory.GetFiles(result, "*", SearchOption.AllDirectories).Length == 0)
                    throw new InvalidDataException("Выбранных компонентов нет в архиве.");
                return result;
            }
            catch { Directory.Delete(result, true); throw; }
        }

        private static void CommitExport(string temp, string filename)
        {
            if (File.Exists(filename)) File.Replace(temp, filename, null);
            else File.Move(temp, filename);
        }

        private static void DeleteExportTemp(string temp)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { ConfigStore.Log(ex); }
        }

        private static AppConfig ReadAppConfig(Stream stream)
        {
            AppConfig config = (AppConfig)new DataContractJsonSerializer(typeof(AppConfig)).ReadObject(stream);
            if (config == null || config.Items == null || config.Groups == null)
                throw new InvalidDataException("Файл apps.json повреждён.");
            if (config.Items.Count > 10000 || config.Groups.Count > 1000)
                throw new InvalidDataException("Слишком большой список программ или групп.");
            return config;
        }

        private static ReminderDocument ReadReminders(Stream stream)
        {
            ReminderDocument reminders = (ReminderDocument)new DataContractJsonSerializer(
                typeof(ReminderDocument)).ReadObject(stream);
            if (reminders == null || reminders.Reminders == null || reminders.Reminders.Count > 10000)
                throw new InvalidDataException("Некорректный файл reminders.json.");
            foreach (ReminderItem item in reminders.Reminders)
            {
                if (!ReminderStore.IsValid(item))
                    throw new InvalidDataException("Некорректная запись напоминания.");
            }
            return reminders;
        }

        private static void StoreDocument<T>(ZipArchive archive, string name, T document)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream target = entry.Open())
                new DataContractJsonSerializer(typeof(T)).WriteObject(target, document);
        }

        public static void ExportAll(string filename, AppConfig config, ReminderDocument reminders)
        {
            ExportAll(filename, config, reminders, CurrentExecutable());
        }

        internal static void ExportAll(string filename, AppConfig config, ReminderDocument reminders, string executablePath)
        {
            if (config == null || reminders == null) throw new InvalidOperationException("Данные приложения не загружены.");
            string collectionWarning;
            bool collectionsCanSave;
            CollectionDocument collections = CollectionStore.Load(out collectionWarning, out collectionsCanSave);
            if (!collectionsCanSave)
                throw new InvalidDataException("Нельзя создавать неполную резервную копию: " + collectionWarning);
            CollectionStore.Validate(collections);
            HashSet<string> images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CollectionDefinition group in collections.Collections)
                foreach (CollectionEntry entry in group.Entries)
                    if (!String.IsNullOrEmpty(entry.CoverFile)) images.Add(entry.CoverFile);
            // Include the full local wallpaper library, not only the selected image.
            // User images stay outside the executable, but belong in portable backups.
            List<string> backgrounds = CustomBackgroundStore.ListPresets();
            // Copy through serializer: never mutate the live config while making portable paths.
            AppConfig portable;
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(stream, config);
                stream.Position = 0;
                portable = ReadAppConfig(stream);
            }
            // Backup root is machine-specific and can contain a personal cloud path.
            // Keep it local rather than exporting it to a portable archive.
            portable.BackupDirectory = null;
            Dictionary<string, string> owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppItem item in portable.Items)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.Path))
                    throw new InvalidDataException("Недопустимая запись приложения для экспорта.");
                if (ConfigStore.IsOwnedShortcut(item.Path))
                {
                    if (!File.Exists(item.Path))
                        throw new FileNotFoundException("Не найден собственный ярлык, резервная копия неполна.", item.Path);
                    string name = Path.GetFileName(item.Path);
                    owned[name] = item.Path;
                    item.Path = "Shortcuts/" + name;
                }
                // Don't carry the old user's desktop path as personal metadata.
                item.SourcePath = null;
            }
            PluginPackageStore pluginStore = new PluginPackageStore(ConfigStore.DirectoryPath);
            byte[] pluginBackup = pluginStore.CaptureBackup();
            string binaryStage = Path.Combine(Path.GetTempPath(), "AppShelfPrograms-" + Guid.NewGuid().ToString("N"));
            bool includeCore = config.BackupExecutable != false && !String.IsNullOrEmpty(executablePath);
            bool includePackages = config.BackupPluginPackages != false;
            // Write to a temporary file; a failed export does not destroy the old backup.
            string temp = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string beforeCore = includeCore ? HashFile(executablePath) : "";
                string beforePackages = includePackages ? pluginStore.PackageSignature() : "";
                List<string> packages = new List<string>();
                if (includePackages)
                {
                    Directory.CreateDirectory(binaryStage);
                    packages = pluginStore.CapturePackages(binaryStage);
                }
                if (owned.Count + images.Count + backgrounds.Count + 4 + (pluginBackup == null ? 0 : 1) +
                    (includeCore ? 1 : 0) + packages.Count > MaxEntries)
                    throw new InvalidDataException("Слишком много файлов для резервной копии.");
                using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                    {
                        StoreDocument(archive, "apps.json", portable);
                        StoreDocument(archive, "reminders.json", reminders);
                        StoreDocument(archive, "Collections/collections.json", collections);
                        if (pluginBackup != null)
                        {
                            ZipArchiveEntry plugins = archive.CreateEntry(PluginPackageStore.BackupArchiveEntry, CompressionLevel.Optimal);
                            using (Stream target = plugins.Open()) target.Write(pluginBackup, 0, pluginBackup.Length);
                        }
                        if (includeCore) AddBinary(archive, ExecutableEntry, executablePath, MaxExecutableBytes);
                        foreach (string package in packages)
                            AddBinary(archive, PluginPackageStore.PackageArchivePrefix + Path.GetFileName(package), package, PluginPackageStore.MaxArchiveBytes);
                        foreach (string image in images)
                        {
                            string source = CollectionStore.CoverPath(image);
                            if (!File.Exists(source))
                                throw new FileNotFoundException("Не найдена обложка, архив был бы неполным.", source);
                            ZipArchiveEntry cover = archive.CreateEntry(
                                "Collections/Images/" + image, CompressionLevel.Optimal);
                            using (FileStream input = File.OpenRead(source))
                            {
                                if (input.Length == 0 || input.Length > MaxEntryBytes)
                                    throw new InvalidDataException("Недопустимый размер обложки.");
                                using (Stream target = cover.Open()) input.CopyTo(target);
                            }
                        }
                        foreach (string preset in backgrounds)
                        {
                            string source = CustomBackgroundStore.FilePath(preset);
                            if (source == null || !File.Exists(source))
                                throw new FileNotFoundException("Не найден пользовательский фон.", source);
                            ZipArchiveEntry wallpaper = archive.CreateEntry(
                                "Backgrounds/" + Path.GetFileName(source), CompressionLevel.Optimal);
                            using (FileStream input = File.OpenRead(source))
                            {
                                if (input.Length == 0 || input.Length > CustomBackgroundStore.MaxFileBytes)
                                    throw new InvalidDataException("Недопустимый размер пользовательского фона.");
                                using (Stream target = wallpaper.Open()) input.CopyTo(target);
                            }
                        }
                        foreach (KeyValuePair<string, string> file in owned)
                        {
                            ZipArchiveEntry entry = archive.CreateEntry("Shortcuts/" + file.Key, CompressionLevel.Optimal);
                            using (FileStream input = File.OpenRead(file.Value))
                            {
                                if (input.Length > MaxEntryBytes) throw new InvalidDataException("Слишком большой ярлык: " + file.Key);
                                using (Stream target = entry.Open()) input.CopyTo(target);
                            }
                        }
                        ZipArchiveEntry meta = archive.CreateEntry("backup-info.txt", CompressionLevel.Optimal);
                        using (StreamWriter writer = new StreamWriter(meta.Open()))
                        {
                            writer.WriteLine("AppShelf portable backup 2.5");
                            writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            writer.WriteLine("Includes settings, reminders, private shortcuts, collections, custom backgrounds and optional plugin data.");
                        }
                    }
                    output.Flush(true);
                }
                // Use the same complete validation as import before replacing a previous backup.
                ReminderDocument checkedReminders;
                Dictionary<string, string> checkedShortcuts;
                CollectionDocument checkedCollections;
                Dictionary<string, string> checkedImages;
                ReadBackup(temp, null, out checkedReminders, out checkedShortcuts,
                    out checkedCollections, out checkedImages);
                byte[] afterPlugins = pluginStore.CaptureBackup();
                if ((pluginBackup == null) != (afterPlugins == null) ||
                    (pluginBackup != null && !System.Linq.Enumerable.SequenceEqual(pluginBackup, afterPlugins)))
                    throw new IOException("Данные плагинов изменились во время резервного копирования.");
                if ((includeCore && beforeCore != HashFile(executablePath)) ||
                    (includePackages && beforePackages != pluginStore.PackageSignature()))
                    throw new IOException("Программы изменились во время резервного копирования.");
                CommitExport(temp, filename);
            }
            finally
            {
                DeleteExportTemp(temp);
                try { if (Directory.Exists(binaryStage)) Directory.Delete(binaryStage, true); }
                catch (Exception error) { ConfigStore.Log(error); }
            }
        }

        public static void ExportReminders(string filename, ReminderDocument reminders)
        {
            if (reminders == null) throw new InvalidOperationException("Напоминания не загружены.");
            string temp = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(ReminderDocument)).WriteObject(output, reminders);
                    output.Flush(true);
                }
                using (FileStream input = File.OpenRead(temp)) ReadReminders(input);
                CommitExport(temp, filename);
            }
            finally { DeleteExportTemp(temp); }
        }

        public static ReminderDocument ImportReminders(string filename)
        {
            using (FileStream input = File.OpenRead(filename))
                return ReadReminders(input);
        }

        public static void SaveImportedReminders(ReminderDocument document)
        {
            // ReminderStore.Save creates an automatic .bak file of existing reminders.
            ReminderStore.Save(document);
        }

        private static AppConfig ReadBackup(string filename, string stage, out ReminderDocument reminders,
            out Dictionary<string, string> shortcuts, out CollectionDocument collections,
            out Dictionary<string, string> images, string programExtraction = null, bool extractExecutable = false, bool extractPackages = false)
        {
            AppConfig restored = null;
            reminders = null;
            collections = null;
            shortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (FileStream input = File.OpenRead(filename))
            using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("Слишком много файлов в архиве.");
                long total = 0;
                long coverTotal = 0;
                long wallpaperTotal = 0;
                long packageTotal = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    // Ignore only the directory marker. Unknown files and path traversal are rejected.
                    if (entry.Length == 0 && (name == "Shortcuts/" ||
                        name == "Collections/" || name == "Collections/Images/" ||
                        name == "Backgrounds/")) continue;
                    bool isCover = name.StartsWith("Collections/Images/", StringComparison.OrdinalIgnoreCase)
                        && name.LastIndexOf('/') == "Collections/Images/".Length - 1
                        && CollectionStore.ValidCoverFile(Path.GetFileName(name));
                    bool isWallpaper = name.StartsWith("Backgrounds/", StringComparison.OrdinalIgnoreCase)
                        && name.LastIndexOf('/') == "Backgrounds/".Length - 1
                        && CustomBackgroundStore.IsValidFileName(Path.GetFileName(name));
                    bool isPluginBackup = name == PluginPackageStore.BackupArchiveEntry;
                    bool isCore = name == ExecutableEntry;
                    bool isPackage = PluginPackageStore.IsPackageBackupEntry(name);
                    bool allowed = isCore || isPackage || isPluginBackup || name == "apps.json" || name == "reminders.json" ||
                        name == "backup-info.txt" || name == "Collections/collections.json" ||
                        isCover || isWallpaper ||
                        (name.StartsWith("Shortcuts/", StringComparison.OrdinalIgnoreCase) &&
                            name.LastIndexOf('/') == "Shortcuts/".Length - 1 &&
                            (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)));
                    // Collections use a larger documented JSON limit than other entries.
                    long entryLimit = isCore ? MaxExecutableBytes : isPackage ? PluginPackageStore.MaxArchiveBytes : isPluginBackup ? PluginPackageStore.MaxBackupDocument : name == "Collections/collections.json"
                        ? CollectionStore.MaxDocumentBytes :
                        isWallpaper ? CustomBackgroundStore.MaxFileBytes : MaxEntryBytes;
                    if (!allowed || !entries.Add(name) || entry.Length > entryLimit)
                        throw new InvalidDataException("Небезопасный или неподдерживаемый файл в ZIP: " + name);
                    if (isCover) coverTotal += entry.Length;
                    else if (isWallpaper) wallpaperTotal += entry.Length;
                    else if (isPackage) packageTotal += entry.Length;
                    else if (!isPluginBackup && !isCore) total += entry.Length;
                    if (total > MaxTotalBytes || coverTotal > MaxCoverTotalBytes ||
                        wallpaperTotal > MaxBackgroundTotalBytes || packageTotal > PluginPackageStore.MaxPackageBackupTotal)
                        throw new InvalidDataException("Резервная копия слишком большая.");
                    if (isCore || isPackage)
                    {
                        if (entry.FullName != name || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Недопустимая запись программы в архиве.");
                        ReadBinary(entry, name, programExtraction, extractExecutable, extractPackages);
                    }
                    else if (isPluginBackup)
                    {
                        byte[] bytes;
                        using (Stream content = entry.Open()) bytes = PluginPackageStore.ReadBackupBytes(content);
                        if (bytes.Length != entry.Length) throw new InvalidDataException("Неполная копия данных плагинов.");
                        if (stage != null) File.WriteAllBytes(Path.Combine(stage, "plugin-recovery.json"), bytes);
                    }
                    else if (name == "apps.json")
                    {
                        using (Stream content = entry.Open()) restored = ReadAppConfig(content);
                    }
                    else if (name == "reminders.json")
                    {
                        using (Stream content = entry.Open()) reminders = ReadReminders(content);
                    }
                    else if (name == "Collections/collections.json")
                    {
                        using (Stream content = entry.Open())
                            collections = CollectionStore.ReadDocument(content);
                    }
                    else if (isCover || isWallpaper)
                    {
                        string nameOnly = Path.GetFileName(name);
                        string folder = isCover ? "Covers" : "Backgrounds";
                        string target = stage == null ? null : Path.Combine(stage, folder, nameOnly);
                        using (Stream content = entry.Open())
                        using (MemoryStream image = new MemoryStream())
                        {
                            byte[] buffer = new byte[8192];
                            int read;
                            while ((read = content.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                if (image.Length + read > entryLimit)
                                    throw new InvalidDataException("Изображение в ZIP превышает лимит.");
                                image.Write(buffer, 0, read);
                            }
                            if (image.Length != entry.Length)
                                throw new InvalidDataException("Неполное изображение в ZIP.");
                            image.Position = 0;
                            CustomBackgroundStore.ValidateImage(image, Path.GetExtension(nameOnly), entryLimit);
                            if (target != null)
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                image.Position = 0;
                                using (FileStream outFile = File.Create(target)) image.CopyTo(outFile);
                            }
                        }
                        images[name] = target;
                    }
                    else if (name.StartsWith("Shortcuts/", StringComparison.OrdinalIgnoreCase))
                    {
                        string file = Path.GetFileName(name);
                        string target = stage == null ? null : Path.Combine(stage, file);
                        using (Stream content = entry.Open())
                        {
                            if (target == null) content.CopyTo(Stream.Null);
                            else using (FileStream outFile = File.Create(target)) content.CopyTo(outFile);
                        }
                        shortcuts[name] = target;
                    }
                }
            }
            if (restored == null || reminders == null)
                throw new InvalidDataException("Архив должен содержать apps.json и reminders.json.");
            // Archives created before Collections were introduced remain valid.
            // They must not overwrite or delete existing collection data.
            HashSet<string> requiredImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (collections != null)
            {
                foreach (CollectionDefinition group in collections.Collections)
                    foreach (CollectionEntry entry in group.Entries)
                        if (!String.IsNullOrEmpty(entry.CoverFile))
                            requiredImages.Add("Collections/Images/" + entry.CoverFile);
                foreach (string image in requiredImages)
                    if (!images.ContainsKey(image))
                        throw new InvalidDataException("В архиве отсутствует обложка: " + image);
            }
            int archivedCoverCount = 0;
            foreach (string key in images.Keys)
                if (key.StartsWith("Collections/Images/", StringComparison.OrdinalIgnoreCase))
                    archivedCoverCount++;
            if (archivedCoverCount != requiredImages.Count)
                throw new InvalidDataException("В архиве есть обложки, не относящиеся к подборкам.");
            if (CustomBackgroundStore.IsCustomPreset(restored.MainBackgroundPreset) &&
                !images.ContainsKey("Backgrounds/" + restored.MainBackgroundPreset.Substring(7)))
                throw new InvalidDataException("В архиве нет выбранного пользовательского фона.");
            foreach (AppItem item in restored.Items)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.Path))
                    throw new InvalidDataException("Недопустимая запись приложения в архиве.");
                if (item.Path.StartsWith("Shortcuts/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!shortcuts.ContainsKey(item.Path))
                        throw new InvalidDataException("В архиве отсутствует ярлык: " + item.Path);
                    item.Path = Path.Combine(ConfigStore.ShortcutsDirectory, Path.GetFileName(item.Path));
                }
                else if (ConfigStore.IsOwnedShortcut(item.Path))
                {
                    // A raw local apps.json is not a portable backup.
                    throw new InvalidDataException("Резервная копия содержит абсолютные пути к внутренним ярлыкам.");
                }
            }
            return restored;
        }

        // Existing images may be referenced by an older .bak document. Never
        // overwrite a differently-valued image with the same internal filename.
        private static bool SameFile(string first, string second)
        {
            FileInfo a = new FileInfo(first), b = new FileInfo(second);
            if (a.Length != b.Length) return false;
            using (FileStream left = File.OpenRead(first))
            using (FileStream right = File.OpenRead(second))
            {
                byte[] x = new byte[16384], y = new byte[16384];
                int read;
                while ((read = left.Read(x, 0, x.Length)) > 0)
                {
                    int consumed = 0;
                    while (consumed < read)
                    {
                        int chunk = right.Read(y, consumed, read - consumed);
                        if (chunk == 0) return false;
                        consumed += chunk;
                    }
                    for (int i = 0; i < read; i++)
                        if (x[i] != y[i]) return false;
                }
                return right.ReadByte() == -1;
            }
        }

        private static void RollbackFile(string original, string active, bool existedBefore, List<Exception> errors)
        {
            try
            {
                if (existedBefore)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(active));
                    File.Copy(original, active, true);
                }
                else if (File.Exists(active)) File.Delete(active);
            }
            catch (Exception ex)
            {
                errors.Add(new IOException("Не удалось восстановить " + active + ": " + ex.Message, ex));
            }
        }

        public static string RestoreAll(string filename, string selectedBackupRoot = null)
        {
            string stage = Path.Combine(Path.GetTempPath(), "AppShelfRestore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                ReminderDocument reminders;
                Dictionary<string, string> shortcuts;
                CollectionDocument collections;
                Dictionary<string, string> images;
                AppConfig restored = ReadBackup(filename, stage, out reminders, out shortcuts,
                    out collections, out images);
                // Save the current state before touching it. Never delete prior backups.
                Directory.CreateDirectory(ConfigStore.DirectoryPath);
                // Current releases use the user-selected location. The no-argument
                // API stays available for legacy callers and isolated test fixtures.
                string safetyRoot = String.IsNullOrWhiteSpace(selectedBackupRoot)
                    ? Path.Combine(ConfigStore.DirectoryPath, "Backups")
                    : Path.Combine(BackupAutomation.ValidateRoot(selectedBackupRoot), "BeforeRestore");
                string backupDir = Path.Combine(safetyRoot,
                    "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(backupDir);
                List<string> documents = new List<string> { "apps.json", "apps.json.bak", "reminders.json",
                    "reminders.json.bak", "Collections/collections.json",
                    "Collections/collections.json.bak" };
                string recovered = Path.Combine(stage, "plugin-recovery.json");
                string recoveredPath = null;
                if (File.Exists(recovered))
                {
                    recoveredPath = new PluginPackageStore(ConfigStore.DirectoryPath).RecoveredPath();
                    documents.Add(PluginPackageStore.RecoveredRelativePath);
                }
                HashSet<string> savedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in documents)
                {
                    string active = Path.Combine(ConfigStore.DirectoryPath, name);
                    try
                    {
                        string safeDestination = Path.Combine(backupDir, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(safeDestination));
                        File.Copy(active, safeDestination);
                        savedDocuments.Add(name);
                    }
                    catch (FileNotFoundException) { }
                    // A profile upgraded from an older version may not have
                    // the Collections directory at all. This is not an error.
                    catch (DirectoryNotFoundException)
                    {
                        if (Directory.Exists(Path.GetDirectoryName(active))) throw;
                    }
                }
                HashSet<string> savedShortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(ConfigStore.ShortcutsDirectory))
                {
                    string beforeShortcuts = Path.Combine(backupDir, "Shortcuts");
                    Directory.CreateDirectory(beforeShortcuts);
                    foreach (string file in Directory.GetFiles(ConfigStore.ShortcutsDirectory, "*", SearchOption.TopDirectoryOnly))
                    {
                        File.Copy(file, Path.Combine(beforeShortcuts, Path.GetFileName(file)), true);
                        savedShortcuts.Add(Path.GetFileName(file));
                    }
                }
                Directory.CreateDirectory(ConfigStore.ShortcutsDirectory);
                List<string> retiredShortcuts = ConfigStore.RetiredShortcutCandidates();
                List<string> newImages = new List<string>();
                try
                {
                    if (collections != null)
                    {
                        Directory.CreateDirectory(CollectionStore.ImagesPath);
                        foreach (KeyValuePair<string, string> item in images)
                        {
                            if (!item.Key.StartsWith("Collections/Images/", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string destination = CollectionStore.CoverPath(Path.GetFileName(item.Key));
                            if (File.Exists(destination))
                            {
                                if (!SameFile(item.Value, destination))
                                    throw new InvalidDataException("Конфликт обложки в локальной папке: " +
                                        Path.GetFileName(destination) + ". Существующий файл сохранён.");
                            }
                            else
                            {
                                CustomBackgroundStore.CopyImageFile(item.Value, destination, false);
                                newImages.Add(destination);
                            }
                        }
                    }
                    foreach (KeyValuePair<string, string> item in images)
                    {
                        if (!item.Key.StartsWith("Backgrounds/", StringComparison.OrdinalIgnoreCase))
                            continue;
                        Directory.CreateDirectory(CustomBackgroundStore.DirectoryPath);
                        string preset = "custom:" + Path.GetFileName(item.Key);
                        string destination = CustomBackgroundStore.FilePath(preset);
                        if (destination == null)
                            throw new InvalidDataException("Недопустимое имя пользовательского фона.");
                        if (File.Exists(destination))
                        {
                            if (!SameFile(item.Value, destination))
                                throw new InvalidDataException("Конфликт пользовательского фона: " +
                                    Path.GetFileName(destination));
                        }
                        else
                        {
                            CustomBackgroundStore.CopyImageFile(item.Value, destination, false);
                            newImages.Add(destination);
                        }
                    }
                    foreach (KeyValuePair<string, string> item in shortcuts)
                        File.Copy(item.Value, Path.Combine(ConfigStore.ShortcutsDirectory,
                            Path.GetFileName(item.Key)), true);
                    // Restoring data must not change where backups are stored on this PC.
                    if (!String.IsNullOrWhiteSpace(selectedBackupRoot))
                        restored.BackupDirectory = BackupAutomation.ValidateRoot(selectedBackupRoot);
                    ConfigStore.Save(restored, false);
                    ReminderStore.Save(reminders);
                    if (collections != null) CollectionStore.Save(collections);
                    if (recoveredPath != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(recoveredPath));
                        File.Copy(recovered, recoveredPath, true);
                    }
                }
                catch (Exception importError)
                {
                    // Every component gets a rollback attempt, even if an earlier one failed.
                    List<Exception> rollbackErrors = new List<Exception>();
                    foreach (string name in documents)
                        RollbackFile(Path.Combine(backupDir, name), Path.Combine(ConfigStore.DirectoryPath, name),
                            savedDocuments.Contains(name), rollbackErrors);
                    string oldShortcuts = Path.Combine(backupDir, "Shortcuts");
                    foreach (KeyValuePair<string, string> item in shortcuts)
                    {
                        string shortcutFileName = Path.GetFileName(item.Key);
                        string active = Path.Combine(ConfigStore.ShortcutsDirectory, shortcutFileName);
                        string original = Path.Combine(oldShortcuts, shortcutFileName);
                        RollbackFile(original, active, savedShortcuts.Contains(shortcutFileName), rollbackErrors);
                    }
                    foreach (string image in newImages)
                    {
                        try { if (File.Exists(image)) File.Delete(image); }
                        catch (Exception error) { rollbackErrors.Add(
                            new IOException("Не удалось удалить новый файл изображения: " + image, error)); }
                    }
                    foreach (Exception error in rollbackErrors) ConfigStore.Log(error);
                    throw new BackupRestoreException(importError, rollbackErrors, backupDir);
                }
                // Pruning must not run between the writes of a multi-file restore.
                ConfigStore.CleanupRetiredShortcuts(retiredShortcuts);
                return backupDir;
            }
            finally
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            }
        }
    }


    // All automatic backups use the existing validated portable ZIP format.
    // The chosen root is deliberately outside the live AppShelf data and EXE trees.
    internal static class BackupAutomation
    {
        internal static readonly object Gate = new object();
        internal const int DefaultKeepDaily = 7;
        internal const int KeepUpdates = 3;
        internal const int DefaultQuotaMb = 1024;

        private static bool Inside(string candidate, string directory)
        {
            string root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string value = Path.GetFullPath(candidate).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return value.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ValidateRoot(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Выбери папку для резервных копий.");
            if (!Path.IsPathRooted(directory))
                throw new InvalidOperationException("Папка резервных копий должна иметь абсолютный путь.");
            string full = Path.GetFullPath(directory.Trim()).TrimEnd('\\', '/');
            if (full.Length == 0 || Path.GetPathRoot(full).TrimEnd('\\', '/') ==
                full.TrimEnd('\\', '/'))
                throw new InvalidOperationException("Нельзя сохранять копии в корень диска.");
            string data = Path.GetFullPath(ConfigStore.DirectoryPath);
            if (Inside(full, data) || Inside(data, full))
                throw new InvalidOperationException("Папка копий не должна пересекаться с папкой данных AppShelf.");
            string exe = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            if (Inside(full, exe) || Inside(exe, full))
                throw new InvalidOperationException("Папка копий не должна пересекаться с папкой программы.");
            return full;
        }

        internal static bool AutomaticEnabled(AppConfig config)
        {
            return config != null && config.AutomaticBackupsEnabled != false;
        }

        internal static int IntervalDays(AppConfig config)
        {
            return config != null && config.AutomaticBackupDays == 7 ? 7 : 1;
        }

        internal static int KeepDaily(AppConfig config)
        {
            return config != null && (config.AutomaticBackupKeep == 3 ||
                config.AutomaticBackupKeep == 14) ? config.AutomaticBackupKeep : DefaultKeepDaily;
        }

        internal static int QuotaMb(AppConfig config)
        {
            return config != null && (config.AutomaticBackupQuotaMb == 512 ||
                config.AutomaticBackupQuotaMb == 2048 ||
                config.AutomaticBackupQuotaMb == 4096)
                ? config.AutomaticBackupQuotaMb : DefaultQuotaMb;
        }

        private static void ReadSavedDocuments(out AppConfig config, out ReminderDocument reminders)
        {
            string settingsWarning, reminderWarning;
            bool settingsSafe, remindersSafe;
            config = ConfigStore.Load(out settingsWarning, out settingsSafe);
            reminders = ReminderStore.Load(out reminderWarning, out remindersSafe);
            if (!settingsSafe || !remindersSafe)
                throw new InvalidDataException("Нельзя создавать копию из повреждённых данных: " +
                    (settingsWarning ?? "") + " " + (reminderWarning ?? ""));
        }

        private static void AddSignatureFiles(List<string> files, string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    files.Add(file);
            }
        }

        // Include only files exported into ZIP; never scan arbitrary AppData or backup roots.
        internal static string DataSignature()
        {
            string root = ConfigStore.DirectoryPath;
            List<string> files = new List<string>();
            files.Add(Path.Combine(root, "apps.json"));
            files.Add(Path.Combine(root, "reminders.json"));
            files.Add(Path.Combine(root, "Collections", "collections.json"));
            AddSignatureFiles(files, ConfigStore.ShortcutsDirectory);
            AddSignatureFiles(files, CollectionStore.ImagesPath);
            AddSignatureFiles(files, CustomBackgroundStore.DirectoryPath);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            StringBuilder info = new StringBuilder();
            foreach (string file in files)
            {
                info.Append(file.Substring(root.Length)).Append('|');
                FileInfo item = new FileInfo(file);
                if (item.Exists)
                {
                    // apps.json can be rewritten with identical content when the
                    // window closes. Hash documents (and small files) so that
                    // timestamp-only writes do not create duplicate backups.
                    if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                        item.Length <= 2L * 1024L * 1024L)
                    {
                        using (SHA256 fileHash = SHA256.Create())
                        using (FileStream input = File.OpenRead(file))
                            info.Append(Convert.ToBase64String(fileHash.ComputeHash(input)));
                    }
                    else info.Append(item.Length).Append('|').Append(item.LastWriteTimeUtc.Ticks);
                }
                else info.Append("missing");
                info.Append('\n');
            }
            PluginPackageStore pluginStore = new PluginPackageStore(root);
            byte[] pluginBackup = pluginStore.CaptureBackup();
            string warning; bool canSave;
            AppConfig policy = ConfigStore.Load(out warning, out canSave);
            if (!canSave) throw new InvalidDataException("Не удалось прочитать настройки копирования.");
            if (policy.BackupPluginPackages != false) info.Append(pluginStore.PackageSignature());
            string executable = BackupStore.CurrentExecutable();
            if (policy.BackupExecutable != false && executable != null)
                info.Append("program|").Append(Sha256File(executable)).Append('\n');
            if (pluginBackup != null)
                using (SHA256 hash = SHA256.Create())
                    info.Append("plugins|").Append(Convert.ToBase64String(hash.ComputeHash(pluginBackup))).Append('\n');
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(info.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        private static FileInfo[] ValidDailyCopies(string folder)
        {
            if (!Directory.Exists(folder)) return new FileInfo[0];
            List<FileInfo> files = new List<FileInfo>();
            foreach (string path in Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly))
            {
                FileInfo info = new FileInfo(path);
                // Only manage archives written by AppShelf, never unrelated user files.
                // Cloud on-demand files may be reparse-point placeholders; deleting a
                // file placeholder deletes that entry, not an arbitrary target folder.
                if (Regex.IsMatch(info.Name, @"\AAppShelf-\d{8}-\d{6}-[0-9a-f]{6}\.zip\z",
                    RegexOptions.IgnoreCase))
                    files.Add(info);
            }
            files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
            return files.ToArray();
        }

        internal static DateTime LastDaily(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return DateTime.MinValue;
            FileInfo[] backups = ValidDailyCopies(Path.Combine(root, "Automatic"));
            return backups.Length == 0 ? DateTime.MinValue : backups[0].LastWriteTime;
        }

        internal static string LastError(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return null;
            string path = Path.Combine(root, "Automatic", ".last-error");
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception error) { ConfigStore.Log(error); return null; }
        }

        internal static void RecordError(string root, Exception error)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            try
            {
                string folder = Path.Combine(ValidateRoot(root), "Automatic");
                if (!Directory.Exists(folder)) return;
                File.WriteAllText(Path.Combine(folder, ".last-error"),
                    DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ": " + error.Message);
            }
            catch (Exception exception) { ConfigStore.Log(exception); }
        }

        private static long FileLength(string file)
        {
            return File.Exists(file) ? new FileInfo(file).Length : 0;
        }

        private static long GroupSize(string folder)
        {
            long length = 0;
            if (!Directory.Exists(folder)) return 0;
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly))
                length += FileLength(file);
            return length;
        }

        private sealed class Retained
        {
            internal string Path;
            internal DateTime Utc;
            internal long Size;
            internal bool Folder;
            internal bool Protected;
        }

        private static List<Retained> EnumerateBackups(string root, AppConfig config)
        {
            List<Retained> entries = new List<Retained>();
            string daily = Path.Combine(root, "Automatic");
            FileInfo[] files = ValidDailyCopies(daily);
            for (int index = 0; index < files.Length; index++)
            {
                entries.Add(new Retained { Path = files[index].FullName,
                    Utc = files[index].LastWriteTimeUtc, Size = files[index].Length,
                    Protected = index == 0 });
            }
            string updates = Path.Combine(root, "BeforeUpdate");
            if (Directory.Exists(updates))
            {
                List<DirectoryInfo> groups = new List<DirectoryInfo>();
                foreach (string path in Directory.GetDirectories(updates, "*", SearchOption.TopDirectoryOnly))
                {
                    DirectoryInfo directory = new DirectoryInfo(path);
                    if ((directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        IsCompletedUpdateSet(directory))
                        groups.Add(directory);
                }
                groups.Sort(delegate(DirectoryInfo a, DirectoryInfo b) {
                    return b.CreationTimeUtc.CompareTo(a.CreationTimeUtc);
                });
                for (int index = 0; index < groups.Count; index++)
                {
                    entries.Add(new Retained { Path = groups[index].FullName,
                        Utc = groups[index].CreationTimeUtc, Size = GroupSize(groups[index].FullName),
                        Folder = true, Protected = index == 0 });
                }
            }
            return entries;
        }

        // Old releases atomically renamed their folders. New sets use a distinct
        // suffix and a completion marker instead: cloud clients can open a new
        // EXE without FILE_SHARE_DELETE and prevent renaming its parent folder.
        private const string UpdateSetMarker = ".complete";
        private const string UpdateSetMarkerText = "AppShelf backup complete v1";

        private static bool IsUpdateSetName(string name)
        {
            return Regex.IsMatch(name,
                @"\A(?:\d{8}-\d{6}-v\d+\.\d+\.\d+(?:-beta\.[1-9]\d*)?-to-\d+\.\d+\.\d+(?:-beta\.[1-9]\d*)?|legacy-\d{8}-\d{6})-[0-9a-f]{6}(?:-set)?\z",
                RegexOptions.IgnoreCase);
        }

        private static bool IsCompletedUpdateSet(DirectoryInfo directory)
        {
            if (!IsUpdateSetName(directory.Name)) return false;
            if (!directory.Name.EndsWith("-set", StringComparison.OrdinalIgnoreCase))
                return true; // Atomic publication by older AppShelf versions.
            string marker = Path.Combine(directory.FullName, UpdateSetMarker);
            // File.Exists also returns false on access errors. Only a genuinely
            // missing marker is incomplete; an unreadable marker is preserved.
            try
            {
                return String.Equals(File.ReadAllText(marker), UpdateSetMarkerText,
                    StringComparison.Ordinal);
            }
            catch (FileNotFoundException) { return false; }
        }

        private static void CompleteUpdateSet(string folder)
        {
            // All payload streams are already closed and hashes verified.
            // No rename or deletion of a payload is needed to publish the set.
            string marker = Path.Combine(folder, UpdateSetMarker);
            using (FileStream output = new FileStream(marker, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(UpdateSetMarkerText);
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
        }

        // An interrupted shutdown may leave only temporary ZIPs or an unpublished
        // pre-update folder. Clean our own stale staging entries, not user archives.
        private static void CleanupStaleTemps(string root)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            string daily = Path.Combine(root, "Automatic");
            if (Directory.Exists(daily))
                foreach (string file in Directory.GetFiles(daily, "*.tmp", SearchOption.TopDirectoryOnly))
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                    }
                    catch (Exception error) { ConfigStore.Log(error); }
            string beforeUpdate = Path.Combine(root, "BeforeUpdate");
            if (Directory.Exists(beforeUpdate))
                foreach (string folder in Directory.GetDirectories(beforeUpdate, ".tmp-*",
                    SearchOption.TopDirectoryOnly))
                    try
                    {
                        DirectoryInfo info = new DirectoryInfo(folder);
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                            info.CreationTimeUtc < cutoff &&
                            (Regex.IsMatch(info.Name, @"\A\.tmp-\d{8}-\d{6}-v", RegexOptions.IgnoreCase) ||
                             Regex.IsMatch(info.Name, @"\A\.tmp-legacy-\d{8}-\d{6}-", RegexOptions.IgnoreCase)))
                            Directory.Delete(folder, true);
                    }
                    catch (Exception error) { ConfigStore.Log(error); }
            if (Directory.Exists(beforeUpdate))
                foreach (string folder in Directory.GetDirectories(beforeUpdate, "*-set",
                    SearchOption.TopDirectoryOnly))
                    try
                    {
                        DirectoryInfo info = new DirectoryInfo(folder);
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                            info.CreationTimeUtc < cutoff && IsUpdateSetName(info.Name) &&
                            !IsCompletedUpdateSet(info))
                            Directory.Delete(folder, true);
                    }
                    catch (Exception error) { ConfigStore.Log(error); }
        }

        // Keep one known-good copy in each class. Never touch Manual or BeforeRestore.
        private static void Prune(string root, AppConfig config)
        {
            List<Retained> entries = EnumerateBackups(root, config);
            int dailyCount = 0, updateCount = 0;
            foreach (Retained entry in entries) if (entry.Folder) updateCount++; else dailyCount++;
            List<Retained> oldest = new List<Retained>(entries);
            oldest.Sort(delegate(Retained a, Retained b) { return a.Utc.CompareTo(b.Utc); });
            foreach (Retained entry in oldest)
            {
                bool exceed = entry.Folder ? updateCount > KeepUpdates : dailyCount > KeepDaily(config);
                if (!exceed || entry.Protected) continue;
                try
                {
                    if (entry.Folder) Directory.Delete(entry.Path, true); else File.Delete(entry.Path);
                    entries.Remove(entry);
                    if (entry.Folder) updateCount--; else dailyCount--;
                }
                catch (Exception error) { ConfigStore.Log(error); }
            }
            long total = 0;
            foreach (Retained entry in entries) total += entry.Size;
            long quota = QuotaMb(config) * 1024L * 1024L;
            foreach (Retained entry in oldest)
            {
                if (total <= quota) break;
                if (entry.Protected || !entries.Contains(entry)) continue;
                try
                {
                    if (entry.Folder) Directory.Delete(entry.Path, true); else File.Delete(entry.Path);
                    total -= entry.Size;
                    entries.Remove(entry);
                }
                catch (Exception error) { ConfigStore.Log(error); }
            }
        }

        internal static bool CreateAutomatic(AppConfig config)
        {
            if (!AutomaticEnabled(config) || String.IsNullOrWhiteSpace(config.BackupDirectory))
                return false;
            lock (Gate)
            {
                string root = ValidateRoot(config.BackupDirectory);
                CleanupStaleTemps(root);
                string daily = Path.Combine(root, "Automatic");
                Directory.CreateDirectory(daily);
                FileInfo[] files = ValidDailyCopies(daily);
                if (files.Length > 0 && DateTime.UtcNow - files[0].LastWriteTimeUtc <
                    TimeSpan.FromDays(IntervalDays(config))) return false;
                string before = DataSignature();
                string stamp = Path.Combine(daily, ".last-signature");
                if (files.Length > 0 && File.Exists(stamp) &&
                    String.Equals(File.ReadAllText(stamp).Trim(), before,
                        StringComparison.OrdinalIgnoreCase)) return false;

                string filename = Path.Combine(daily, "AppShelf-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip");
                try
                {
                    AppConfig savedConfig;
                    ReminderDocument savedReminders;
                    ReadSavedDocuments(out savedConfig, out savedReminders);
                    BackupStore.ExportAll(filename, savedConfig, savedReminders);
                    if (!String.Equals(before, DataSignature(), StringComparison.Ordinal))
                        throw new IOException("Данные изменились во время создания резервной копии.");
                }
                catch
                {
                    try { if (File.Exists(filename)) File.Delete(filename); } catch { }
                    throw;
                }
                try
                {
                    File.WriteAllText(stamp, before);
                    string errorFile = Path.Combine(daily, ".last-error");
                    if (File.Exists(errorFile)) File.Delete(errorFile);
                }
                catch (Exception error) { ConfigStore.Log(error); }
                try { Prune(root, config); }
                catch (Exception error) { ConfigStore.Log(error); }
                return true;
            }
        }

        private static string Sha256File(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream input = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }

        // The first transition from 2.22.0 still runs its old updater, which
        // creates .previous beside the EXE. Preserve it in the chosen backup
        // folder and remove the adjacent file only after verified copy.
        internal static string MigrateLegacyPreviousExe(AppConfig config, string currentExe)
        {
            if (config == null || String.IsNullOrWhiteSpace(config.BackupDirectory)) return null;
            lock (Gate)
            {
                string legacy = currentExe + ".previous";
                if (!File.Exists(legacy)) return null;
                string root = ValidateRoot(config.BackupDirectory);
                CleanupStaleTemps(root);
                string parent = Path.Combine(root, "BeforeUpdate");
                Directory.CreateDirectory(parent);
                string id = "legacy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6);
                string destination = Path.Combine(parent, id + "-set");
                Directory.CreateDirectory(destination);
                bool completed = false;
                try
                {
                    string candidate = Path.Combine(destination, "AppShelf-previous.exe");
                    string originalHash = Sha256File(legacy);
                    File.Copy(legacy, candidate, false);
                    if (!String.Equals(originalHash, Sha256File(candidate),
                        StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Не удалось проверить прежнюю копию EXE.");
                    CompleteUpdateSet(destination);
                    completed = true;
                    // If another process changed the original, preserve it.
                    try
                    {
                        if (File.Exists(legacy) &&
                            String.Equals(Sha256File(legacy), originalHash,
                                StringComparison.OrdinalIgnoreCase))
                            File.Delete(legacy);
                    }
                    catch (Exception error) { ConfigStore.Log(error); }
                    try { Prune(root, config); }
                    catch (Exception error) { ConfigStore.Log(error); }
                    return destination;
                }
                finally
                {
                    try { if (!completed && Directory.Exists(destination)) Directory.Delete(destination, true); }
                    catch (Exception error) { ConfigStore.Log(error); }
                }
            }
        }

        private sealed class BackupDataChangedException : IOException
        {
            internal BackupDataChangedException()
                : base("Данные изменились во время резервного копирования.") { }
        }

        internal static string CreateBeforeUpdate(AppConfig config,
            string oldExecutable, string installedVersion, string newVersion)
        {
            // Retry only concurrent data changes, not disk-full, access or hash errors.
            for (int attempt = 1; ; attempt++)
            {
                try { return CreateBeforeUpdateOnce(config, oldExecutable, installedVersion, newVersion); }
                catch (BackupDataChangedException)
                {
                    if (attempt >= 3) throw;
                    Thread.Sleep(attempt * 1000);
                }
            }
        }

        private static string CreateBeforeUpdateOnce(AppConfig config,
            string oldExecutable, string installedVersion, string newVersion)
        {
            lock (Gate)
            {
                string root = ValidateRoot(config.BackupDirectory);
                CleanupStaleTemps(root);
                string baseFolder = Path.Combine(root, "BeforeUpdate");
                Directory.CreateDirectory(baseFolder);
                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-v" +
                    installedVersion + "-to-" + newVersion + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6);
                string result = Path.Combine(baseFolder, name + "-set");
                Directory.CreateDirectory(result);
                bool completed = false;
                try
                {
                    string before = DataSignature();
                    string archive = Path.Combine(result, "AppShelf-data.zip");
                    AppConfig savedConfig;
                    ReminderDocument savedReminders;
                    ReadSavedDocuments(out savedConfig, out savedReminders);
                    BackupStore.ExportAll(archive, savedConfig, savedReminders);
                    if (!String.Equals(before, DataSignature(), StringComparison.Ordinal))
                        throw new BackupDataChangedException();
                    string executable = Path.Combine(result, "AppShelf-" + installedVersion + ".exe");
                    File.Copy(oldExecutable, executable, false);
                    if (!String.Equals(Sha256File(executable), Sha256File(oldExecutable),
                        StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Копия исполняемого файла не прошла проверку.");
                    CompleteUpdateSet(result);
                    completed = true;
                }
                finally
                {
                    try { if (!completed && Directory.Exists(result)) Directory.Delete(result, true); }
                    catch (Exception error) { ConfigStore.Log(error); }
                }
                try { Prune(root, config); }
                catch (Exception error) { ConfigStore.Log(error); }
                return result;
            }
        }
    }

    public sealed partial class MainWindow
    {
        private DispatcherTimer _automaticBackupTimer;
        private bool _automaticBackupInProgress;
        private bool _legacyBackupMigrationRunning;

        private void StartAutomaticBackupMonitor()
        {
            if (_automaticBackupTimer != null) return;
            _automaticBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
            _automaticBackupTimer.Tick += delegate
            {
                _automaticBackupTimer.Interval = TimeSpan.FromMinutes(30);
                QueueAutomaticBackup();
                MigrateLegacyExeIfPossible();
            };
            _automaticBackupTimer.Start();
        }

        private async void QueueAutomaticBackup()
        {
            if (_automaticBackupInProgress || !_canSave || !_remindersCanSave ||
                !BackupAutomation.AutomaticEnabled(_config) ||
                String.IsNullOrWhiteSpace(_config.BackupDirectory) ||
                Dispatcher.HasShutdownStarted) return;
            _automaticBackupInProgress = true;
            try
            {
                // Capture only the policy on the UI thread. Validated persisted
                // documents are loaded and compressed entirely in the worker.
                AppConfig policy = _config.Copy();
                await Task.Run(delegate { BackupAutomation.CreateAutomatic(policy); });
            }
            catch (Exception error)
            {
                ConfigStore.Log(error);
                BackupAutomation.RecordError(_config == null ? null : _config.BackupDirectory, error);
            }
            finally { _automaticBackupInProgress = false; }
        }

        private async void MigrateLegacyExeIfPossible()
        {
            if (_legacyBackupMigrationRunning || _config == null ||
                String.IsNullOrWhiteSpace(_config.BackupDirectory) ||
                Dispatcher.HasShutdownStarted) return;
            _legacyBackupMigrationRunning = true;
            try
            {
                AppConfig policy = _config.Copy();
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                await Task.Run(delegate {
                    BackupAutomation.MigrateLegacyPreviousExe(policy, exe);
                });
            }
            catch (Exception error) { ConfigStore.Log(error); }
            finally { _legacyBackupMigrationRunning = false; }
        }

        // The configured root is shared by daily, pre-update and pre-restore backups.
        private bool SelectBackupDirectory(Window owner)
        {
            using (Forms.FolderBrowserDialog picker = new Forms.FolderBrowserDialog())
            {
                picker.Description = "Выбери папку для резервных копий AppShelf (можно Dropbox или OneDrive).";
                picker.ShowNewFolderButton = true;
                picker.SelectedPath = !String.IsNullOrWhiteSpace(_config.BackupDirectory) &&
                    Directory.Exists(_config.BackupDirectory) ? _config.BackupDirectory :
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (picker.ShowDialog() != Forms.DialogResult.OK) return false;
                try
                {
                    string chosen = BackupAutomation.ValidateRoot(picker.SelectedPath);
                    AppConfig pending = _config.Copy();
                    pending.BackupDirectory = chosen;
                    if (!SaveConfigChanges(pending)) return false;
                    MigrateLegacyExeIfPossible();
                    return true;
                }
                catch (Exception error)
                {
                    MessageBox.Show(owner, "Не удалось выбрать папку копий:\n" + error.Message,
                        "AppShelf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
        }

        private bool EnsureBackupDirectory(Window owner)
        {
            if (String.IsNullOrWhiteSpace(_config.BackupDirectory))
                return SelectBackupDirectory(owner);
            try
            {
                BackupAutomation.ValidateRoot(_config.BackupDirectory);
                return true;
            }
            catch (Exception error)
            {
                MessageBox.Show(owner, "Папка резервных копий недоступна или выбрана неправильно:\n" +
                    error.Message, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return SelectBackupDirectory(owner);
            }
        }

        private string RestoreBackup(string filename)
        {
            if (_collectionsWindow != null)
                throw new InvalidOperationException("Закрой окно «Подборки» перед восстановлением архива.");
            bool couldSaveConfig = _canSave;
            bool couldSaveReminders = _remindersCanSave;
            bool placementTimerWasEnabled = _windowPlacementSaveTimer != null && _windowPlacementSaveTimer.IsEnabled;
            bool remindersTimerWasEnabled = _remindersTimer != null && _remindersTimer.IsEnabled;
            bool restored = false;
            bool rollbackIncomplete = false;

            // Block stale callbacks as well as Closing while imported data is on disk.
            _canSave = false;
            _remindersCanSave = false;
            if (_windowPlacementSaveTimer != null) _windowPlacementSaveTimer.Stop();
            if (_remindersTimer != null) _remindersTimer.Stop();
            try
            {
                string safetyCopy = BackupStore.RestoreAll(filename, _config.BackupDirectory);
                restored = true;
                return safetyCopy;
            }
            catch (BackupRestoreException error)
            {
                rollbackIncomplete = error.RollbackErrors.Count != 0;
                throw;
            }
            finally
            {
                // Success or an incomplete rollback must not allow stale in-memory saves.
                if (!restored && !rollbackIncomplete)
                {
                    _canSave = couldSaveConfig;
                    _remindersCanSave = couldSaveReminders;
                    if (placementTimerWasEnabled) _windowPlacementSaveTimer.Start();
                    if (remindersTimerWasEnabled) _remindersTimer.Start();
                }
            }
        }

        private void ShowBackupMenuWindow() { ShowBackupMenuWindow(this); }

        private void ShowBackupMenuWindow(Window owner)
        {
            Window dialog = new Window
            {
                Title = "AppShelf — резервные копии", Owner = owner,
                Width = 530, Height = 445, MinWidth = 440, MinHeight = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            FluentUi.PrepareWindow(dialog);
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FluentUi.StyleSettingsScroll(scroll);
            StackPanel layout = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = layout;
            dialog.Content = scroll;
            TextBlock title = Text("Резервные копии", 23, WhiteBrush);
            title.FontWeight = FontWeights.Bold;
            layout.Children.Add(title);
            TextBlock note = Text("Полная копия содержит приложения, настройки, напоминания, подборки, ярлыки, список плагинов и разрешённые данные плагинов. Исполняемые файлы и пакеты плагинов включаются согласно настройкам. Журнал ошибок не экспортируется.", 12, MutedBrush);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 7, 0, 13);
            // Keep the compact status visible even when every group is collapsed.
            title.Margin = new Thickness(0, 0, 0, 12);
            TextBlock configuredFolder = Text("", 12, WhiteBrush);
            configuredFolder.TextWrapping = TextWrapping.Wrap;
            configuredFolder.Margin = new Thickness(0, 0, 0, 7);
            layout.Children.Add(configuredFolder);
            TextBlock lastBackup = Text("", 12, MutedBrush);
            lastBackup.TextWrapping = TextWrapping.Wrap;
            lastBackup.Margin = new Thickness(0, 0, 0, 10);
            layout.Children.Add(lastBackup);
            TextBlock backupStatus = Text("", 12, MutedBrush);
            backupStatus.TextWrapping = TextWrapping.Wrap;
            backupStatus.Margin = new Thickness(0, 0, 0, 8);
            layout.Children.Add(backupStatus);
            backupStatus.Visibility = Visibility.Collapsed;
            Action refreshInfo = delegate
            {
                configuredFolder.Text = "Папка: " + (String.IsNullOrWhiteSpace(_config.BackupDirectory)
                    ? "не выбрана" : _config.BackupDirectory);
                DateTime recent = DateTime.MinValue;
                try { recent = BackupAutomation.LastDaily(_config.BackupDirectory); }
                catch (Exception error) { ConfigStore.Log(error); }
                lastBackup.Text = recent == DateTime.MinValue
                    ? "Ежедневных копий пока нет."
                    : "Последняя ежедневная копия: " + recent.ToString("dd.MM.yyyy HH:mm");
                string lastIssue = BackupAutomation.LastError(_config.BackupDirectory);
                backupStatus.Text = String.IsNullOrWhiteSpace(lastIssue) ? "" :
                    "Последняя ошибка: " + lastIssue;
                if (!String.IsNullOrWhiteSpace(_config.BackupDirectory))
                {
                    try
                    {
                        DriveInfo drive = new DriveInfo(Path.GetPathRoot(_config.BackupDirectory));
                        if (drive.IsReady)
                            backupStatus.Text += "\nСвободно на диске: " +
                                (drive.AvailableFreeSpace / (1024L * 1024L)) + " МБ.";
                    }
                    catch (Exception) { /* Network/cloud folder may not expose drive metrics. */ }
                }
                backupStatus.Text = backupStatus.Text.Trim();
                backupStatus.Visibility = String.IsNullOrWhiteSpace(backupStatus.Text)
                    ? Visibility.Collapsed : Visibility.Visible;
            };
            refreshInfo();
            Button chooseFolder = ActionButton("Выбрать папку копий…", false);
            chooseFolder.Click += delegate { if (SelectBackupDirectory(dialog)) refreshInfo(); };

            CheckBox automatic = new CheckBox
            {
                Content = "Автоматически создавать резервные копии",
                IsChecked = BackupAutomation.AutomaticEnabled(_config),
                Foreground = WhiteBrush, Margin = new Thickness(0, 6, 0, 8)
            };

            TextBlock cadenceLabel = Text("Периодичность", 12, MutedBrush);
            cadenceLabel.Margin = new Thickness(0, 3, 0, 4);

            ComboBox cadence = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
            cadence.Items.Add("Раз в сутки");
            cadence.Items.Add("Раз в неделю");
            cadence.SelectedIndex = BackupAutomation.IntervalDays(_config) == 7 ? 1 : 0;

            TextBlock countLabel = Text("Количество ежедневных архивов", 12, MutedBrush);
            countLabel.Margin = new Thickness(0, 3, 0, 4);

            ComboBox count = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
            count.Items.Add("3"); count.Items.Add("7"); count.Items.Add("14");
            count.SelectedIndex = BackupAutomation.KeepDaily(_config) == 3 ? 0 :
                BackupAutomation.KeepDaily(_config) == 14 ? 2 : 1;

            TextBlock quotaLabel = Text("Лимит автоматических копий и копий перед обновлением", 12, MutedBrush);
            quotaLabel.TextWrapping = TextWrapping.Wrap;
            quotaLabel.Margin = new Thickness(0, 3, 0, 4);

            ComboBox quota = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
            quota.Items.Add("512 МБ"); quota.Items.Add("1 ГБ");
            quota.Items.Add("2 ГБ"); quota.Items.Add("4 ГБ");
            int currentQuota = BackupAutomation.QuotaMb(_config);
            quota.SelectedIndex = currentQuota == 512 ? 0 : currentQuota == 2048 ? 2 :
                currentQuota == 4096 ? 3 : 1;

            CheckBox includeExecutable = new CheckBox { Content = "Включать AppShelf.exe в ZIP",
                IsChecked = _config.BackupExecutable != false, Foreground = WhiteBrush, Margin = new Thickness(0, 4, 0, 6) };
            CheckBox includePluginPackages = new CheckBox { Content = "Включать установленные пакеты плагинов",
                IsChecked = _config.BackupPluginPackages != false, Foreground = WhiteBrush, Margin = new Thickness(0, 4, 0, 8) };
            TextBlock privateWarning = Text("В архив попадут и приватные плагины. ZIP не шифруется.", 12, MutedBrush);
            privateWarning.TextWrapping = TextWrapping.Wrap;
            privateWarning.Margin = new Thickness(0, 0, 0, 10);
            Button savePolicy = ActionButton("Сохранить настройки бэкапа", false);
            savePolicy.Click += delegate
            {
                if (automatic.IsChecked == true && !EnsureBackupDirectory(dialog)) return;
                AppConfig pending = _config.Copy();
                pending.AutomaticBackupsEnabled = automatic.IsChecked == true;
                pending.BackupExecutable = includeExecutable.IsChecked == true;
                pending.BackupPluginPackages = includePluginPackages.IsChecked == true;
                pending.AutomaticBackupDays = cadence.SelectedIndex == 1 ? 7 : 1;
                pending.AutomaticBackupKeep = count.SelectedIndex == 0 ? 3 :
                    count.SelectedIndex == 2 ? 14 : 7;
                pending.AutomaticBackupQuotaMb = quota.SelectedIndex == 0 ? 512 :
                    quota.SelectedIndex == 2 ? 2048 : quota.SelectedIndex == 3 ? 4096 : 1024;
                if (SaveConfigChanges(pending))
                {
                    refreshInfo();
                    QueueAutomaticBackup();
                }
            };

            Button openBackupFolder = ActionButton("Открыть папку резервных копий", false);
            openBackupFolder.Click += delegate
            {
                if (!EnsureBackupDirectory(dialog)) return;
                try
                {
                    Directory.CreateDirectory(_config.BackupDirectory);
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + _config.BackupDirectory + "\"") { UseShellExecute = true });
                }
                catch (Exception error) { ShowError("Не удалось открыть папку копий.", error); }
            };

            TextBlock backupHelp = Text(
                "Ежедневные копии создаются в фоне, только если данные изменились. " +
                "Перед обновлением сохраняются ZIP и старый EXE. " +
                "Dropbox/OneDrive могут синхронизировать выбранную папку, но AppShelf не проверяет загрузку в облако.",
                12, MutedBrush);
            backupHelp.TextWrapping = TextWrapping.Wrap;
            backupHelp.Margin = new Thickness(0, 0, 0, 12);

            Button exportAll = ActionButton("Экспортировать всё в ZIP…", true);
            exportAll.Click += delegate
            {
                if (!_canSave || !_remindersCanSave) { ShowSaveDisabled(); return; }
                if (!EnsureBackupDirectory(dialog)) return;
                string manualDirectory = Path.Combine(_config.BackupDirectory, "Manual");
                try { Directory.CreateDirectory(manualDirectory); }
                catch (Exception error) { ShowError("Не удалось создать папку ручных копий.", error); return; }
                SaveFileDialog picker = new SaveFileDialog { Title = "Экспорт AppShelf",
                    InitialDirectory = manualDirectory,
                    Filter = "Архив AppShelf (*.zip)|*.zip", FileName = "AppShelf-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip" };
                if (picker.ShowDialog(dialog) != true) return;
                try
                {
                    BackupStore.ExportAll(picker.FileName, _config, _reminderDocument);
                    MessageBox.Show(dialog, "Резервная копия сохранена:\n" + picker.FileName,
                        "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { ShowError("Не удалось экспортировать данные.", ex); }
            };

            Button importAll = ActionButton("Восстановить из ZIP…", false);
            importAll.Click += delegate
            {
                if (owner != this)
                {
                    MessageBox.Show(dialog, "Чтобы восстановить полный ZIP, сначала закрой настройки " +
                        "и открой резервные копии из главного меню AppShelf.",
                        "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!EnsureBackupDirectory(dialog)) return;
                OpenFileDialog picker = new OpenFileDialog { Title = "Восстановление AppShelf",
                    InitialDirectory = _config.BackupDirectory,
                    Filter = "Архив AppShelf (*.zip)|*.zip" };
                if (picker.ShowDialog(dialog) != true) return;
                if (MessageBox.Show(dialog, "Заменить текущие приложения, группы, настройки, напоминания и подборки (если они есть в архиве)?\n" +
                    "Перед заменой будет создана резервная копия текущих файлов.\n" +
                    "Данные плагинов будут доступны для отдельного восстановления в менеджере.\n" +
                    "AppShelf закроется после импорта.", "AppShelf", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try
                {
                    string safetyCopy = RestoreBackup(picker.FileName);
                    MessageBox.Show(dialog, "Восстановление завершено. Запусти AppShelf заново.\n" +
                        "Предыдущие файлы сохранены в:\n" + safetyCopy, "AppShelf",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    dialog.Close();
                    _explicitExitRequested = true;
                    Close();
                }
                catch (Exception ex)
                {
                    ShowError("Не удалось восстановить данные. Текущие файлы проверь в папке Backups.", ex);
                }
            };

            Button extractPrograms = ActionButton("Извлечь программу и плагины из ZIP…", false);
            extractPrograms.Click += async delegate
            {
                OpenFileDialog picker = new OpenFileDialog { Title = "Выберите резервную копию",
                    Filter = "Архив AppShelf (*.zip)|*.zip" };
                if (picker.ShowDialog(dialog) != true) return;
                Window choice = new Window { Owner = dialog, Title = "AppShelf — Извлечение программ",
                    Width = 460, Height = 280, MinWidth = 400, MinHeight = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                FluentUi.PrepareWindow(choice);
                StackPanel options = new StackPanel { Margin = new Thickness(18) };
                choice.Content = options;
                CheckBox core = new CheckBox { Content = "AppShelf.exe", IsChecked = true, Foreground = WhiteBrush };
                CheckBox plugins = new CheckBox { Content = "Пакеты плагинов (.asplugin)", IsChecked = true,
                    Foreground = WhiteBrush, Margin = new Thickness(0, 12, 0, 12) };
                options.Children.Add(core); options.Children.Add(plugins);
                TextBlock info = Text("Файлы будут извлечены в новую папку. Текущая программа и плагины не заменяются и не запускаются. Устанавливайте только доверенные пакеты.", 12, MutedBrush);
                info.TextWrapping = TextWrapping.Wrap; options.Children.Add(info);
                Button proceed = ActionButton("Выбрать папку и извлечь…", true);
                proceed.Click += delegate { if (core.IsChecked == true || plugins.IsChecked == true) choice.DialogResult = true; };
                options.Children.Add(proceed);
                if (choice.ShowDialog() != true) return;
                using (Forms.FolderBrowserDialog folder = new Forms.FolderBrowserDialog())
                {
                    folder.Description = "Выберите папку для извлечения программы и пакетов";
                    if (folder.ShowDialog() != Forms.DialogResult.OK) return;
                    string destination = folder.SelectedPath, archive = picker.FileName;
                    bool copyCore = core.IsChecked == true, copyPlugins = plugins.IsChecked == true;
                    extractPrograms.IsEnabled = false;
                    try
                    {
                        string result = await Task.Run(delegate { return BackupStore.ExtractPrograms(archive, destination, copyCore, copyPlugins); });
                        MessageBox.Show(dialog, "Файлы извлечены:\n" + result + "\n\nДля установки плагинов используйте менеджер плагинов.",
                            "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception error) { ShowError("Не удалось извлечь программы.", error); }
                    finally { extractPrograms.IsEnabled = true; }
                }
            };

            Button exportRem = ActionButton("Экспортировать напоминания в JSON…", false);
            exportRem.Click += delegate
            {
                SaveFileDialog picker = new SaveFileDialog { Title = "Экспорт напоминаний",
                    Filter = "Напоминания (*.json)|*.json", FileName = "AppShelf-reminders.json" };
                if (picker.ShowDialog(dialog) != true) return;
                try { BackupStore.ExportReminders(picker.FileName, _reminderDocument); }
                catch (Exception ex) { ShowError("Не удалось экспортировать напоминания.", ex); }
            };

            Button importRem = ActionButton("Импортировать напоминания из JSON…", false);
            importRem.Click += delegate
            {
                if (!_remindersCanSave) { ShowSaveDisabled(); return; }
                OpenFileDialog picker = new OpenFileDialog { Title = "Импорт напоминаний",
                    Filter = "Напоминания (*.json)|*.json" };
                if (picker.ShowDialog(dialog) != true) return;
                if (MessageBox.Show(dialog, "Заменить текущие напоминания содержимым выбранного файла?",
                    "AppShelf", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try
                {
                    ReminderDocument imported = BackupStore.ImportReminders(picker.FileName);
                    BackupStore.SaveImportedReminders(imported);
                    _reminderDocument = imported;
                    UpdateReminderChip();
                    UpdateReminderList();
                    MessageBox.Show(dialog, "Напоминания импортированы. Старый файл сохранён рядом как reminders.json.bak.",
                        "AppShelf", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { ShowError("Не удалось импортировать напоминания.", ex); }
            };

            Button openFolder = ActionButton("Открыть папку данных AppShelf", false);
            openFolder.Margin = new Thickness(0, 16, 0, 0);
            openFolder.Click += delegate
            {
                Directory.CreateDirectory(ConfigStore.DirectoryPath);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ConfigStore.DirectoryPath + "\"")
                { UseShellExecute = true });
            };
            StackPanel backupRows = new StackPanel();
            Border backupFrame = new Border
            {
                Tag = "BackupSectionsFrame",
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = FluentUi.Brush("#344A66"),
                Background = FluentUi.Brush("#152135"),
                Padding = new Thickness(4),
                Child = backupRows
            };
            layout.Children.Add(backupFrame);

            // Reuse the same accordion rows, arrows and selection treatment as
            // Appearance. The controls are created once; collapsing a group
            // does not discard changes or invoke any backup operation.
            Dictionary<string, Border> bodies = new Dictionary<string, Border>();
            Dictionary<string, System.Windows.Shapes.Path> arrows =
                new Dictionary<string, System.Windows.Shapes.Path>();
            Dictionary<string, Border> headers = new Dictionary<string, Border>();
            LinearGradientBrush selectedHeading = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#223751"),
                (Color)ColorConverter.ConvertFromString("#1D314A"), 0);
            selectedHeading.Freeze();
            Action<string> toggleBackupSection = delegate(string key)
            {
                bool expand = bodies[key].Visibility != Visibility.Visible;
                foreach (KeyValuePair<string, Border> part in bodies)
                {
                    bool active = expand && part.Key == key;
                    part.Value.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                    SetSettingsArrow(arrows[part.Key], active);
                    headers[part.Key].Background = active ? (Brush)selectedHeading : Brushes.Transparent;
                }
            };
            AddSettingsSection(backupRows, "backup-automatic", "Автоматические копии",
                new List<UIElement> { chooseFolder, automatic, cadenceLabel, cadence,
                    countLabel, count, quotaLabel, quota, includeExecutable, includePluginPackages, privateWarning, savePolicy, openBackupFolder, backupHelp },
                bodies, arrows, headers, toggleBackupSection);
            AddSettingsSection(backupRows, "backup-full", "Экспорт и восстановление",
                new List<UIElement> { note, exportAll, importAll, extractPrograms },
                bodies, arrows, headers, toggleBackupSection);
            AddSettingsSection(backupRows, "backup-reminders", "Только напоминания",
                new List<UIElement> { exportRem, importRem },
                bodies, arrows, headers, toggleBackupSection);
            layout.Children.Add(openFolder);
            dialog.ShowDialog();
        }
    }
}
