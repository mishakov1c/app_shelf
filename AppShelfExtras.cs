// AppShelf extensions: Windows route analysis and optional RDP idle prevention.
// Compatible with C# 5 / .NET Framework 4.8 and .NET 8 WPF.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
// Ordinary messages follow AppShelf's dark dialog; no native startup errors here.
using MessageBox = AppShelf.AppDialog;
using System.Windows.Controls;
using System.Windows.Media;

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

    internal static class HappDiagnostics
    {
        private const string AdapterName = "happ-default-tun";
        private const uint ErrorInsufficientBuffer = 122;

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardRow
        {
            public uint Destination;
            public uint Mask;
            public uint Policy;
            public uint NextHop;
            public uint InterfaceIndex;
            public uint Type;
            public uint Protocol;
            public uint Age;
            public uint NextHopAs;
            public uint Metric1;
            public uint Metric2;
            public uint Metric3;
            public uint Metric4;
            public uint Metric5;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIpForwardTable(IntPtr table, ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order);
        [DllImport("iphlpapi.dll")]
        private static extern uint GetBestInterface(uint destination, out uint interfaceIndex);

        private static string Ip(uint value)
        {
            // DWORD values are stored as the IPv4 octets in host memory order.
            return new IPAddress(BitConverter.GetBytes(value)).ToString();
        }

        private static int PrefixLength(uint mask)
        {
            byte[] bytes = BitConverter.GetBytes(mask);
            int bits = 0;
            foreach (byte b in bytes)
            {
                for (int i = 0; i < 8; i++)
                    if ((b & (1 << i)) != 0) bits++;
            }
            return bits;
        }

        private static List<MibIpForwardRow> ReadRoutes()
        {
            int length = 0;
            uint first = GetIpForwardTable(IntPtr.Zero, ref length, true);
            if (first != ErrorInsufficientBuffer || length <= 4)
                throw new Win32Exception((int)first, "Не удалось узнать размер таблицы IPv4.");
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                uint result = GetIpForwardTable(buffer, ref length, true);
                if (result != 0) throw new Win32Exception((int)result, "Не удалось получить таблицу IPv4.");
                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf(typeof(MibIpForwardRow));
                if (count < 0 || count > (length - 4) / rowSize)
                    throw new InvalidDataException("Неожиданный размер таблицы IPv4.");
                List<MibIpForwardRow> routes = new List<MibIpForwardRow>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = IntPtr.Add(buffer, 4 + i * rowSize);
                    routes.Add((MibIpForwardRow)Marshal.PtrToStructure(pointer, typeof(MibIpForwardRow)));
                }
                return routes;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static NetworkInterface FindTunAdapter()
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                if (String.Equals(nic.Name, AdapterName, StringComparison.OrdinalIgnoreCase))
                    return nic;
            return null;
        }

        public static bool HasTunAdapter()
        {
            return FindTunAdapter() != null;
        }

        private static string BestInterface(string ip)
        {
            uint index;
            uint packed = BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
            uint error = GetBestInterface(packed, out index);
            if (error != 0) return ip + ": недоступно (ошибка " + error + ")";
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    IPv4InterfaceProperties props = nic.GetIPProperties().GetIPv4Properties();
                    if (props != null && props.Index == index)
                        return ip + ": " + nic.Name + " (интерфейс #" + index + ")";
                }
                catch (NetworkInformationException) { }
            }
            return ip + ": интерфейс #" + index;
        }

        public static string Report()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("Проверка: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("Адаптер: " + AdapterName);
            report.AppendLine();
            NetworkInterface tun = FindTunAdapter();
            int tunIndex = -1;
            if (tun == null)
            {
                report.AppendLine("Адаптер не найден в Windows.");
            }
            else
            {
                report.AppendLine("Состояние адаптера: " + tun.OperationalStatus);
                try
                {
                    IPv4InterfaceProperties props = tun.GetIPProperties().GetIPv4Properties();
                    if (props != null)
                    {
                        tunIndex = props.Index;
                        report.AppendLine("InterfaceIndex: " + tunIndex);
                    }
                    foreach (UnicastIPAddressInformation address in tun.GetIPProperties().UnicastAddresses)
                        if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            report.AppendLine("IPv4: " + address.Address);
                }
                catch (NetworkInformationException ex)
                {
                    report.AppendLine("Ошибка чтения параметров адаптера: " + ex.Message);
                }
            }
            report.AppendLine();
            report.AppendLine("Активная таблица маршрутов IPv4 для Happ:");
            try
            {
                List<MibIpForwardRow> allRoutes = ReadRoutes();
                List<MibIpForwardRow> routes = new List<MibIpForwardRow>();
                foreach (MibIpForwardRow row in allRoutes)
                    if (tunIndex >= 0 && row.InterfaceIndex == (uint)tunIndex) routes.Add(row);
                report.AppendLine("Маршрутов: " + routes.Count);
                foreach (MibIpForwardRow row in routes)
                    report.AppendLine("  " + Ip(row.Destination) + "/" + PrefixLength(row.Mask) +
                        "  next hop: " + Ip(row.NextHop) + "  метрика: " + row.Metric1);
                if (tunIndex < 0) report.AppendLine("  Нет активного индекса Happ TUN для фильтрации.");
            }
            catch (Exception ex)
            {
                report.AppendLine("Ошибка таблицы маршрутов: " + ex.Message);
                ConfigStore.Log(ex);
            }
            report.AppendLine();
            report.AppendLine("Выбранные интерфейсы Windows (это не тест передачи данных):");
            report.AppendLine("  " + BestInterface("1.1.1.1"));
            report.AppendLine("  " + BestInterface("8.8.8.8"));
            report.AppendLine();
            report.AppendLine("ВНИМАНИЕ: активный TUN — нормальное состояние при включённом Happ.");
            report.AppendLine("Нельзя определить переключатель Happ или работоспособность VPN по одним маршрутам.");
            report.AppendLine("Отчёт о маршрутах охватывает только IPv4; VPN-соединение не проверяется.");
            report.AppendLine();
            report.Append(ProxyReport());
            report.AppendLine();
            report.Append(DnsServersReport());
            report.AppendLine();
            report.AppendLine("WinHTTP, PAC и браузерные расширения могут использовать другие настройки прокси.");
            return report.ToString();
        }


        // The current user's WinINET proxy is also used by many browsers, but it is
        // not a reliable indicator of Happ's actual VPN state.
        internal sealed class ProxySnapshot
        {
            public bool Enabled;
            public string Server;
            public string AutoConfigUrl;
        }

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

        public static ProxySnapshot ReadProxy()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
            {
                if (key == null) throw new InvalidOperationException("Раздел настроек прокси Windows не найден.");
                return new ProxySnapshot
                {
                    Enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0,
                    Server = Convert.ToString(key.GetValue("ProxyServer", "")) ?? "",
                    AutoConfigUrl = Convert.ToString(key.GetValue("AutoConfigURL", "")) ?? ""
                };
            }
        }

        // Conservative filter: every explicitly configured proxy must be loopback.
        // Mixed local/remote, PAC, invalid and empty configurations are not modified.
        public static bool IsLoopbackProxy(string configured)
        {
            if (String.IsNullOrWhiteSpace(configured)) return false;
            string[] entries = configured.Split(';');
            int valid = 0;
            foreach (string entry in entries)
            {
                string part = entry.Trim();
                if (part.Length == 0) continue;
                int equals = part.IndexOf('=');
                if (equals >= 0) part = part.Substring(equals + 1).Trim();
                if (part.Length == 0) return false;
                Uri uri;
                string address = part.IndexOf("://", StringComparison.Ordinal) < 0 ? "http://" + part : part;
                if (!Uri.TryCreate(address, UriKind.Absolute, out uri)) return false;
                if (uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
                    uri.Fragment.Length != 0) return false;
                IPAddress ip;
                if (!String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                    (!IPAddress.TryParse(uri.Host, out ip) || !IPAddress.IsLoopback(ip))) return false;
                valid++;
            }
            return valid > 0;
        }

        private static void NotifyProxyChanged()
        {
            // Inform WinINET applications about the setting. Other applications may
            // still require a restart or use an independent proxy configuration.
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
        }

        public static void SetLocalProxyEnabled(bool enabled, string expectedServer)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
            {
                if (key == null) throw new InvalidOperationException("Нет доступа к настройкам прокси.");
                ProxySnapshot current = ReadProxy();
                if (!String.Equals(current.Server, expectedServer, StringComparison.Ordinal) ||
                    !IsLoopbackProxy(current.Server) || !String.IsNullOrWhiteSpace(current.AutoConfigUrl))
                    throw new InvalidOperationException("Настройки прокси изменились либо не являются локальными. Действие отменено.");
                if (current.Enabled == enabled)
                    throw new InvalidOperationException("Состояние прокси уже изменилось. Действие отменено.");
                key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            NotifyProxyChanged();
        }

        public static string ProxyReport()
        {
            try
            {
                ProxySnapshot proxy = ReadProxy();
                StringBuilder result = new StringBuilder();
                result.AppendLine("Системный прокси WinINET (текущий пользователь):");
                result.AppendLine("  Ручной прокси: " + (proxy.Enabled ? "включён" : "выключен"));
                result.AppendLine("  Адрес: " + (String.IsNullOrWhiteSpace(proxy.Server) ? "не указан" : proxy.Server));
                result.AppendLine("  PAC-скрипт: " + (String.IsNullOrWhiteSpace(proxy.AutoConfigUrl) ? "не указан" : proxy.AutoConfigUrl));
                if (proxy.Enabled && IsLoopbackProxy(proxy.Server))
                    result.AppendLine("  Внимание: указан локальный прокси. Его принадлежность Happ НЕ подтверждена.");
                return result.ToString();
            }
            catch (Exception ex)
            {
                return "Не удалось проверить WinINET-прокси: " + ex.Message + Environment.NewLine;
            }
        }

        private static string DnsServersReport()
        {
            StringBuilder result = new StringBuilder();
            result.AppendLine("DNS-серверы активных сетевых адаптеров:");
            try
            {
                int count = 0;
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    StringBuilder addresses = new StringBuilder();
                    foreach (IPAddress dns in properties.DnsAddresses)
                    {
                        if (addresses.Length > 0) addresses.Append(", ");
                        addresses.Append(dns);
                    }
                    if (addresses.Length == 0) continue;
                    result.AppendLine("  " + nic.Name + ": " + addresses);
                    count++;
                }
                if (count == 0) result.AppendLine("  Не обнаружены.");
            }
            catch (Exception ex) { result.AppendLine("  Ошибка: " + ex.Message); }
            return result.ToString();
        }

        private static async Task<string> CheckDnsAsync()
        {
            try
            {
                Task<IPAddress[]> resolve = Dns.GetHostAddressesAsync("ya.ru");
                if (await Task.WhenAny(resolve, Task.Delay(3500)) != resolve)
                    return "тайм-аут";
                IPAddress[] addresses = await resolve;
                return addresses.Length > 0 ? "работает" : "нет адресов";
            }
            catch (Exception ex) { return "ошибка: " + ex.GetType().Name; }
        }

        private static async Task<string> CheckHttpsAsync(bool direct)
        {
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create("https://ya.ru/");
                request.Method = "HEAD";
                request.UserAgent = "AppShelf (Happ diagnostics)";
                request.Proxy = direct ? null : WebRequest.GetSystemWebProxy();
                request.AllowAutoRedirect = true;
                request.Timeout = 4000;
                request.ReadWriteTimeout = 4000;
                Stopwatch watch = Stopwatch.StartNew();
                Task<WebResponse> response = request.GetResponseAsync();
                if (await Task.WhenAny(response, Task.Delay(4500)) != response)
                {
                    request.Abort();
                    return "тайм-аут";
                }
                try
                {
                    using (WebResponse value = await response) { }
                }
                catch (WebException ex)
                {
                    // HTTP 403/405 means transport worked, not that the site is down.
                    if (!(ex.Response is HttpWebResponse)) return ex.Status.ToString();
                    ex.Response.Dispose();
                }
                return "доступен (" + watch.ElapsedMilliseconds + " мс)";
            }
            catch (WebException ex) { return ex.Status.ToString(); }
            catch (Exception ex) { return "ошибка: " + ex.GetType().Name; }
            finally { if (request != null) request.Abort(); }
        }

        internal sealed class ConnectivitySnapshot
        {
            public string Dns;
            public string DirectHttps;
            public string SystemHttps;

            public bool InternetAvailable
            {
                get {
                    return (DirectHttps ?? "").StartsWith("доступен", StringComparison.Ordinal) ||
                        (SystemHttps ?? "").StartsWith("доступен", StringComparison.Ordinal);
                }
            }

            public string ToReport()
            {
                return "Проверка реальной доступности:" + Environment.NewLine +
                    "  DNS ya.ru: " + Dns + Environment.NewLine +
                    "  HTTPS напрямую: " + DirectHttps + Environment.NewLine +
                    "  HTTPS через системный прокси: " + SystemHttps + Environment.NewLine +
                    "  Эти проверки не гарантируют доступность всех сайтов или работу браузера." + Environment.NewLine;
            }
        }

        internal static async Task<ConnectivitySnapshot> CheckConnectivityAsync()
        {
            Task<string> dns = CheckDnsAsync();
            Task<string> direct = CheckHttpsAsync(true);
            Task<string> system = CheckHttpsAsync(false);
            await Task.WhenAll(dns, direct, system);
            return new ConnectivitySnapshot
            {
                Dns = dns.Result, DirectHttps = direct.Result, SystemHttps = system.Result
            };
        }

        public static async Task<string> ConnectivityReportAsync()
        {
            return (await CheckConnectivityAsync()).ToReport();
        }

        internal static string ActiveInterfacesSummary()
        {
            StringBuilder result = new StringBuilder();
            try
            {
                int count = 0;
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (count++ >= 7)
                    {
                        result.AppendLine("… другие активные интерфейсы");
                        break;
                    }
                    result.Append(nic.Name).Append("  ·  ").Append(nic.NetworkInterfaceType);
                    try
                    {
                        IPInterfaceProperties props = nic.GetIPProperties();
                        foreach (UnicastIPAddressInformation address in props.UnicastAddresses)
                            if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                result.Append("  ·  ").Append(address.Address);
                                break;
                            }
                        foreach (IPAddress dns in props.DnsAddresses)
                            if (dns.AddressFamily == AddressFamily.InterNetwork)
                            {
                                result.Append("  ·  DNS ").Append(dns);
                                break;
                            }
                    }
                    catch (NetworkInformationException) { }
                    result.AppendLine();
                }
                if (count == 0) result.AppendLine("Нет активных интерфейсов.");
            }
            catch (Exception ex) { result.Append("Не удалось получить адаптеры: ").Append(ex.Message); }
            return result.ToString();
        }

        public static Process[] RunningHappProcesses()
        {
            // Exact executable name only. Never terminate another VPN or proxy service.
            return Process.GetProcessesByName("Happ");
        }

        public static async Task<int> FlushDnsAsync()
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe");
            psi.Arguments = "/flushdns";
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить ipconfig.");
                await Task.Run(delegate { process.WaitForExit(); });
                return process.ExitCode;
            }
        }

        public static async Task<int> DisableTunAsync()
        {
            // Ask Windows to elevate a single command. AppShelf itself stays unelevated
            // so shell drag/drop continues to work normally.
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
            psi.Arguments = "interface set interface name=\"happ-default-tun\" admin=disabled";
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить netsh.");
                await Task.Run(delegate { process.WaitForExit(); });
                return process.ExitCode;
            }
        }
    }

    // Exactly two configurable probes replace the old fixed pairs. A legacy slot
    // preserves the original ICMP+HTTPS behavior for existing apps.json files.
    internal static class NetworkChecks
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

        internal static async Task<string> ProbeAsync(Target target, int index)
        {
            if (!String.IsNullOrWhiteSpace(target.Error))
                return "Проверка " + index + ": " + target.Error;
            if (target.Mode == "default")
                return await MainWindow.ProbeEndpoint(target.Host,
                    index == 1 ? "https://1.1.1.1/cdn-cgi/trace" : "https://ya.ru/");
            long? duration;
            if (target.Mode == "icmp")
            {
                duration = await MainWindow.TryIcmp(target.Host);
                return "ICMP " + target.Host + ": " +
                    (duration.HasValue ? duration.Value + " мс" : "нет ответа (ICMP может блокироваться)");
            }
            if (target.Mode == "https")
            {
                duration = await MainWindow.TryHttps(target.Value);
                return "HTTPS " + target.Value + ": " +
                    (duration.HasValue ? duration.Value + " мс" : "нет ответа");
            }
            duration = await MainWindow.TryTcp(target.Host, target.Port);
            return "TCP " + target.Value + ": " +
                (duration.HasValue ? duration.Value + " мс" : "нет соединения");
        }
    }

    public sealed class HappDiagnosticsWindow : Window
    {
        private readonly TextBox _report;
        private AppConfig _networkConfig;
        private int _networkConfigVersion;
        private readonly TextBlock _internetStatus;
        private readonly TextBlock _dnsStatus;
        private readonly TextBlock _proxyStatus;
        private readonly TextBlock _interfacesText;
        private readonly TextBlock _checksText;
        private readonly Button _disable;
        private readonly Button _refresh;
        private readonly Button _recover;
        private readonly Button _restoreProxy;
        private bool _busy;
        private string _lastDisabledProxy;
        private static readonly Brush Back = FluentUi.Background;
        private static readonly Brush Tile = FluentUi.Surface;
        private static readonly Brush Fore = FluentUi.Foreground;

        private static Brush BrushFrom(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static Button CreateButton(string text)
        {
            return new Button { Content = text, Padding = new Thickness(13, 7, 13, 7),
                Margin = new Thickness(0, 0, 9, 0), Background = Tile, Foreground = Fore };
        }

        private static Border SummaryCard(string title, out TextBlock status)
        {
            StackPanel content = new StackPanel { Margin = new Thickness(1) };
            TextBlock label = new TextBlock
            {
                Text = title, FontSize = 12, Foreground = FluentUi.Secondary
            };
            content.Children.Add(label);
            status = new TextBlock
            {
                Text = "Проверяем…", FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = Fore, Margin = new Thickness(0, 7, 0, 0)
            };
            content.Children.Add(status);
            return new Border
            {
                Width = 175, MinHeight = 82, Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(13), CornerRadius = new CornerRadius(11),
                Background = FluentUi.SurfaceRaised,
                BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1),
                Child = content
            };
        }

        private static Border DetailCard(string heading, TextBlock body)
        {
            StackPanel content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = heading, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = Fore, Margin = new Thickness(0, 0, 0, 8)
            });
            content.Children.Add(body);
            return new Border
            {
                Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 11),
                Background = FluentUi.Surface,
                BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11), Child = content
            };
        }

        public HappDiagnosticsWindow() : this(new AppConfig()) { }

        public HappDiagnosticsWindow(AppConfig config)
        {
            _networkConfig = config ?? new AppConfig();
            Title = "AppShelf — Диагностика сети";
            Width = 790;
            Height = 640;
            MinWidth = 620;
            MinHeight = 490;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FluentUi.PrepareWindow(this);
            DockPanel root = new DockPanel { Margin = new Thickness(22) };
            Content = root;
            StackPanel intro = new StackPanel { Margin = new Thickness(2, 0, 2, 16) };
            TextBlock heading = new TextBlock { Text = "Диагностика сети", FontSize = 23,
                FontWeight = FontWeights.Bold, Foreground = Fore };
            intro.Children.Add(heading);
            TextBlock note = new TextBlock { Text = "Интернет, DNS, системный прокси и сетевые интерфейсы Windows. Подробности и действия восстановления — ниже.",
                Foreground = FluentUi.Secondary, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            intro.Children.Add(note);
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);
            WrapPanel controls = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
            DockPanel.SetDock(controls, Dock.Bottom);
            root.Children.Add(controls);
            _refresh = CreateButton("Повторить проверку");
            _refresh.Click += async delegate { await RefreshReportAsync(); };
            controls.Children.Add(_refresh);
            Button copy = CreateButton("Скопировать отчёт");
            copy.Click += delegate
            {
                try { Clipboard.SetText(_report.Text); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            controls.Children.Add(copy);
            _disable = CreateButton("Отключить Happ TUN…");
            _disable.Click += async delegate { await DisableClicked(); };
            controls.Children.Add(_disable);
            _recover = CreateButton("Восстановить интернет…");
            _recover.Click += async delegate { await RecoverClicked(); };
            controls.Children.Add(_recover);
            _restoreProxy = CreateButton("Вернуть прокси…");
            _restoreProxy.Visibility = Visibility.Collapsed;
            _restoreProxy.Click += async delegate { await RestoreProxyClicked(); };
            controls.Children.Add(_restoreProxy);
            _report = new TextBox
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 12.5,
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Tile, Foreground = Fore, Padding = new Thickness(15),
                BorderBrush = FluentUi.BorderColor, BorderThickness = new Thickness(1)
            };
            FluentUi.StyleDiagnosticReportScroll(_report);
            // Top-level diagnostics remain useful even when Happ is not installed.
            controls.Children.Remove(_disable);
            controls.Children.Remove(_recover);
            controls.Children.Remove(_restoreProxy);

            StackPanel dashboard = new StackPanel
            {
                Margin = new Thickness(1, 4, 12, 12)
            };
            WrapPanel statusCards = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            statusCards.Children.Add(SummaryCard("Интернет", out _internetStatus));
            statusCards.Children.Add(SummaryCard("DNS", out _dnsStatus));
            statusCards.Children.Add(SummaryCard("Прокси", out _proxyStatus));
            dashboard.Children.Add(statusCards);

            _interfacesText = new TextBlock
            {
                Text = "Определяем интерфейсы…", FontSize = 12.5,
                Foreground = FluentUi.Secondary, TextWrapping = TextWrapping.Wrap,
                Tag = "NetworkInterfacesOverview"
            };
            dashboard.Children.Add(DetailCard("Сетевые подключения", _interfacesText));
            _checksText = new TextBlock
            {
                Text = "Проверяем DNS и HTTPS…", FontSize = 12.5,
                Foreground = FluentUi.Secondary, TextWrapping = TextWrapping.Wrap,
                Tag = "NetworkConnectivityChecks"
            };
            dashboard.Children.Add(DetailCard("Проверки подключения", _checksText));

            Expander details = new Expander
            {
                Header = "Подробный отчёт", IsExpanded = false,
                Foreground = Fore, Background = Tile,
                Margin = new Thickness(0, 0, 0, 10),
                Tag = "NetworkDetailedReport"
            };
            _report.Height = 205;
            details.Content = _report;
            dashboard.Children.Add(details);

            WrapPanel advancedActions = new WrapPanel
            {
                Margin = new Thickness(9, 9, 0, 9)
            };
            advancedActions.Children.Add(_recover);
            advancedActions.Children.Add(_disable);
            advancedActions.Children.Add(_restoreProxy);
            Expander advanced = new Expander
            {
                Header = "Восстановление и Happ — дополнительные действия",
                IsExpanded = false, Foreground = Fore, Background = Tile,
                Tag = "HappSpecialActions", Content = advancedActions
            };
            dashboard.Children.Add(advanced);

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            FluentUi.StyleSettingsScroll(scroll);
            scroll.Content = dashboard;
            root.Children.Add(scroll);
            ButtonsEnabled(true);
            Loaded += async delegate { await RefreshReportAsync(); };
        }


        // Existing diagnostic windows must not show stale targets after Settings saves.
        // No automatic new traffic: the user explicitly repeats the check if needed.
        public void UpdateNetworkConfig(AppConfig config)
        {
            _networkConfig = config ?? new AppConfig();
            _networkConfigVersion++;
            _internetStatus.Text = "Настройки изменены";
            _internetStatus.Foreground = FluentUi.Secondary;
            _dnsStatus.Text = "Повтори проверку";
            _dnsStatus.Foreground = FluentUi.Secondary;
            _checksText.Text = "Сетевые цели изменены. Нажми «Повторить проверку» для новых результатов.";
            _report.Text = "Настройки изменились; предыдущие результаты больше не актуальны.";
        }

        private void ButtonsEnabled(bool enabled)
        {
            _refresh.IsEnabled = enabled;
            _recover.IsEnabled = enabled;
            bool hasHapp = HappDiagnostics.HasTunAdapter();
            _disable.IsEnabled = enabled && hasHapp;
            _disable.Visibility = hasHapp ? Visibility.Visible : Visibility.Collapsed;
            _restoreProxy.IsEnabled = enabled && _lastDisabledProxy != null;
        }

        private async Task RefreshReportCoreAsync()
        {
            int checkedVersion = _networkConfigVersion;
            AppConfig checkedConfig = _networkConfig.Copy();
            _internetStatus.Text = "Проверяем…";
            _dnsStatus.Text = "Проверяем…";
            _checksText.Text = "Проверяем DNS и HTTPS…";
            _interfacesText.Text = HappDiagnostics.ActiveInterfacesSummary();
            try
            {
                HappDiagnostics.ProxySnapshot proxy = HappDiagnostics.ReadProxy();
                _proxyStatus.Text = proxy.Enabled ? "Включён" :
                    !String.IsNullOrWhiteSpace(proxy.AutoConfigUrl) ? "PAC-скрипт" : "Не задан";
                _proxyStatus.Foreground = FluentUi.Secondary;
            }
            catch (Exception ex)
            {
                _proxyStatus.Text = "Неизвестно";
                ConfigStore.Log(ex);
            }

            string report = HappDiagnostics.Report();
            _report.Text = report + Environment.NewLine + "Проверяем DNS и HTTPS…";
            if (!String.IsNullOrWhiteSpace(checkedConfig.NetworkCheck1Mode) ||
                !String.IsNullOrWhiteSpace(checkedConfig.NetworkCheck2Mode))
            {
                // Custom targets REPLACE the fixed destinations, not add background
                // diagnostics to them. Only two expressly selected checks run here.
                Task<string> one = NetworkChecks.ProbeAsync(NetworkChecks.Resolve(checkedConfig, 1), 1);
                Task<string> two = NetworkChecks.ProbeAsync(NetworkChecks.Resolve(checkedConfig, 2), 2);
                await Task.WhenAll(one, two);
                if (checkedVersion != _networkConfigVersion) return;
                _internetStatus.Text = "См. проверки";
                _internetStatus.Foreground = FluentUi.Secondary;
                _dnsStatus.Text = "Отдельно не проверяется";
                _dnsStatus.Foreground = FluentUi.Secondary;
                _checksText.Text = one.Result + Environment.NewLine + two.Result;
                _report.Text = report + Environment.NewLine +
                    "Проверки пользовательских адресов (не оценка всего интернета):" +
                    Environment.NewLine + "  " + one.Result +
                    Environment.NewLine + "  " + two.Result;
                return;
            }
            HappDiagnostics.ConnectivitySnapshot snapshot = await HappDiagnostics.CheckConnectivityAsync();
            if (checkedVersion != _networkConfigVersion) return;
            _internetStatus.Text = snapshot.InternetAvailable ? "Доступен" : "Не подтверждён";
            _internetStatus.Foreground = snapshot.InternetAvailable ? FluentUi.Green : FluentUi.Amber;
            _dnsStatus.Text = snapshot.Dns == "работает" ? "Работает" : "Ошибка / тайм-аут";
            _dnsStatus.Foreground = snapshot.Dns == "работает" ? FluentUi.Green : FluentUi.Amber;
            _checksText.Text = "DNS ya.ru: " + snapshot.Dns + Environment.NewLine +
                "HTTPS напрямую: " + snapshot.DirectHttps + Environment.NewLine +
                "HTTPS через системный прокси: " + snapshot.SystemHttps;
            _report.Text = report + Environment.NewLine + snapshot.ToReport();
        }

        private async Task RefreshReportAsync()
        {
            if (_busy) return;
            _busy = true;
            ButtonsEnabled(false);
            try { await RefreshReportCoreAsync(); }
            catch (Exception ex)
            {
                ConfigStore.Log(ex);
                _internetStatus.Text = "Не подтверждён";
                _dnsStatus.Text = "Ошибка проверки";
                _checksText.Text = "Не удалось завершить диагностику. Подробности — в отчёте.";
                _report.Text = "Ошибка диагностики: " + ex.Message + "\nЖурнал: " + ConfigStore.LogPath;
            }
            finally { _busy = false; ButtonsEnabled(true); }
        }

        private async Task DisableClicked()
        {
            if (_busy) return;
            MessageBoxResult confirmation = MessageBox.Show(this,
                "Отключить сетевой адаптер happ-default-tun?\n\n" +
                "Это прервёт VPN-соединение и может отключить текущий RDP-сеанс. " +
                "Happ может создать адаптер заново при переподключении. " +
                "Сам процесс Happ завершаться не будет.\n\nПродолжить?",
                "AppShelf — отключение TUN", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
            _busy = true;
            ButtonsEnabled(false);
            try
            {
                int exitCode = await HappDiagnostics.DisableTunAsync();
                if (exitCode != 0)
                    MessageBox.Show(this, "Windows вернула ошибку " + exitCode + " при отключении TUN.",
                        "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
                await RefreshReportCoreAsync();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223)
                {
                    ConfigStore.Log(ex);
                    MessageBox.Show(this, ex.Message, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                ConfigStore.Log(ex);
                MessageBox.Show(this, ex.Message, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _busy = false; ButtonsEnabled(true); }
        }

        private async Task RecoverClicked()
        {
            if (_busy) return;
            if (MessageBox.Show(this,
                "Запустить пошаговое восстановление сети?\n\n" +
                "Каждое действие будет предложено отдельно, без автоматического сброса сети. " +
                "Прерывание Happ или TUN может отключить VPN и RDP. " +
                "Перед изменениями рекомендуется скопировать отчёт.\n\nПродолжить?",
                "AppShelf — восстановление", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _busy = true;
            ButtonsEnabled(false);
            StringBuilder actions = new StringBuilder();
            try
            {
                Process[] processes = HappDiagnostics.RunningHappProcesses();
                try
                {
                    if (processes.Length > 0 &&
                        MessageBox.Show(this,
                            "Обнаружено процессов Happ: " + processes.Length +
                            ".\n\nПринудительно завершить именно эти процессы? " +
                            "Несохранённое состояние Happ может быть потеряно. " +
                            "Другие VPN и процессы не затрагиваются.",
                            "AppShelf — завершение Happ", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        int stopped = 0;
                        foreach (Process process in processes)
                        {
                            try
                            {
                                // Recheck name in case the process exited/reused the PID.
                                if (!String.Equals(process.ProcessName, "Happ", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                process.Kill();
                                await Task.Run(delegate { process.WaitForExit(3000); });
                                if (process.HasExited) stopped++;
                            }
                            catch (Exception ex) { ConfigStore.Log(ex); }
                        }
                        actions.AppendLine("Завершено процессов Happ: " + stopped + "/" + processes.Length);
                    }
                }
                finally { foreach (Process process in processes) process.Dispose(); }

                if (HappDiagnostics.HasTunAdapter() &&
                    MessageBox.Show(this,
                        "Отключить адаптер happ-default-tun?\n\n" +
                        "Это может прервать RDP и связанные сетевые сеансы. Требуется UAC.",
                        "AppShelf — отключение TUN", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    int code = await HappDiagnostics.DisableTunAsync();
                    actions.AppendLine(code == 0 ? "Happ TUN отключён." : "Отключение TUN: код Windows " + code);
                }

                HappDiagnostics.ProxySnapshot proxy = HappDiagnostics.ReadProxy();
                if (proxy.Enabled && HappDiagnostics.IsLoopbackProxy(proxy.Server) &&
                    String.IsNullOrWhiteSpace(proxy.AutoConfigUrl))
                {
                    if (MessageBox.Show(this,
                        "Windows использует локальный прокси: " + proxy.Server +
                        "\n\nНевозможно доказать, что он принадлежит Happ. " +
                        "Он может использоваться другим приложением.\n\n" +
                        "Временно выключить ручной прокси WinINET? Адрес останется сохранён. " +
                        "Вернуть настройку можно кнопкой «Вернуть прокси» или в параметрах Windows.",
                        "AppShelf — локальный прокси", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        HappDiagnostics.SetLocalProxyEnabled(false, proxy.Server);
                        _lastDisabledProxy = proxy.Server;
                        _restoreProxy.Visibility = Visibility.Visible;
                        actions.AppendLine("Локальный прокси WinINET выключен по подтверждению.");
                    }
                }
                else if (proxy.Enabled || !String.IsNullOrWhiteSpace(proxy.AutoConfigUrl))
                {
                    actions.AppendLine("Прокси или PAC настроены; автоматического изменения нет.");
                }

                string checks = await HappDiagnostics.ConnectivityReportAsync();
                if (checks.IndexOf("DNS ya.ru: работает", StringComparison.Ordinal) < 0 &&
                    MessageBox.Show(this,
                        "Проверка DNS не прошла. Очистить только кеш DNS Windows " +
                        "командой ipconfig /flushdns?\n\nАдреса DNS-серверов изменяться не будут.",
                        "AppShelf — DNS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    int code = await HappDiagnostics.FlushDnsAsync();
                    actions.AppendLine(code == 0 ? "Кеш DNS очищен." : "Очистка DNS: код Windows " + code);
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) { ConfigStore.Log(ex); actions.AppendLine("Ошибка Windows: " + ex.Message); }
                else actions.AppendLine("Запрос прав администратора отменён.");
            }
            catch (Exception ex) { ConfigStore.Log(ex); actions.AppendLine("Ошибка восстановления: " + ex.Message); }
            try { await RefreshReportCoreAsync(); }
            catch (Exception ex) { ConfigStore.Log(ex); actions.AppendLine("Ошибка повторной проверки: " + ex.Message); }
            finally { _busy = false; ButtonsEnabled(true); }
            MessageBox.Show(this,
                (actions.Length == 0 ? "Никакие изменения не выполнялись." : actions.ToString()) +
                "\nПроверка обновлена в окне анализатора. Успешный HTTPS-тест не гарантирует работу браузера.",
                "AppShelf — результат восстановления", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task RestoreProxyClicked()
        {
            if (_busy || _lastDisabledProxy == null) return;
            if (MessageBox.Show(this,
                "Снова включить ручной прокси WinINET (" + _lastDisabledProxy + ")?\n\n" +
                "Это может вернуть зависимость браузера от локального прокси. " +
                "Действие доступно только пока открыто это окно.",
                "AppShelf — вернуть прокси", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _busy = true;
            ButtonsEnabled(false);
            try
            {
                HappDiagnostics.SetLocalProxyEnabled(true, _lastDisabledProxy);
                _lastDisabledProxy = null;
                _restoreProxy.Visibility = Visibility.Collapsed;
                await RefreshReportCoreAsync();
            }
            catch (Exception ex)
            {
                ConfigStore.Log(ex);
                MessageBox.Show(this, ex.Message, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _busy = false; ButtonsEnabled(true); }
        }

    }
}
