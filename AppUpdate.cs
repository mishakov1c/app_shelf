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

        private static readonly string CacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppShelf", "UpdateCache");
        private static readonly object CacheGate = new object();

        [DataContract]
        private sealed class CachedFeed
        {
            [DataMember] public long CheckedUtcTicks;
            [DataMember] public string Json;
        }

        internal sealed class CheckResult
        {
            internal Release[] Releases;
            internal DateTime? CheckedUtc;
            internal DateTime? RetryUtc;
            internal bool FromCache;
        }

        internal sealed class GithubRateLimitException : IOException
        {
            internal readonly DateTime RetryUtc;
            internal GithubRateLimitException(DateTime retryUtc)
                : base("GitHub временно ограничил запросы.") { RetryUtc = retryUtc; }
        }

        // DataContractJsonSerializer assigns these fields via reflection.
        // The test harness treats compiler warnings as errors (CS0649).
#pragma warning disable 0649
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
#pragma warning restore 0649

        internal sealed class Release
        {
            internal Version Version;
            internal string VersionText;
            internal bool IsBeta;
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

        // Keep the numeric assembly version separate from the semantic release tag.
        // Stable wins over all beta builds with the same numeric version.
        internal static string NormalizeReleaseVersion(string text)
        {
            string value = (text ?? "").Trim();
            Match match = Regex.Match(value,
                @"\A[vV]?((?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4}))(?:-beta\.([1-9][0-9]{0,8}))?\z");
            if (!match.Success)
                throw new InvalidDataException("Некорректный номер версии релиза.");
            Version core = ParseVersion(match.Groups[1].Value);
            if (core.Major > 65534 || core.Minor > 65534 || core.Build > 65534)
                throw new InvalidDataException("Некорректный номер версии релиза.");
            return match.Groups[1].Value + (match.Groups[2].Success
                ? "-beta." + match.Groups[2].Value : "");
        }

        private static Version CoreVersion(string normalized)
        {
            return ParseVersion(normalized.Split('-')[0]);
        }

        internal static int CompareReleaseVersions(string first, string second)
        {
            string a = NormalizeReleaseVersion(first);
            string b = NormalizeReleaseVersion(second);
            int core = CoreVersion(a).CompareTo(CoreVersion(b));
            if (core != 0) return core;
            int ai = a.IndexOf("-beta.", StringComparison.Ordinal);
            int bi = b.IndexOf("-beta.", StringComparison.Ordinal);
            int an = ai < 0 ? Int32.MaxValue : Int32.Parse(a.Substring(ai + 6),
                System.Globalization.CultureInfo.InvariantCulture);
            int bn = bi < 0 ? Int32.MaxValue : Int32.Parse(b.Substring(bi + 6),
                System.Globalization.CultureInfo.InvariantCulture);
            return an.CompareTo(bn);
        }

        internal static string InstalledVersionText()
        {
            Assembly assembly = typeof(AppUpdate).Assembly;
            AssemblyInformationalVersionAttribute info =
                (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    assembly, typeof(AssemblyInformationalVersionAttribute));
            if (info != null)
            {
                // Old development/test builds may use a different informational suffix.
                // Published beta metadata must match the numeric assembly version.
                string value = info.InformationalVersion;
                if (value != null && value.IndexOf("-beta.", StringComparison.Ordinal) >= 0)
                {
                    string normalized = NormalizeReleaseVersion(value);
                    if (!CoreVersion(normalized).Equals(InstalledVersion()))
                        throw new InvalidDataException("Метаданные бета-версии не совпадают.");
                    return normalized;
                }
            }
            return InstalledVersion().ToString(3);
        }

        internal static DateTime? RateLimitUntil(int status, string remaining,
            string reset, string retryAfter, DateTime now)
        {
            if (status != 403 && status != 429) return null;
            long seconds;
            DateTime retry;
            DateTime? until = null;
            if (Int64.TryParse(retryAfter, out seconds) && seconds >= 0 && seconds <= 604800)
                until = now.AddSeconds(seconds);
            else if (DateTime.TryParse(retryAfter, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out retry))
                until = retry.ToUniversalTime();
            if (remaining == "0" && Int64.TryParse(reset, out seconds) &&
                seconds >= 0 && seconds <= 253402300799L)
            {
                retry = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
                if (!until.HasValue || retry > until.Value) until = retry;
            }
            if (status == 429 || remaining == "0" || !String.IsNullOrWhiteSpace(retryAfter))
                return until.HasValue && until.Value > now ? until.Value : now.AddMinutes(1);
            return null; // Not every 403 is a rate limit.
        }

        private static T ReadGithub<T>(string address)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.Method = "GET";
            request.UserAgent = "AppShelf-update-check";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream input = response.GetResponseStream())
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] block = new byte[8192];
                    int count;
                    while ((count = input.Read(block, 0, block.Length)) != 0)
                    {
                        if (buffer.Length + count > 8 * 1024 * 1024)
                            throw new InvalidDataException("Слишком большой ответ GitHub.");
                        buffer.Write(block, 0, count);
                    }
                    buffer.Position = 0;
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(buffer);
                }
            }
            catch (WebException error)
            {
                using (HttpWebResponse response = error.Response as HttpWebResponse)
                {
                    if (response == null) throw;
                    int status = (int)response.StatusCode;
                    DateTime? retry = RateLimitUntil(status,
                        response.Headers["X-RateLimit-Remaining"],
                        response.Headers["X-RateLimit-Reset"],
                        response.Headers["Retry-After"], DateTime.UtcNow);
                    if (retry.HasValue) throw new GithubRateLimitException(retry.Value);
                    if (status == 403)
                        throw new IOException("GitHub отказал в доступе (403). Проверь сеть, VPN и доступ к GitHub.", error);
                    throw;
                }
            }
        }

        private static string ReadReleaseFeed(bool includeBeta)
        {
            string address = "https://api.github.com/repos/" + Owner + "/" +
                Repository + "/releases";
            List<GithubRelease> releases = new List<GithubRelease>();
            if (!includeBeta)
                releases.Add(ReadGithub<GithubRelease>(address + "/latest"));
            else
            {
                bool complete = false;
                for (int page = 1; page <= 10; page++)
                {
                    List<GithubRelease> part = ReadGithub<List<GithubRelease>>(
                        address + "?per_page=100&page=" + page);
                    if (part == null) throw new InvalidDataException("Некорректный ответ GitHub.");
                    releases.AddRange(part);
                    if (part.Count < 100) { complete = true; break; }
                }
                if (!complete) throw new InvalidDataException("Слишком много релизов GitHub.");
            }
            using (MemoryStream buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(List<GithubRelease>)).WriteObject(buffer, releases);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static CachedFeed LoadCache(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length > 32 * 1024 * 1024) return null;
                using (FileStream input = File.OpenRead(path))
                    return (CachedFeed)new DataContractJsonSerializer(typeof(CachedFeed)).ReadObject(input);
            }
            catch (Exception error) { ConfigStore.Log(error); return null; }
        }

        private static void SaveCache(string path, CachedFeed value)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    new DataContractJsonSerializer(typeof(CachedFeed)).WriteObject(output, value);
                    output.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception error) { ConfigStore.Log(error); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static DateTime? CacheTime(CachedFeed entry)
        {
            if (entry == null || entry.CheckedUtcTicks <= 0 ||
                entry.CheckedUtcTicks > DateTime.MaxValue.Ticks) return null;
            return new DateTime(entry.CheckedUtcTicks, DateTimeKind.Utc);
        }

        internal static CheckResult CheckWithCache(bool includeBeta, string root,
            Func<bool, string> fetch, DateTime now)
        {
            lock (CacheGate)
            {
                string channel = Path.Combine(root, includeBeta ? "beta.json" : "stable.json");
                string cooldown = Path.Combine(root, "cooldown.json");
                CachedFeed saved = LoadCache(channel);
                DateTime? checkedUtc = CacheTime(saved);
                Release[] cached = null;
                if (checkedUtc.HasValue && checkedUtc.Value <= now)
                {
                    try { cached = SelectAvailableFromJson(saved.Json, includeBeta); }
                    catch (Exception error) { ConfigStore.Log(error); checkedUtc = null; }
                }
                else checkedUtc = null;
                DateTime? retry = CacheTime(LoadCache(cooldown));
                if (retry.HasValue && retry.Value > now)
                    return new CheckResult { Releases = cached, CheckedUtc = checkedUtc,
                        RetryUtc = retry, FromCache = true };
                if (cached != null && checkedUtc.HasValue &&
                    now - checkedUtc.Value < TimeSpan.FromSeconds(60))
                    return new CheckResult { Releases = cached, CheckedUtc = checkedUtc,
                        FromCache = true };
                try
                {
                    string json = fetch(includeBeta);
                    Release[] fresh = SelectAvailableFromJson(json, includeBeta);
                    SaveCache(channel, new CachedFeed { CheckedUtcTicks = now.Ticks, Json = json });
                    return new CheckResult { Releases = fresh, CheckedUtc = now };
                }
                catch (GithubRateLimitException error)
                {
                    SaveCache(cooldown, new CachedFeed { CheckedUtcTicks = error.RetryUtc.Ticks });
                    return new CheckResult { Releases = cached, CheckedUtc = checkedUtc,
                        RetryUtc = error.RetryUtc, FromCache = true };
                }
            }
        }

        internal static CheckResult CheckWithDetails(bool includeBeta)
        {
            return CheckWithCache(includeBeta, CacheRoot, ReadReleaseFeed, DateTime.UtcNow);
        }

        internal static Release Check()
        {
            return CheckChannel(false);
        }

        internal static Release CheckChannel(bool includeBeta)
        {
            return CheckAvailable(includeBeta)[0];
        }

        internal static Release[] CheckAvailable(bool includeBeta)
        {
            CheckResult result = CheckWithDetails(includeBeta);
            if (result.RetryUtc.HasValue) throw new GithubRateLimitException(result.RetryUtc.Value);
            return result.Releases;
        }

        internal static Release SelectReleaseFromJson(string json, bool includeBeta)
        {
            using (MemoryStream buffer = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return SelectRelease((List<GithubRelease>)new DataContractJsonSerializer(
                    typeof(List<GithubRelease>)).ReadObject(buffer), includeBeta);
        }

        private static Release[] SelectAvailable(List<GithubRelease> releases, bool includeBeta)
        {
            Release newest = SelectRelease(releases, includeBeta);
            if (!newest.IsBeta) return new Release[] { newest };
            // When a newer beta exists, a stable version must still be selectable.
            Release stable = SelectRelease(releases, false);
            return new Release[] { newest, stable };
        }

        internal static Release[] SelectAvailableFromJson(string json, bool includeBeta)
        {
            using (MemoryStream buffer = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return SelectAvailable((List<GithubRelease>)new DataContractJsonSerializer(
                    typeof(List<GithubRelease>)).ReadObject(buffer), includeBeta);
        }

        private static Release SelectRelease(List<GithubRelease> releases, bool includeBeta)
        {
            GithubRelease release = null;
            string versionText = null;
            if (releases != null)
                foreach (GithubRelease candidate in releases)
                {
                    if (candidate == null || candidate.Draft ||
                        (candidate.Prerelease && !includeBeta)) continue;
                    string tag;
                    try { tag = NormalizeReleaseVersion(candidate.Tag); }
                    catch (InvalidDataException) { continue; }
                    bool beta = tag.IndexOf("-beta.", StringComparison.Ordinal) >= 0;
                    if (beta != candidate.Prerelease) continue;
                    if (release == null || CompareReleaseVersions(tag, versionText) > 0)
                    {
                        release = candidate;
                        versionText = tag;
                    }
                }
            if (release == null)
                throw new InvalidDataException("Нет подходящего опубликованного релиза.");
            Version version = CoreVersion(versionText);
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
                !String.Equals(download.AbsolutePath, "/" + Owner + "/" + Repository +
                    "/releases/download/" + release.Tag.Trim() + "/AppShelf.exe", StringComparison.Ordinal) ||
                !String.IsNullOrEmpty(download.Query) || !String.IsNullOrEmpty(download.Fragment))
                throw new InvalidDataException("GitHub вернул неожиданный адрес EXE.");

            return new Release
            {
                Version = version,
                VersionText = versionText,
                IsBeta = release.Prerelease,
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
                        identity.Version.Build).Equals(release.Version) ||
                    !String.Equals(FileVersionInfo.GetVersionInfo(temporary).ProductVersion,
                        release.VersionText, StringComparison.Ordinal))
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

        internal static void ValidateInstalledTarget(string target, string expectedHash)
        {
            if (!String.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) ||
                WithinStaging(target) || !File.Exists(target) ||
                !String.Equals(AssemblyName.GetAssemblyName(target).Name, "AppShelf",
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(FileHash(target), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Исходный EXE не соответствует запущенному AppShelf.");
        }

        internal static void StartInstaller(PreparedUpdate update)
        {
            if (update == null) throw new ArgumentNullException("update");
            string target = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            ValidateInstalledTarget(target, FileHash(typeof(AppUpdate).Assembly.Location));
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

        internal static void Discard(PreparedUpdate update)
        {
            if (update == null || String.IsNullOrWhiteSpace(update.Directory)) return;
            try
            {
                string folder = Path.GetFullPath(update.Directory);
                if (!WithinStaging(folder) ||
                    !String.Equals(Path.GetDirectoryName(folder),
                        Path.GetFullPath(StagingRoot).TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                    return;
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception error) { ConfigStore.Log(error); }
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
                !String.Equals(Path.GetExtension(target), ".exe",
                    StringComparison.OrdinalIgnoreCase) || WithinStaging(target) ||
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
                {
                    if (!previous.HasExited && !String.Equals(
                        Path.GetFullPath(previous.MainModule.FileName), target,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Установщик запущен не для исходного процесса AppShelf.");
                    if (!previous.WaitForExit(60000))
                        throw new IOException("AppShelf не закрылся за 60 секунд.");
                }
            }
            catch (ArgumentException) { } // The old process already exited.

            if (!String.Equals(FileHash(payload), expectedHash,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проверка SHA-256 перед установкой не пройдена.");

            string candidate = target + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
            // The old EXE is already backed up under the user-selected root.
            // The running helper in staging is also an emergency local rollback source.
            // Do not leave .previous beside the application after every update.
            string backup = Process.GetCurrentProcess().MainModule.FileName;
            ValidateInstalledTarget(target, FileHash(backup));
            bool replaced = false;
            try
            {
                // Temp copy and destination live on the same volume for File.Replace.
                File.Copy(payload, candidate, false);
                if (!String.Equals(FileHash(candidate), expectedHash,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Файл изменился во время копирования.");
                File.Replace(candidate, target, null, true);
                replaced = true;
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    WorkingDirectory = Path.GetDirectoryName(target),
                    UseShellExecute = true
                });
                // Persistent data and old EXE are stored together in BeforeUpdate.
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
