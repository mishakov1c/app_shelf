// Update from official AppShelf releases. The source repository must be public
// before anonymous GitHub requests will work.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace AppShelf
{
    internal static class AppUpdate
    {
        internal const string Owner = "mishakov1c";
        internal const string Repository = "app_shelf";
        private const long MaximumDownloadBytes = 200L * 1024 * 1024;
        private static readonly string StagingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppShelf", "Updates");

        [DataContract]
        private sealed class GithubRelease
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "body")] public string Body;
            [DataMember(Name = "draft")] public bool Draft;
            [DataMember(Name = "prerelease")] public bool Prerelease;
            [DataMember(Name = "assets")] public List<GithubAsset> Assets;
        }

        [DataContract]
        private sealed class GithubAsset
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "size")] public long Size;
            [DataMember(Name = "digest")] public string Digest;
            [DataMember(Name = "browser_download_url")] public string DownloadUrl;
        }

        internal sealed class Release
        {
            internal Version Version;
            internal string VersionText;
            internal string Notes;
            internal string DownloadUrl;
            internal string Sha256;
            internal long Size;
        }

        internal sealed class PreparedUpdate
        {
            internal string Directory;
            internal string Payload;
            internal string Sha256;
            internal string Version;
        }

        internal static Version ParseVersion(string text)
        {
            string value = (text ?? "").Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);
            if (!Regex.IsMatch(value, @"\A[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\z"))
                throw new InvalidDataException("Некорректный номер версии релиза.");
            Version version;
            if (!Version.TryParse(value, out version) || version.Major < 0 ||
                version.Minor < 0 || version.Build < 0)
                throw new InvalidDataException("Некорректный номер версии релиза.");
            return version;
        }

        internal static Version InstalledVersion()
        {
            Version value = typeof(AppUpdate).Assembly.GetName().Version;
            return new Version(value.Major, value.Minor, value.Build);
        }

        internal static Release Check()
        {
            string address = "https://api.github.com/repos/" + Owner + "/" +
                Repository + "/releases/latest";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.Method = "GET";
            request.UserAgent = "AppShelf-update-check";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            GithubRelease release;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] block = new byte[8192];
                int count;
                while ((count = input.Read(block, 0, block.Length)) != 0)
                {
                    if (buffer.Length + count > 1024 * 1024)
                        throw new InvalidDataException("Слишком большой ответ GitHub.");
                    buffer.Write(block, 0, count);
                }
                buffer.Position = 0;
                release = (GithubRelease)new DataContractJsonSerializer(
                    typeof(GithubRelease)).ReadObject(buffer);
            }
            if (release == null || release.Draft || release.Prerelease)
                throw new InvalidDataException("Нет опубликованного стабильного релиза.");

            Version version = ParseVersion(release.Tag);
            GithubAsset asset = null;
            if (release.Assets != null)
                foreach (GithubAsset item in release.Assets)
                    if (item != null && item.Name == "AppShelf.exe")
                    {
                        asset = item;
                        break;
                    }
            if (asset == null || asset.Size < 10000 || asset.Size > MaximumDownloadBytes)
                throw new InvalidDataException(
                    "Релиз должен содержать AppShelf.exe допустимого размера.");

            string hash = ExtractSha256(asset.Digest);
            if (hash == null)
                throw new InvalidDataException(
                    "GitHub не предоставил SHA-256 для AppShelf.exe. Установка запрещена.");

            Uri download;
            if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out download) ||
                download.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(download.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !download.AbsolutePath.StartsWith("/" + Owner + "/" + Repository +
                    "/releases/download/", StringComparison.OrdinalIgnoreCase) ||
                !download.AbsolutePath.EndsWith("/AppShelf.exe", StringComparison.Ordinal))
                throw new InvalidDataException("GitHub вернул неожиданный адрес EXE.");

            return new Release
            {
                Version = version,
                VersionText = version.ToString(3),
                Notes = String.IsNullOrWhiteSpace(release.Body)
                    ? "Описание отсутствует." : release.Body,
                DownloadUrl = asset.DownloadUrl,
                Sha256 = hash,
                Size = asset.Size
            };
        }

        internal static string ExtractSha256(string digest)
        {
            if (String.IsNullOrWhiteSpace(digest) ||
                !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
                return null;
            return digest.Substring(7).ToLowerInvariant();
        }

        private static string FileHash(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream input = File.OpenRead(file))
            {
                byte[] value = sha.ComputeHash(input);
                return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static PreparedUpdate Download(Release release, Action<long, long> progress)
        {
            if (release == null || release.Size < 10000 ||
                release.Size > MaximumDownloadBytes ||
                ExtractSha256("sha256:" + release.Sha256) == null)
                throw new InvalidOperationException("Нет проверенных данных о релизе.");

            Directory.CreateDirectory(StagingRoot);
            string folder = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, "AppShelf.exe.part");
            string payload = Path.Combine(folder, "AppShelf.exe");
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(release.DownloadUrl);
                request.UserAgent = "AppShelf-update-download";
                request.Timeout = 25000;
                request.ReadWriteTimeout = 25000;
                request.AllowAutoRedirect = true;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream source = response.GetResponseStream())
                using (FileStream output = new FileStream(temporary,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (response.ResponseUri == null ||
                        response.ResponseUri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidDataException("Небезопасная ссылка на файл обновления.");
                    if (response.ContentLength > release.Size)
                        throw new InvalidDataException("Размер EXE отличается от данных релиза.");
                    long received = 0;
                    byte[] bytes = new byte[65536];
                    int count;
                    while ((count = source.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        received += count;
                        if (received > release.Size || received > MaximumDownloadBytes)
                            throw new InvalidDataException("Файл обновления больше заявленного.");
                        output.Write(bytes, 0, count);
                        if (progress != null) progress(received, release.Size);
                    }
                    output.Flush(true);
                    if (received != release.Size)
                        throw new InvalidDataException("Файл обновления загружен не полностью.");
                }
                if (!String.Equals(FileHash(temporary), release.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 файла обновления не совпадает.");

                // The standard AppShelf release is a .NET Framework executable.
                // Reject unrelated programs even if they are named AppShelf.exe.
                AssemblyName identity = AssemblyName.GetAssemblyName(temporary);
                if (!String.Equals(identity.Name, "AppShelf",
                    StringComparison.OrdinalIgnoreCase) ||
                    !new Version(identity.Version.Major, identity.Version.Minor,
                        identity.Version.Build).Equals(release.Version))
                    throw new InvalidDataException(
                        "Файл обновления не соответствует версии AppShelf.");
                File.Move(temporary, payload);
                return new PreparedUpdate
                {
                    Directory = folder,
                    Payload = payload,
                    Sha256 = release.Sha256,
                    Version = release.VersionText
                };
            }
            catch
            {
                try { Directory.Delete(folder, true); }
                catch (Exception error) { ConfigStore.Log(error); }
                throw;
            }
        }

        private static string Quote(string argument)
        {
            if (argument.IndexOf('"') >= 0)
                throw new InvalidOperationException("Недопустимый путь обновления.");
            return "\"" + argument + "\"";
        }

        internal static void StartInstaller(PreparedUpdate update)
        {
            if (update == null) throw new ArgumentNullException("update");
            string target = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            if (!String.Equals(Path.GetFileName(target), "AppShelf.exe",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Не удалось определить исходный AppShelf.exe.");
            if (!String.Equals(FileHash(update.Payload), update.Sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл обновления был изменён после загрузки.");

            string helper = Path.Combine(update.Directory, "AppShelfUpdater.exe");
            File.Copy(target, helper, false);
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                WorkingDirectory = update.Directory,
                Arguments = "--apply-update " +
                    Process.GetCurrentProcess().Id + " " + Quote(target) + " " +
                    Quote(update.Payload) + " " + update.Sha256
            };
            Process.Start(psi);
        }

        private static bool WithinStaging(string path)
        {
            string prefix = Path.GetFullPath(StagingRoot).TrimEnd(
                Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        // This mode runs in the copied *old* EXE, outside the application's
        // single-instance mutex, after the main application exits.
        internal static void Apply(string[] args)
        {
            if (args == null || args.Length != 5 || args[0] != "--apply-update")
                throw new InvalidOperationException("Неверные параметры установщика.");
            int parentId;
            if (!Int32.TryParse(args[1], out parentId) || parentId <= 0)
                throw new InvalidOperationException("Неверный идентификатор процесса.");
            string target = Path.GetFullPath(args[2]);
            string payload = Path.GetFullPath(args[3]);
            string expectedHash = args[4];
            if (!WithinStaging(payload) || !WithinStaging(
                    Process.GetCurrentProcess().MainModule.FileName) ||
                !String.Equals(Path.GetFileName(target), "AppShelf.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetFileName(payload), "AppShelf.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(payload),
                    Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName),
                    StringComparison.OrdinalIgnoreCase) ||
                ExtractSha256("sha256:" + expectedHash) == null ||
                !File.Exists(target) || !File.Exists(payload))
                throw new InvalidOperationException("Некорректные пути или хеш обновления.");

            try
            {
                using (Process previous = Process.GetProcessById(parentId))
                    if (!previous.WaitForExit(60000))
                        throw new IOException("AppShelf не закрылся за 60 секунд.");
            }
            catch (ArgumentException) { } // The old process already exited.

            if (!String.Equals(FileHash(payload), expectedHash,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проверка SHA-256 перед установкой не пройдена.");

            string candidate = target + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = target + ".previous";
            bool replaced = false;
            try
            {
                // Temp copy and destination live on the same volume for File.Replace.
                File.Copy(payload, candidate, false);
                if (!String.Equals(FileHash(candidate), expectedHash,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Файл изменился во время копирования.");
                File.Replace(candidate, target, backup, true);
                replaced = true;
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    WorkingDirectory = Path.GetDirectoryName(target),
                    UseShellExecute = true
                });
                // The .previous EXE is intentionally retained for manual rollback.
            }
            catch
            {
                if (!replaced)
                {
                    // On permissions/IO failure the original EXE is still intact.
                    // Restart it so the user is not left with a closed application.
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = target,
                            WorkingDirectory = Path.GetDirectoryName(target),
                            UseShellExecute = true
                        });
                    }
                    catch (Exception error) { ConfigStore.Log(error); }
                }
                if (replaced && File.Exists(backup))
                {
                    string rollback = target + ".rollback-" +
                        Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.Copy(backup, rollback, false);
                        File.Replace(rollback, target, null, true);
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = target,
                            WorkingDirectory = Path.GetDirectoryName(target),
                            UseShellExecute = true
                        });
                    }
                    catch (Exception error) { ConfigStore.Log(error); }
                    finally
                    {
                        try { if (File.Exists(rollback)) File.Delete(rollback); }
                        catch { }
                    }
                }
                throw;
            }
            finally
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); }
                catch { }
            }
        }
    }
}
