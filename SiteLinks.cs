// Sites reuse AppShelf-owned .url shortcuts so launch, groups and ZIP backup stay compatible.
// Favicon files are disposable local cache data and are intentionally excluded from backups.
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShelf
{
    public static class SiteLinks
    {
        public static readonly string IconsDirectory = Path.Combine(ConfigStore.DirectoryPath, "SiteIcons");

        public static bool TryNormalize(string value, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(value)) return false;
            string text = value.Trim();
            if (text.Length > 4096 || text.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0) return false;
            // Allow a user to enter github.com without a scheme, not an arbitrary URI scheme.
            if (text.IndexOf("://", StringComparison.Ordinal) < 0 &&
                text.IndexOf(':') < 0) text = "https://" + text;
            Uri parsed;
            if (!Uri.TryCreate(text, UriKind.Absolute, out parsed)) return false;
            if ((parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                String.IsNullOrWhiteSpace(parsed.Host) || parsed.UserInfo.Length != 0 ||
                parsed.AbsoluteUri.Length > 4096) return false;
            uri = parsed;
            return true;
        }

        public static string SuggestedName(Uri uri)
        {
            string host = uri.Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? host.Substring(4) : host;
        }

        public static string CreateShortcut(Uri uri)
        {
            Directory.CreateDirectory(ConfigStore.ShortcutsDirectory);
            string filename = Path.Combine(ConfigStore.ShortcutsDirectory,
                Guid.NewGuid().ToString("N") + ".url");
            // URI.AbsoluteUri escapes non-ASCII characters and never contains CR/LF.
            string contents = "[InternetShortcut]\r\nURL=" + uri.AbsoluteUri + "\r\n";
            using (FileStream stream = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, Encoding.ASCII))
                writer.Write(contents);
            return filename;
        }

        public static Uri ReadShortcutUrl(string filename)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(filename) ||
                    !filename.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(filename) || new FileInfo(filename).Length > 16384) return null;
                using (StreamReader input = new StreamReader(filename, Encoding.Default, true))
                {
                    string line;
                    while ((line = input.ReadLine()) != null)
                    {
                        if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                        Uri parsed;
                        return TryNormalize(line.Substring(4), out parsed) ? parsed : null;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        public static string IconPath(Uri uri)
        {
            string origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            using (SHA256 hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(origin)))
                    .Replace("-", "").ToLowerInvariant();
                return Path.Combine(IconsDirectory, key + ".ico");
            }
        }

        public static ImageSource LoadCachedIcon(Uri uri)
        {
            string filename = IconPath(uri);
            if (!File.Exists(filename)) return null;
            try
            {
                byte[] data = File.ReadAllBytes(filename);
                if (data.Length == 0 || data.Length > 262144) throw new InvalidDataException("Invalid cached favicon.");
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(data);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // A damaged icon is only cache data, never an owned .url shortcut.
                try { File.Delete(filename); } catch (Exception) { }
                return null;
            }
        }

        // A favicon may be a PNG or ICO file, and frequently is not at /favicon.ico.
        // Fetch and validate image bytes off the UI thread; cache only valid images.
        private static byte[] FetchBytes(Uri address, int maximum, Stopwatch elapsed,
            out Uri responseAddress, bool allowTruncated = false)
        {
            responseAddress = address;
            int timeout = Math.Min(4500, 9000 - (int)elapsed.ElapsedMilliseconds);
            if (timeout <= 0) return null;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.Method = "GET";
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 4;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.UserAgent = "AppShelf/2.6 (favicon)";
            // ReadWriteTimeout alone restarts on each read and does not bound a trickle response.
            using (Timer deadline = new Timer(delegate { request.Abort(); }, null, timeout, Timeout.Infinite))
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                Uri final = response.ResponseUri;
                responseAddress = final;
                if ((final.Scheme != Uri.UriSchemeHttp && final.Scheme != Uri.UriSchemeHttps) ||
                    final.UserInfo.Length != 0 || response.StatusCode != HttpStatusCode.OK ||
                    (!allowTruncated && response.ContentLength > maximum)) return null;
                using (Stream stream = response.GetResponseStream())
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (output.Length + read > maximum)
                        {
                            if (!allowTruncated) return null;
                            output.Write(buffer, 0, (int)(maximum - output.Length));
                            break; // HTML head is enough; never download an unbounded page.
                        }
                        output.Write(buffer, 0, read);
                    }
                    return output.Length == 0 ? null : output.ToArray();
                }
            }
        }

        private static bool IsDecodableIcon(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8 || bytes.Length > 262144) return false;
            try
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image.PixelWidth > 0 && image.PixelHeight > 0;
            }
            catch (Exception) { return false; }
        }

        private static string HtmlAttribute(string tag, string attribute)
        {
            string pattern = "\\b" + System.Text.RegularExpressions.Regex.Escape(attribute) +
                "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))";
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(tag, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string value = match.Groups[1].Success ? match.Groups[1].Value :
                match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            return WebUtility.HtmlDecode(value);
        }

        private static List<Uri> FindLinkedIcons(Uri site, Stopwatch elapsed)
        {
            List<Uri> icons = new List<Uri>();
            try
            {
                Uri pageAddress;
                byte[] page = FetchBytes(site, 131072, elapsed, out pageAddress, true);
                if (page == null) return icons;
                string html = Encoding.UTF8.GetString(page);
                System.Text.RegularExpressions.MatchCollection tags =
                    System.Text.RegularExpressions.Regex.Matches(html, "<link\\b[^>]*>",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match tag in tags)
                {
                    string rel = HtmlAttribute(tag.Value, "rel") ?? "";
                    if (rel.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string href = HtmlAttribute(tag.Value, "href");
                    if (String.IsNullOrWhiteSpace(href) || href.Length > 4096) continue;
                    Uri url;
                    if (!Uri.TryCreate(pageAddress, href, out url) ||
                        (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
                        url.UserInfo.Length != 0) continue;
                    if (url.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!icons.Contains(url)) icons.Add(url);
                    if (icons.Count == 8) break;
                }
            }
            catch (Exception) { } // HTML discovery is optional.
            return icons;
        }

        private static bool StoreIcon(Uri site, byte[] bytes)
        {
            if (!IsDecodableIcon(bytes)) return false;
            string filename = IconPath(site);
            string temporary = null;
            try
            {
                Directory.CreateDirectory(IconsDirectory);
                temporary = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                if (!File.Exists(filename)) File.Move(temporary, filename);
                return true;
            }
            catch (Exception) { return false; }
            finally
            {
                if (temporary != null)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
                }
            }
        }

        public static bool FetchFavicon(Uri site)
        {
            if (LoadCachedIcon(site) != null) return true;
            Stopwatch elapsed = Stopwatch.StartNew();
            Uri responseAddress;
            Uri root = new Uri(site.GetLeftPart(UriPartial.Authority) + "/");
            try
            {
                Uri standard = new Uri(root, "favicon.ico");
                if (StoreIcon(site, FetchBytes(standard, 262144, elapsed, out responseAddress))) return true;
            }
            catch (Exception) { }
            // Many modern websites declare icon URL in HTML or redirect to a CDN.
            foreach (Uri linked in FindLinkedIcons(root, elapsed))
            {
                try
                {
                    if (StoreIcon(site, FetchBytes(linked, 262144, elapsed, out responseAddress))) return true;
                }
                catch (Exception) { }
            }
            return false; // Fallback to the Windows .url icon.
        }
    }
}
