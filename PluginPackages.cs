// AppShelf plugin package storage. No code is loaded or executed by this layer.
// Compatible with C# 5 / .NET Framework 4.8 and .NET 8.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
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
    }

    internal sealed class InstalledPlugin
    {
        internal string Id;
        internal PluginManifest Manifest;
        internal bool Enabled;
        internal string Error;
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
            new[] { "command", "window", "background", "settings", "notification" },
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
            Func<string, IDisposable> beforeCommit = null)
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
            }
        }
    }
}
