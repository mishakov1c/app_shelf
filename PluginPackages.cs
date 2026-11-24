// AppShelf plugin package storage. No code is loaded or executed by this layer.
// Compatible with C# 5 / .NET Framework 4.8 and .NET 8.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace AppShelf
{
    [DataContract]
    internal sealed class PluginManifest
    {
        [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion;
        [DataMember(Name = "apiVersion", IsRequired = true)] public int ApiVersion;
        [DataMember(Name = "id", IsRequired = true)] public string Id;
        [DataMember(Name = "name", IsRequired = true)] public string Name;
        [DataMember(Name = "version", IsRequired = true)] public string Version;
        [DataMember(Name = "entryPoint", IsRequired = true)] public string EntryPoint;
        [DataMember(Name = "description", EmitDefaultValue = false)] public string Description;
        [DataMember(Name = "publisher", EmitDefaultValue = false)] public string Publisher;
        [DataMember(Name = "capabilities", IsRequired = true)] public string[] Capabilities;
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
                            total += copied;
                        }
                    }
                }
            }
        }

        // Stage and validate before replacing the installed code. Runtime must stop the
        // old process before calling this method; data lives in a separate directory.
        internal PluginManifest Install(string package, Func<PluginManifest, bool> confirm, string expectedId = null)
        {
            if (confirm == null) throw new ArgumentNullException("confirm");
            EnsureInstalledRoot();
            string stage = Path.Combine(InstalledRoot, ".stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                ExtractPackage(package, stage);
                PluginManifest manifest = ReadManifest(stage);
                if (expectedId != null && manifest.Id != expectedId)
                    Reject("Пакет относится к другому плагину.");
                string target = Child(InstalledRoot, manifest.Id);
                if (Directory.Exists(target))
                {
                    CheckPlainTree(target);
                    PluginManifest previous = ReadManifest(target);
                    if (previous.Id != manifest.Id) Reject("Идентификатор установленного плагина не совпадает.");
                    if (new Version(manifest.Version).CompareTo(new Version(previous.Version)) <= 0)
                        Reject("Для обновления требуется более новая версия плагина.");
                    if (!confirm(manifest)) return null;
                    File.WriteAllText(Path.Combine(stage, "approved.flag"), manifest.Version);
                    string old = Path.Combine(InstalledRoot, ".previous-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(target, old);
                    try { Directory.Move(stage, target); }
                    catch
                    {
                        // Never silently discard the previous package if rollback itself fails.
                        if (!Directory.Exists(target)) Directory.Move(old, target);
                        throw;
                    }
                    TryRemoveStage(old);
                }
                else
                {
                    if (!confirm(manifest)) return null;
                    File.WriteAllText(Path.Combine(stage, "approved.flag"), manifest.Version);
                    Directory.Move(stage, target);
                }
                return manifest;
            }
            finally { TryRemoveStage(stage); }
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
            string path = Child(InstalledRoot, id);
            CheckPlainTree(path);
            PluginManifest manifest = ReadManifest(path);
            if (manifest.Id != id) Reject("Идентификатор установленного плагина не совпадает.");
            return manifest;
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
            List<InstalledPlugin> result = new List<InstalledPlugin>();
            foreach (string directory in Directory.GetDirectories(InstalledRoot))
            {
                string id = Path.GetFileName(directory);
                if (id.StartsWith(".stage-", StringComparison.Ordinal) ||
                    id.StartsWith(".previous-", StringComparison.Ordinal)) continue;
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

        internal void SetEnabled(string id, bool enabled)
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

        // Uninstall package code only. Data deletion requires a separate explicit action.
        internal void Remove(string id)
        {
            ValidateId(id);
            EnsureInstalledRoot();
            string path = Child(InstalledRoot, id);
            CheckPlainTree(path);
            Directory.Delete(path, true);
        }
    }
}
