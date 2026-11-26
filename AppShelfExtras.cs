// Windows RDP idle prevention and read-only legacy network settings validation.
using System;
using System.Runtime.InteropServices;

namespace AppShelf
{
    internal static class RdpKeepAwake
    {
        private const int SmRemoteSession = 0x1000;
        private const byte VkF15 = 0x7E;
        private const uint KeyEventFKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint CbSize;
            public uint DwTime;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo input);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extra);

        public static bool IsRemoteSession()
        {
            // SESSIONNAME also detects RDP sessions in older Windows deployments.
            string name = Environment.GetEnvironmentVariable("SESSIONNAME") ?? "";
            return name.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase) ||
                   GetSystemMetrics(SmRemoteSession) != 0;
        }

        public static void KeepSessionActiveIfIdle()
        {
            // Never send simulated input to a local interactive session.
            if (!IsRemoteSession()) return;
            LastInputInfo input = new LastInputInfo();
            input.CbSize = (uint)Marshal.SizeOf(typeof(LastInputInfo));
            if (!GetLastInputInfo(ref input)) return;
            uint elapsed = unchecked((uint)Environment.TickCount - input.DwTime);
            if (elapsed < 40000) return;

            // A rarely-used virtual key: down/up, no text typed or mouse movement.
            // A server policy may still disconnect; Windows can ignore synthetic input.
            keybd_event(VkF15, 0, 0, UIntPtr.Zero);
            keybd_event(VkF15, 0, KeyEventFKeyUp, UIntPtr.Zero);
        }
    }

    // Pure validation for the one-time transfer of historical apps.json fields.
    internal static class LegacyNetworkTargets
    {
        internal sealed class Target
        {
            public string Mode;
            public string Value;
            public string Host;
            public int Port;
            public string Error;
        }

        public static string DefaultHost(int index)
        {
            return index == 1 ? "1.1.1.1" : "ya.ru";
        }

        internal static bool TryParse(string mode, string value, int index,
            out Target target, out string error)
        {
            target = null;
            error = null;
            if (index != 1 && index != 2)
            {
                error = "Недопустимый номер проверки.";
                return false;
            }
            mode = String.IsNullOrWhiteSpace(mode) ? "default" : mode.Trim().ToLowerInvariant();
            value = (value ?? "").Trim();
            if (mode == "default")
            {
                target = new Target { Mode = "default", Value = DefaultHost(index),
                    Host = DefaultHost(index), Port = 443 };
                return true;
            }
            if (mode != "icmp" && mode != "https" && mode != "tcp")
            {
                error = "Неизвестный тип проверки.";
                return false;
            }
            if (value.Length == 0 || value.Length > 512 ||
                value.IndexOfAny(new char[] { '\r', '\n', '\t', ' ' }) >= 0)
            {
                error = "Укажи один адрес без пробелов (не более 512 символов).";
                return false;
            }
            string host = value;
            int port = 443;
            if (mode == "https")
            {
                Uri uri;
                if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                    uri.Fragment.Length != 0 ||
                    uri.Host.Length == 0 || Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown)
                {
                    error = "Для HTTPS укажи полный адрес https://site.example/path без логина и фрагмента.";
                    return false;
                }
                host = uri.Host;
                port = uri.Port;
            }
            else if (mode == "tcp")
            {
                // Support host:port, IPv4:port and [IPv6]:port; never guess the port.
                int separator = value.LastIndexOf(':');
                if (separator <= 0 || separator == value.Length - 1 ||
                    !Int32.TryParse(value.Substring(separator + 1), out port) ||
                    port < 1 || port > 65535)
                {
                    error = "Для TCP укажи host:port, например server.company.local:443.";
                    return false;
                }
                host = value.Substring(0, separator);
                if (host.StartsWith("[", StringComparison.Ordinal) &&
                    host.EndsWith("]", StringComparison.Ordinal))
                    host = host.Substring(1, host.Length - 2);
            }
            if (mode != "https" &&
                (host.Length == 0 || host.IndexOfAny(new char[] {
                    '/', '\\', '@', '?', '#', '%', '[', ']', ' ' }) >= 0 ||
                 Uri.CheckHostName(host) == UriHostNameType.Unknown))
            {
                error = "Некорректный адрес узла. Укажи домен или IP без протокола.";
                return false;
            }
            target = new Target { Mode = mode, Value = value, Host = host, Port = port };
            return true;
        }

        internal static Target Resolve(AppConfig config, int index)
        {
            string mode = index == 1 ? config.NetworkCheck1Mode : config.NetworkCheck2Mode;
            string value = index == 1 ? config.NetworkCheck1Target : config.NetworkCheck2Target;
            Target target; string error;
            if (TryParse(mode, value, index, out target, out error)) return target;
            return new Target { Mode = mode, Value = value, Error = error };
        }

        internal static string Label(Target target, int index)
        {
            return target.Mode == "default" ? DefaultHost(index) : target.Value ?? ("Проверка " + index);
        }

    }
}
