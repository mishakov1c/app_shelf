// AppShelf plugin package storage. No code is loaded or executed by this layer.
// Compatible with C# 5 / .NET Framework 4.8 and .NET 8.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AppShelf
{
    [DataContract]
    internal sealed class PluginManifest
    {
        [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(Name = "apiVersion", IsRequired = true)] public int ApiVersion { get; set; }
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
        [DataMember(Name = "version", IsRequired = true)] public string Version { get; set; }
        [DataMember(Name = "entryPoint", IsRequired = true)] public string EntryPoint { get; set; }
        [DataMember(Name = "description", EmitDefaultValue = false)] public string Description { get; set; }
        [DataMember(Name = "publisher", EmitDefaultValue = false)] public string Publisher { get; set; }
        [DataMember(Name = "capabilities", IsRequired = true)] public string[] Capabilities { get; set; }
        [DataMember(Name = "backupFiles", EmitDefaultValue = false)] public string[] BackupFiles { get; set; }
    }

    internal sealed class InstalledPlugin
    {
        internal string Id;
        internal PluginManifest Manifest;
        internal bool Enabled;
        internal string Error;
    }

    [DataContract]
    internal sealed class PluginBackupFile
    {
        [DataMember(Name = "path", IsRequired = true)] public string Path { get; set; }
        [DataMember(Name = "base64", IsRequired = true)] public string Base64 { get; set; }
    }

    [DataContract]
    internal sealed class PluginBackupItem
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
        [DataMember(Name = "version", IsRequired = true)] public string Version { get; set; }
        [DataMember(Name = "enabled", IsRequired = true)] public bool Enabled { get; set; }
        [DataMember(Name = "dataIncluded", IsRequired = true)] public bool DataIncluded { get; set; }
        [DataMember(Name = "files", IsRequired = true)] public List<PluginBackupFile> Files { get; set; }
    }

    [DataContract]
    internal sealed class PluginBackupDocument
    {
        [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(Name = "plugins", IsRequired = true)] public List<PluginBackupItem> Plugins { get; set; }
    }

    internal sealed class PluginPackageStore
    {
        internal const int SchemaVersion = 1;
        internal const int ApiVersion = 1;
        internal const long MaxArchiveBytes = 128L * 1024 * 1024;
        internal const long MaxManifestBytes = 64L * 1024;
        private const int MaxEntries = 2048;
        private readonly string root;
        // All store instances in the single AppShelf process share this commit gate.
        // Never hold it during the trust dialog or while acquiring the runtime gate.
        private static readonly object CommitGate = new object();
        private string InstalledRoot { get { return Path.Combine(root, "Installed"); } }
        private string DataRoot { get { return Path.Combine(root, "Data"); } }
        private static readonly Regex IdPattern = new Regex(
            @"\A[a-z][a-z0-9-]{0,31}(?:\.[a-z][a-z0-9-]{0,31}){1,3}\z",
            RegexOptions.CultureInvariant);
        private static readonly Regex SegmentPattern = new Regex(
            @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]{0,79}\z", RegexOptions.CultureInvariant);
        private static readonly Regex VersionPattern = new Regex(
            @"\A(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z",
            RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Capabilities = new HashSet<string>(
            new[] { "command", "window", "background", "settings", "notification", "widget" },
            StringComparer.Ordinal);

        internal PluginPackageStore(string profileRoot)
        {
            if (String.IsNullOrWhiteSpace(profileRoot)) throw new ArgumentException("profileRoot");
            root = Path.Combine(Path.GetFullPath(profileRoot), "Plugins");
        }

        private static void Reject(string reason) { throw new InvalidDataException(reason); }

        private static bool ValidSegment(string value)
        {
            if (!SegmentPattern.IsMatch(value) || value.EndsWith(".", StringComparison.Ordinal)) return false;
            string stem = value.Split('.')[0].ToUpperInvariant();
            return stem != "CON" && stem != "PRN" && stem != "AUX" && stem != "NUL" &&
                !Regex.IsMatch(stem, @"\A(?:COM|LPT)[0-9]\z", RegexOptions.CultureInvariant);
        }

        private static void ValidateId(string id)
        {
            if (id == null || id.Length > 100 || !IdPattern.IsMatch(id))
                Reject("Недопустимый идентификатор плагина.");
            foreach (string segment in id.Split('.'))
                if (!ValidSegment(segment)) Reject("Недопустимый идентификатор плагина.");
        }

        // Archive paths must be relative, canonical, ASCII, and portable on Windows.
        // Reject ADS, traversal, drive/UNC paths, DOS devices and ambiguous trailing dots.
        private static string SafeRelative(string value, bool directory)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 180 || value.IndexOf('\\') >= 0)
                Reject("Недопустимый путь в пакете.");
            if (directory) value = value.Substring(0, value.Length - 1);
            foreach (string segment in value.Split('/'))
                if (!ValidSegment(segment)) Reject("Недопустимый путь в пакете: " + value);
            if (value != "plugin.json" && value != "bin" &&
                !value.StartsWith("bin/", StringComparison.Ordinal))
                Reject("Пакет может содержать только plugin.json и bin/.");
            if (directory && value == "plugin.json") Reject("Манифест должен быть файлом.");
            if (!directory && value == "bin") Reject("bin должен быть каталогом.");
            return value;
        }

        private static string Child(string parent, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Reject("Путь выходит за пределы пакета.");
            return full;
        }

        private static void EnsurePlainDirectory(string path)
        {
            Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Reject("Каталоги-ссылки для плагинов не поддерживаются.");
        }

        private void EnsureInstalledRoot()
        {
            EnsurePlainDirectory(root);
            EnsurePlainDirectory(InstalledRoot);
        }

        // Do not traverse reparse points when reading/removing installed package trees.
        private static void CheckPlainTree(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Reject("Ссылки внутри плагина не поддерживаются.");
            foreach (string item in Directory.GetFileSystemEntries(path))
            {
                FileAttributes attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    Reject("Ссылки внутри плагина не поддерживаются.");
                if ((attributes & FileAttributes.Directory) != 0) CheckPlainTree(item);
            }
        }

        private static PluginManifest ReadManifest(string directory)
        {
            string filename = Path.Combine(directory, "plugin.json");
            FileInfo info = new FileInfo(filename);
            if (!info.Exists || info.Length == 0 || info.Length > MaxManifestBytes)
                Reject("Отсутствует или превышает лимит plugin.json.");
            PluginManifest manifest;
            using (FileStream input = File.OpenRead(filename))
            {
                try
                {
                    manifest = (PluginManifest)new DataContractJsonSerializer(typeof(PluginManifest)).ReadObject(input);
                }
                catch (SerializationException error)
                {
                    throw new InvalidDataException("Некорректный plugin.json.", error);
                }
            }
            if (manifest == null || manifest.SchemaVersion != SchemaVersion || manifest.ApiVersion != ApiVersion)
                Reject("Несовместимая версия схемы или API плагина.");
            ValidateId(manifest.Id);
            if (String.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 ||
                (manifest.Description != null && manifest.Description.Length > 2000) ||
                (manifest.Publisher != null && manifest.Publisher.Length > 100))
                Reject("Некорректное название или описание плагина.");
            if (manifest.Version == null || !VersionPattern.IsMatch(manifest.Version))
                Reject("Версия плагина должна иметь формат X.Y.Z.");
            if (manifest.EntryPoint == null || !manifest.EntryPoint.StartsWith("bin/", StringComparison.Ordinal) ||
                !manifest.EntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                Reject("Точка входа должна быть EXE внутри bin/.");
            string entry = SafeRelative(manifest.EntryPoint, false);
            if (!File.Exists(Child(directory, entry))) Reject("Исполняемый файл плагина отсутствует.");
            if (manifest.Capabilities == null || manifest.Capabilities.Length == 0 ||
                manifest.Capabilities.Length > Capabilities.Count)
                Reject("Не указаны возможности плагина.");
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string capability in manifest.Capabilities)
                if (capability == null || !Capabilities.Contains(capability) || !seen.Add(capability))
                    Reject("Неизвестная или повторяющаяся возможность плагина.");
            ValidateBackupPaths(manifest.BackupFiles);
            return manifest;
        }

        private static void ExtractPackage(string package, string stage)
        {
            if (!String.Equals(Path.GetExtension(package), ".asplugin", StringComparison.OrdinalIgnoreCase))
                Reject("Ожидается пакет .asplugin.");
            using (FileStream input = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length == 0 || input.Length > MaxArchiveBytes) Reject("Недопустимый размер пакета.");
                using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
                        Reject("Недопустимое количество файлов.");
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long total = 0;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                        string relative = SafeRelative(entry.FullName, directory);
                        if (!seen.Add(relative)) Reject("Повторяющийся путь в пакете: " + relative);
                        // ZIP Unix symlink and Windows reparse attributes are not supported.
                        int mode = (entry.ExternalAttributes >> 16) & 0xF000;
                        if (mode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                            Reject("Ссылки в пакете не поддерживаются.");
                        long limit = relative == "plugin.json" ? MaxManifestBytes : MaxArchiveBytes;
                        if (entry.Length < 0 || entry.Length > limit || entry.Length > MaxArchiveBytes - total)
                            Reject("Превышен лимит распакованных данных.");
                        string destination = Child(stage, relative);
                        if (directory)
                        {
                            if (entry.Length != 0) Reject("Некорректная запись каталога.");
                            Directory.CreateDirectory(destination);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (Stream source = entry.Open())
                        using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[81920];
                            long copied = 0;
                            int read;
                            while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                if (read > limit - copied || read > MaxArchiveBytes - total - copied ||
                                    read > entry.Length - copied) Reject("Превышен лимит распакованных данных.");
                                output.Write(buffer, 0, read);
                                copied += read;
                            }
                            if (copied != entry.Length) Reject("Повреждённая запись ZIP.");
                            output.Flush(true);
                            total += copied;
                        }
                    }
                }
            }
        }

        [DataContract]
        private sealed class UpdateJournal
        {
            [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
            [DataMember(Name = "token", IsRequired = true)] public string Token { get; set; }
            [DataMember(Name = "oldVersion", IsRequired = true)] public string OldVersion { get; set; }
            [DataMember(Name = "newVersion", IsRequired = true)] public string NewVersion { get; set; }
        }

        private string JournalPath(string id) { return Child(InstalledRoot, ".update-" + id + ".json"); }
        private string StagePath(string token) { return Child(InstalledRoot, ".stage-" + token); }
        private string PreviousPath(string token) { return Child(InstalledRoot, ".previous-" + token); }

        private static void WriteApproval(string directory, string version)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(version);
            using (FileStream output = new FileStream(Path.Combine(directory, "approved.flag"), FileMode.CreateNew))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
        }

        private void WriteJournal(UpdateJournal journal)
        {
            string path = JournalPath(journal.Id);
            string temporary = Child(InstalledRoot, ".journal-" + journal.Token + ".tmp");
            try
            {
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(UpdateJournal)).WriteObject(output, journal);
                    output.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private UpdateJournal ReadJournal(string id)
        {
            string path = JournalPath(id);
            FileInfo file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length < 1 || file.Length > 4096)
                Reject("Некорректный журнал обновления плагина.");
            UpdateJournal journal;
            using (FileStream input = File.OpenRead(path))
                journal = (UpdateJournal)new DataContractJsonSerializer(typeof(UpdateJournal)).ReadObject(input);
            if (journal == null || journal.Id != id || journal.Token == null ||
                !Regex.IsMatch(journal.Token, @"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant) ||
                journal.OldVersion == null || journal.NewVersion == null ||
                !VersionPattern.IsMatch(journal.OldVersion) || !VersionPattern.IsMatch(journal.NewVersion) ||
                new Version(journal.NewVersion).CompareTo(new Version(journal.OldVersion)) <= 0)
                Reject("Некорректный журнал обновления плагина.");
            return journal;
        }

        private static PluginManifest CheckedVersion(string path, string id, string version, bool requireApproval)
        {
            CheckPlainTree(path);
            PluginManifest manifest = ReadManifest(path);
            if (manifest.Id != id || manifest.Version != version || (requireApproval && !Approved(path, manifest)))
                Reject("Версия или идентификатор пакета не совпадает с журналом обновления.");
            return manifest;
        }

        private static void DeleteTransactionTree(string path)
        {
            if (!Directory.Exists(path)) return;
            CheckPlainTree(path);
            Directory.Delete(path, true);
        }

        // Recovery is called under CommitGate before discovery or execution.
        // The journal exists only after approval and after the old process is stopped.
        // Never guess from unassociated .stage/.previous directories.
        private void RecoverOne(string id)
        {
            ValidateId(id);
            string journalPath = JournalPath(id);
            if (!File.Exists(journalPath)) return;
            UpdateJournal journal = ReadJournal(id);
            string target = Child(InstalledRoot, id);
            string stage = StagePath(journal.Token), previous = PreviousPath(journal.Token);
            if (Directory.Exists(target))
            {
                CheckPlainTree(target);
                PluginManifest current = ReadManifest(target);
                if (current.Id != id) Reject("Конфликт идентификаторов при восстановлении плагина.");
                if (current.Version == journal.OldVersion)
                {
                    // Crash before moving old, or after rollback. No second old tree is expected.
                    if (Directory.Exists(previous)) Reject("Два каталога старой версии; требуется проверка.");
                    DeleteTransactionTree(stage);
                }
                else if (current.Version == journal.NewVersion && Approved(target, current))
                {
                    // New version is installed. Verify old before deletion; a missing old tree
                    // is valid if cleanup completed just before the crash.
                    if (Directory.Exists(stage)) Reject("Конфликт каталогов новой версии.");
                    // The old tree may be partially deleted by a previous cleanup attempt.
                    // The validated new target is authoritative at this point; never follow links.
                    DeleteTransactionTree(previous);
                }
                else Reject("Не удалось однозначно определить установленную версию плагина.");
            }
            else
            {
                // Crash between renames. Restore only the exact old version; never
                // activate staged code in the absence of a valid old package.
                if (!Directory.Exists(previous)) Reject("Не найдена предыдущая версия плагина.");
                CheckedVersion(previous, id, journal.OldVersion, false);
                Directory.Move(previous, target);
                DeleteTransactionTree(stage);
            }
            File.Delete(journalPath);
        }

        private void RecoverOrThrow(string id)
        {
            try { RecoverOne(id); }
            catch (Exception error)
            {
                throw new InvalidDataException("Не завершено восстановление обновления " + id +
                    ". Файлы сохранены. " + error.Message, error);
            }
        }

        // Stage and validate before replacing the installed code. Runtime reservation
        // is obtained after confirmation, before the atomic directory swap.
        internal PluginManifest Install(string package, Func<PluginManifest, bool> confirm, string expectedId = null,
            Func<string, IDisposable> beforeCommit = null, string previousBackupRoot = null, bool requirePreviousBackup = false)
        {
            if (confirm == null) throw new ArgumentNullException("confirm");
            EnsureInstalledRoot();
            string token = Guid.NewGuid().ToString("N");
            string stage = StagePath(token);
            string pendingId = null;
            Directory.CreateDirectory(stage);
            try
            {
                ExtractPackage(package, stage);
                PluginManifest manifest = ReadManifest(stage);
                if (expectedId != null && manifest.Id != expectedId)
                    Reject("Пакет относится к другому плагину.");
                string target = Child(InstalledRoot, manifest.Id);
                lock (CommitGate)
                {
                    RecoverOrThrow(manifest.Id);
                    CheckReplacement(target, manifest);
                }
                using (IDisposable reservation = ConfirmCommit(manifest, confirm, beforeCommit))
                {
                    if (reservation == null) return null;
                    lock (CommitGate)
                    {
                        RecoverOrThrow(manifest.Id);
                        CheckReplacement(target, manifest);
                        WriteApproval(stage, manifest.Version);
                        if (!Directory.Exists(target))
                        {
                            Directory.Move(stage, target);
                            return manifest;
                        }
                        PluginManifest previous = ReadManifest(target);
                        if (requirePreviousBackup) BackupPreviousPackage(manifest.Id, previous.Version, previousBackupRoot);
                        UpdateJournal journal = new UpdateJournal { Id = manifest.Id, Token = token,
                            OldVersion = previous.Version, NewVersion = manifest.Version };
                        WriteJournal(journal);
                        pendingId = manifest.Id;
                        try
                        {
                            Directory.Move(target, PreviousPath(token));
                            Directory.Move(stage, target);
                        }
                        catch
                        {
                            // Roll back using the same recovery logic as startup.
                            // If rollback fails, retain the journal and all remaining files.
                            RecoverOrThrow(manifest.Id);
                            throw;
                        }
                        RecoverOrThrow(manifest.Id);
                        return manifest;
                    }
                }
            }
            finally
            {
                lock (CommitGate)
                {
                    if (pendingId == null || !File.Exists(JournalPath(pendingId))) TryRemoveStage(stage);
                }
            }
        }

        private sealed class EmptyReservation : IDisposable { public void Dispose() { } }

        private static IDisposable ConfirmCommit(PluginManifest manifest, Func<PluginManifest, bool> confirm,
            Func<string, IDisposable> beforeCommit)
        {
            if (!confirm(manifest)) return null;
            return beforeCommit == null ? new EmptyReservation() : beforeCommit(manifest.Id);
        }

        private static void CheckReplacement(string target, PluginManifest manifest)
        {
            if (!Directory.Exists(target)) return;
            CheckPlainTree(target);
            PluginManifest previous = ReadManifest(target);
            if (previous.Id != manifest.Id) Reject("Идентификатор установленного плагина не совпадает.");
            if (new Version(manifest.Version).CompareTo(new Version(previous.Version)) <= 0)
                Reject("Для обновления требуется более новая версия плагина.");
        }

        internal bool HasInstalledDirectory { get { return Directory.Exists(InstalledRoot); } }

        internal PluginManifest ReadEnabled(string id, out string executable)
        {
            lock (CommitGate)
            {
                PluginManifest manifest = ReadInstalled(id);
                string directory = Child(InstalledRoot, id);
                string flag = Path.Combine(directory, "enabled.flag");
                if (!Approved(directory, manifest) || !File.Exists(flag) || new FileInfo(flag).Length != 0)
                    Reject("Плагин отключён или не подтверждён.");
                executable = Child(directory, manifest.EntryPoint);
                return manifest;
            }
        }

        private static void TryRemoveStage(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    CheckPlainTree(path);
                    Directory.Delete(path, true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal PluginManifest ReadInstalled(string id)
        {
            ValidateId(id);
            EnsureInstalledRoot();
            lock (CommitGate)
            {
                RecoverOrThrow(id);
                string path = Child(InstalledRoot, id);
                CheckPlainTree(path);
                PluginManifest manifest = ReadManifest(path);
                if (manifest.Id != id) Reject("Идентификатор установленного плагина не совпадает.");
                return manifest;
            }
        }

        internal string GetDataDirectory(string id)
        {
            ValidateId(id);
            EnsurePlainDirectory(root);
            EnsurePlainDirectory(DataRoot);
            string directory = Child(DataRoot, id);
            EnsurePlainDirectory(directory);
            return directory;
        }

        internal const long MaxPackageBackupTotal = 512L * 1024 * 1024;
        internal const string PackageArchivePrefix = "Plugins/Packages/";

        internal static bool IsPackageBackupEntry(string name)
        {
            if (!name.StartsWith(PackageArchivePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(".asplugin", StringComparison.Ordinal)) return false;
            string id = name.Substring(PackageArchivePrefix.Length,
                name.Length - PackageArchivePrefix.Length - ".asplugin".Length);
            try { ValidateId(id); return true; }
            catch (InvalidDataException) { return false; }
        }

        private static List<string> PackageFiles(string folder)
        {
            CheckPlainTree(folder);
            List<string> files = new List<string> { "plugin.json" };
            string bin = Path.Combine(folder, "bin");
            foreach (string file in Directory.GetFiles(bin, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(folder.Length + 1).Replace('\\', '/');
                SafeRelative(relative, false);
                files.Add(relative);
            }
            files.Sort(StringComparer.Ordinal);
            if (files.Count > MaxEntries) Reject("Слишком много файлов плагина.");
            return files;
        }

        // Only manifest + bin are package code. Runtime approval and enabled flags,
        // private data and diagnostic files outside bin never become installation trust.
        private void WriteInstalledPackage(string id, string destination)
        {
            string folder = Child(InstalledRoot, id);
            PluginManifest manifest = ReadInstalled(id);
            if (!Approved(folder, manifest)) Reject("Плагин не подтверждён: " + id);
            List<string> files = PackageFiles(folder);
            long total = 0;
            using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (ZipArchive zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (string name in files)
                    {
                        using (FileStream input = File.OpenRead(Child(folder, name)))
                        {
                            long length = input.Length;
                            if (length > MaxArchiveBytes - total) Reject("Слишком большой пакет плагина.");
                            total += length;
                            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                            using (Stream target = entry.Open()) input.CopyTo(target);
                        }
                    }
                }
                output.Flush(true);
            }
            if (new FileInfo(destination).Length > MaxArchiveBytes) Reject("Слишком большой архив плагина.");
        }

        internal List<string> CapturePackages(string directory)
        {
            lock (CommitGate)
            {
                List<string> result = new List<string>();
                if (!HasInstalledDirectory) return result;
                long total = 0;
                foreach (InstalledPlugin item in List())
                {
                    if (item.Error != null || item.Manifest == null) Reject("Нельзя создать полную копию плагина: " + item.Id);
                    string filename = Path.Combine(directory, item.Id + ".asplugin");
                    WriteInstalledPackage(item.Id, filename);
                    total += new FileInfo(filename).Length;
                    if (total > MaxPackageBackupTotal) Reject("Превышен общий лимит пакетов плагинов.");
                    result.Add(filename);
                }
                return result;
            }
        }

        internal long EstimatePackageBytes()
        {
            lock (CommitGate)
            {
                long total = 0;
                if (!HasInstalledDirectory) return total;
                foreach (InstalledPlugin item in List())
                {
                    if (item.Error != null || item.Manifest == null) Reject("Нельзя оценить пакет: " + item.Id);
                    string folder = Child(InstalledRoot, item.Id);
                    foreach (string name in PackageFiles(folder))
                        total += new FileInfo(Child(folder, name)).Length;
                }
                return total;
            }
        }

        internal string PackageSignature()
        {
            lock (CommitGate)
            {
                if (!HasInstalledDirectory) return "";
                StringBuilder info = new StringBuilder();
                List<InstalledPlugin> installed = List();
                installed.Sort(delegate(InstalledPlugin a, InstalledPlugin b) { return StringComparer.Ordinal.Compare(a.Id,b.Id); });
                foreach (InstalledPlugin item in installed)
                {
                    if (item.Error != null || item.Manifest == null) Reject("Нельзя создать полную копию плагина: " + item.Id);
                    string folder = Child(InstalledRoot, item.Id);
                    foreach (string name in PackageFiles(folder))
                        using (FileStream input = File.OpenRead(Child(folder, name)))
                        using (SHA256 sha = SHA256.Create())
                            info.Append(item.Id).Append('/').Append(name).Append('|')
                                .Append(Convert.ToBase64String(sha.ComputeHash(input))).Append('\n');
                }
                return info.ToString();
            }
        }

        internal static void ValidateArchivedPackage(string filename, string expectedId)
        {
            ValidateId(expectedId);
            string stage = Path.Combine(Path.GetTempPath(), "AppShelfPkg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                ExtractPackage(filename, stage);
                PluginManifest manifest = ReadManifest(stage);
                if (manifest.Id != expectedId) Reject("Идентификатор пакета не совпадает с архивом.");
            }
            finally { TryRemoveStage(stage); }
        }

        private void BackupPreviousPackage(string id, string version, string backupRoot)
        {
            if (String.IsNullOrWhiteSpace(backupRoot)) Reject("Перед обновлением плагина выберите папку резервных копий AppShelf.");
            string directory = Path.Combine(backupRoot, "BeforePluginUpdate");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, id + "-" + version + "-" + Guid.NewGuid().ToString("N") + ".asplugin");
            string temporary = Path.Combine(directory, ".stage-" + Guid.NewGuid().ToString("N") + ".asplugin");
            try
            {
                WriteInstalledPackage(id, temporary);
                ValidateArchivedPackage(temporary, id);
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal const int MaxBackupDocument = 16 * 1024 * 1024;
        private const int MaxBackupFile = 4 * 1024 * 1024;
        private const int MaxBackupTotal = 8 * 1024 * 1024;
        internal const string BackupArchiveEntry = "Plugins/backup.json";
        internal const string RecoveredRelativePath = "Plugins/recovered-backup.json";

        private static void ValidateBackupPaths(string[] paths)
        {
            if (paths == null) return;
            if (paths.Length > 64) Reject("Слишком много файлов данных плагина для копирования.");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (String.IsNullOrEmpty(path) || path.Length > 180 || path.IndexOf('\\') >= 0)
                    Reject("Недопустимый путь данных плагина.");
                foreach (string part in path.Split('/'))
                    if (!ValidSegment(part)) Reject("Недопустимый путь данных плагина: " + path);
                if (!seen.Add(path)) Reject("Повторяющийся путь данных плагина.");
            }
            foreach (string path in paths)
                foreach (string other in paths)
                    if (other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                        Reject("Конфликт путей данных плагина.");
        }

        // No links in host-owned policy/recovery locations or declared data paths.
        private static void PlainIfPresent(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    Reject("Ссылки в данных плагина не поддерживаются.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        private string PolicyPath(string id)
        {
            ValidateId(id);
            PlainIfPresent(root);
            string folder = Path.Combine(root, "BackupPolicy");
            PlainIfPresent(folder);
            string path = Child(folder, id + ".flag");
            PlainIfPresent(path);
            return path;
        }

        private static string BackupConsent(PluginManifest manifest)
        {
            string[] names = (string[])(manifest.BackupFiles ?? new string[0]).Clone();
            Array.Sort(names, StringComparer.Ordinal);
            using (SHA256 hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    manifest.Id + "\n" + manifest.Version + "\n" + String.Join("\n", names))));
        }

        private bool BackupPolicyExists(string id)
        {
            return File.Exists(PolicyPath(id));
        }

        internal bool BackupDataEnabled(string id)
        {
            lock (CommitGate)
            {
                string path = PolicyPath(id);
                if (!File.Exists(path) || new FileInfo(path).Length > 128) return false;
                PluginManifest manifest = ReadInstalled(id);
                return manifest.BackupFiles != null && manifest.BackupFiles.Length != 0 &&
                    File.ReadAllText(path) == BackupConsent(manifest);
            }
        }

        internal void SetBackupDataEnabled(string id, bool enabled)
        {
            lock (CommitGate)
            {
                string path = PolicyPath(id);
                if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
                PluginManifest manifest = ReadInstalled(id);
                if (manifest.BackupFiles == null || manifest.BackupFiles.Length == 0)
                    Reject("Плагин не объявляет файлы для резервирования.");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                byte[] bytes = Encoding.UTF8.GetBytes(BackupConsent(manifest));
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        private string DataFile(string id, string relative)
        {
            ValidateId(id);
            ValidateBackupPaths(new[] { relative });
            string folder = Child(DataRoot, id);
            PlainIfPresent(root); PlainIfPresent(DataRoot); PlainIfPresent(folder);
            string current = folder;
            foreach (string part in relative.Split('/'))
            {
                current = Child(current, part);
                PlainIfPresent(current);
            }
            return current;
        }

        private static byte[] ReadBounded(Stream input, int max)
        {
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (read > max - output.Length) Reject("Превышен лимит резервной копии плагинов.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        internal byte[] CaptureBackup()
        {
            lock (CommitGate)
            {
                if (!HasInstalledDirectory) return null;
                PluginBackupDocument document = new PluginBackupDocument
                { SchemaVersion = 1, Plugins = new List<PluginBackupItem>() };
                int total = 0;
                List<InstalledPlugin> installed = List();
                installed.Sort(delegate(InstalledPlugin a, InstalledPlugin b) { return StringComparer.Ordinal.Compare(a.Id,b.Id); });
                foreach (InstalledPlugin plugin in installed)
                {
                    if (plugin.Error != null || plugin.Manifest == null)
                    {
                        // Don't silently omit data that the user opted into.
                        if (plugin.Id != null && IdPattern.IsMatch(plugin.Id) && BackupPolicyExists(plugin.Id))
                            Reject("Нельзя сохранить данные повреждённого плагина: " + plugin.Id);
                        continue;
                    }
                    PluginManifest m = plugin.Manifest;
                    PluginBackupItem item = new PluginBackupItem { Id = m.Id, Name = m.Name,
                        Version = m.Version, Enabled = plugin.Enabled,
                        DataIncluded = BackupDataEnabled(m.Id) && m.BackupFiles != null && m.BackupFiles.Length != 0,
                        Files = new List<PluginBackupFile>() };
                    if (item.DataIncluded)
                    {
                        string[] names = (string[])m.BackupFiles.Clone();
                        Array.Sort(names, StringComparer.Ordinal);
                        foreach (string name in names)
                        {
                            string path = DataFile(m.Id, name);
                            if (Directory.Exists(path)) Reject("Ожидался файл данных плагина: " + name);
                            if (!File.Exists(path)) continue; // optional declared file
                            byte[] bytes;
                            using (FileStream input = File.OpenRead(path))
                                bytes = ReadBounded(input, Math.Min(MaxBackupFile, MaxBackupTotal - total));
                            total += bytes.Length;
                            item.Files.Add(new PluginBackupFile { Path = name, Base64 = Convert.ToBase64String(bytes) });
                        }
                    }
                    document.Plugins.Add(item);
                }
                if (document.Plugins.Count == 0) return null;
                return EncodeBackup(document);
            }
        }

        private static byte[] EncodeBackup(PluginBackupDocument document)
        {
            ValidateBackup(document);
            using (MemoryStream output = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(PluginBackupDocument)).WriteObject(output, document);
                if (output.Length > MaxBackupDocument) Reject("Слишком большой архив данных плагинов.");
                return output.ToArray();
            }
        }

        private static void ValidateBackup(PluginBackupDocument document)
        {
            if (document == null || document.SchemaVersion != 1 || document.Plugins == null ||
                document.Plugins.Count > 256) Reject("Некорректный список резервных копий плагинов.");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (PluginBackupItem item in document.Plugins)
            {
                if (item == null) Reject("Некорректная запись плагина.");
                ValidateId(item.Id);
                if (!ids.Add(item.Id) || String.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 ||
                    item.Version == null || !VersionPattern.IsMatch(item.Version) || item.Files == null ||
                    item.Files.Count > 64 || (!item.DataIncluded && item.Files.Count != 0))
                    Reject("Некорректная запись плагина в архиве.");
                List<string> paths = new List<string>();
                foreach (PluginBackupFile file in item.Files)
                {
                    if (file == null || file.Base64 == null || file.Base64.Length > ((MaxBackupFile + 2) / 3) * 4)
                        Reject("Некорректный файл данных плагина.");
                    paths.Add(file.Path);
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(file.Base64); }
                    catch (FormatException error) { throw new InvalidDataException("Некорректные данные плагина.", error); }
                    if (bytes.Length > MaxBackupFile || bytes.Length > MaxBackupTotal - total)
                        Reject("Превышен лимит данных плагинов.");
                    total += bytes.Length;
                }
                ValidateBackupPaths(paths.ToArray());
            }
        }

        internal static byte[] ReadBackupBytes(Stream input)
        {
            byte[] bytes = ReadBounded(input, MaxBackupDocument);
            DecodeBackup(bytes);
            return bytes;
        }

        private static PluginBackupDocument DecodeBackup(byte[] bytes)
        {
            PluginBackupDocument document;
            using (MemoryStream input = new MemoryStream(bytes, false))
                document = (PluginBackupDocument)new DataContractJsonSerializer(typeof(PluginBackupDocument)).ReadObject(input);
            ValidateBackup(document);
            return document;
        }

        internal string RecoveredPath()
        {
            PlainIfPresent(root);
            string path = Path.Combine(root, "recovered-backup.json");
            PlainIfPresent(path);
            return path;
        }

        internal List<PluginBackupItem> ReadRecovered()
        {
            lock (CommitGate)
            {
                string path = RecoveredPath();
                if (!File.Exists(path)) return new List<PluginBackupItem>();
                using (FileStream input = File.OpenRead(path))
                    return DecodeBackup(ReadBounded(input, MaxBackupDocument)).Plugins;
            }
        }

        internal void ApplyRecoveredData(string id, string version, string backupRoot)
        {
            lock (CommitGate)
            {
                PluginManifest manifest = ReadInstalled(id);
                if (manifest.Version != version) Reject("Для восстановления нужна та же версия плагина.");
                if (!Approved(Child(InstalledRoot, id), manifest)) Reject("Плагин не подтверждён.");
                PluginBackupItem item = ReadRecovered().Find(delegate(PluginBackupItem x) { return x.Id == id; });
                if (item == null || item.Version != version || !item.DataIncluded || item.Files.Count == 0)
                    Reject("Нет подходящих сохранённых данных плагина.");
                HashSet<string> allowed = new HashSet<string>(manifest.BackupFiles ?? new string[0], StringComparer.Ordinal);
                foreach (PluginBackupFile file in item.Files)
                    if (!allowed.Contains(file.Path)) Reject("Плагин не разрешает восстановление файла: " + file.Path);
                if (String.IsNullOrWhiteSpace(backupRoot)) Reject("Сначала выберите папку резервных копий AppShelf.");
                // Keep the plugin disabled even if rollback fails; never automatically
                // run code against a partly restored data set after restart.
                SetEnabled(id, false);
                string safety = Path.Combine(backupRoot, "BeforeRestore", "plugin-" + id + "-" + Guid.NewGuid().ToString("N"));
                // Save only affected existing files; never copy or delete other plugin data.
                List<string> touched = new List<string>();
                HashSet<string> existed = new HashSet<string>(StringComparer.Ordinal);
                foreach (PluginBackupFile file in item.Files)
                {
                    string active = DataFile(id, file.Path);
                    if (Directory.Exists(active)) Reject("Конфликт файла данных: " + file.Path);
                    if (File.Exists(active))
                    {
                        string old = Child(safety, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(old));
                        File.Copy(active, old);
                        existed.Add(file.Path);
                    }
                }
                try
                {
                    foreach (PluginBackupFile file in item.Files)
                    {
                        string active = DataFile(id, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(active));
                        string temp = active + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(file.Base64);
                            using (FileStream output = new FileStream(temp, FileMode.CreateNew))
                            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                            if (existed.Contains(file.Path)) File.Replace(temp, active, null);
                            else File.Move(temp, active);
                            touched.Add(file.Path);
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                    }
                }
                catch (Exception error)
                {
                    List<Exception> rollback = new List<Exception>();
                    foreach (string name in touched)
                    {
                        try
                        {
                            string active = DataFile(id, name);
                            if (existed.Contains(name)) File.Copy(Child(safety, name), active, true);
                            else File.Delete(active);
                        }
                        catch (Exception failure) { rollback.Add(failure); }
                    }
                    if (rollback.Count != 0)
                        throw new IOException("Не удалось полностью отменить восстановление. Предыдущие файлы: " + safety,
                            new AggregateException(rollback));
                    throw new IOException("Восстановление данных отменено. Предыдущие файлы: " + safety, error);
                }
            }
        }

        private static bool Approved(string directory, PluginManifest manifest)
        {
            string file = Path.Combine(directory, "approved.flag");
            return File.Exists(file) && new FileInfo(file).Length <= 32 &&
                File.ReadAllText(file) == manifest.Version;
        }

        internal List<InstalledPlugin> List()
        {
            EnsureInstalledRoot();
            lock (CommitGate)
            {
            List<InstalledPlugin> result = new List<InstalledPlugin>();
            HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(InstalledRoot, ".update-*.json"))
            {
                string name = Path.GetFileName(file);
                string id = name.Substring(8, name.Length - 13);
                try { RecoverOrThrow(id); }
                catch (Exception error)
                {
                    pending.Add(id);
                    result.Add(new InstalledPlugin { Id = id, Error = error.Message });
                }
            }
            foreach (string directory in Directory.GetDirectories(InstalledRoot))
            {
                string id = Path.GetFileName(directory);
                if (id.StartsWith(".stage-", StringComparison.Ordinal) ||
                    id.StartsWith(".previous-", StringComparison.Ordinal) || pending.Contains(id)) continue;
                InstalledPlugin row = new InstalledPlugin { Id = id };
                try
                {
                    row.Manifest = ReadInstalled(id);
                    if (!Approved(directory, row.Manifest))
                        Reject("Нет подтверждения доверия для этой версии.");
                    string flag = Path.Combine(directory, "enabled.flag");
                    row.Enabled = File.Exists(flag) && new FileInfo(flag).Length == 0;
                }
                catch (Exception error) { row.Error = error.Message; row.Enabled = false; }
                result.Add(row);
            }
            result.Sort(delegate(InstalledPlugin a, InstalledPlugin b)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(
                    a.Manifest == null ? a.Id : a.Manifest.Name,
                    b.Manifest == null ? b.Id : b.Manifest.Name);
            });
            return result;
            }
        }

        internal void SetEnabled(string id, bool enabled)
        {
            lock (CommitGate)
            {
            PluginManifest manifest = ReadInstalled(id);
            string directory = Child(InstalledRoot, id);
            string flag = Path.Combine(directory, "enabled.flag");
            if (enabled)
            {
                if (!Approved(directory, manifest)) Reject("Нет подтверждения доверия для этой версии.");
                using (FileStream output = new FileStream(flag, FileMode.Create, FileAccess.Write, FileShare.None))
                    output.Flush(true);
            }
            else File.Delete(flag);
            }
        }

        // Uninstall package code only. Data deletion requires a separate explicit action.
        internal void Remove(string id)
        {
            ValidateId(id);
            EnsureInstalledRoot();
            lock (CommitGate)
            {
                RecoverOrThrow(id);
                string path = Child(InstalledRoot, id);
                CheckPlainTree(path);
                Directory.Delete(path, true);
                // Consent is not inherited by a later reinstall.
                SetBackupDataEnabled(id, false);
            }
        }
    }
}
