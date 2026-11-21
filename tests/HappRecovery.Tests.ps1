# Windows PowerShell 5.1: safe, read-only proxy guard tests.
# Run after Build-AppShelf.cmd --no-start --no-pause.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'AppShelf.exe'
if (-not [IO.File]::Exists($exe)) { throw 'Build AppShelf.exe before running the tests.' }
$assembly = [Reflection.Assembly]::LoadFrom($exe)
$type = $assembly.GetType('AppShelf.HappDiagnostics', $true)
# --- Two replacement network targets: validation must happen before any IO.
$targetType = $assembly.GetType('AppShelf.NetworkChecks', $true)
$parse = $targetType.GetMethod('TryParse', [Reflection.BindingFlags]'Static, NonPublic')
if ($null -eq $parse) { throw 'FAIL: typed network target validator missing.' }
$targetCases = @(
    @{Mode=''; Target=''; Index=1; Good=$true; Canonical='1.1.1.1'}
    @{Mode='default'; Target=''; Index=2; Good=$true; Canonical='ya.ru'}
    @{Mode='icmp'; Target='192.168.1.1'; Index=1; Good=$true; Canonical='192.168.1.1'}
    @{Mode='icmp'; Target='server.company.local'; Index=2; Good=$true; Canonical='server.company.local'}
    @{Mode='https'; Target='https://example.com/path'; Index=1; Good=$true; Canonical='example.com'}
    @{Mode='tcp'; Target='server.company.local:8443'; Index=2; Good=$true; Canonical='server.company.local'}
    @{Mode='tcp'; Target='[::1]:443'; Index=1; Good=$true; Canonical='::1'}
    @{Mode='https'; Target='http://example.com'; Index=1; Good=$false}
    @{Mode='https'; Target='https://user:secret@example.com'; Index=1; Good=$false}
    @{Mode='https'; Target='https://example.com/#fragment'; Index=1; Good=$false}
    @{Mode='tcp'; Target='example.com'; Index=1; Good=$false}
    @{Mode='tcp'; Target='example.com:65536'; Index=1; Good=$false}
    @{Mode='icmp'; Target='https://example.com'; Index=2; Good=$false}
    @{Mode='icmp'; Target='host name'; Index=2; Good=$false}
    @{Mode='icmp'; Target='example.com/path'; Index=1; Good=$false}
    @{Mode='icmp'; Target='1.1.1.1'; Index=3; Good=$false}
    @{Mode='unknown'; Target='example.com'; Index=1; Good=$false}
)
foreach ($case in $targetCases) {
    $arguments = [object[]]@([string]$case.Mode, [string]$case.Target,
        [int]$case.Index, $null, $null)
    $valid = [bool]$parse.Invoke($null, $arguments)
    if ($valid -ne $case.Good) {
        throw ("FAIL: network target '{0}' ({1}) accepted={2}, expected={3}" -f
            $case.Target, $case.Mode, $valid, $case.Good)
    }
    if ($valid) {
        $parsed = $arguments[3]
        $parsedHost = [string]$parsed.GetType().GetField('Host').GetValue($parsed)
        if ($parsedHost -ne $case.Canonical -or $arguments[4] -ne $null) {
            throw "FAIL: normalized network host for $($case.Target)"
        }
    } elseif ([String]::IsNullOrWhiteSpace([string]$arguments[4])) {
        throw 'FAIL: invalid network target has no actionable validation message.'
    }
}
Write-Host ("PASS: {0} typed network target validations; no real network checks." -f $targetCases.Count)

$method = $type.GetMethod('IsLoopbackProxy', [Reflection.BindingFlags]'Public, Static')
if ($null -eq $method) { throw 'The proxy safety guard is missing.' }

$cases = @(
    @{ Label = 'IPv4 loopback'; Server = '127.0.0.1:1080'; Expected = $true }
    @{ Label = 'localhost'; Server = 'localhost:1080'; Expected = $true }
    @{ Label = 'explicit schemes'; Server = 'http=127.0.0.1:1080;https=localhost:8080'; Expected = $true }
    @{ Label = 'socks localhost'; Server = 'socks=localhost:1080'; Expected = $true }
    @{ Label = 'explicit IPv4 scheme'; Server = 'http://127.0.0.1:8888'; Expected = $true }
    @{ Label = 'IPv6 loopback'; Server = '[::1]:1080'; Expected = $true }
    @{ Label = 'empty'; Server = ''; Expected = $false }
    @{ Label = 'remote endpoint'; Server = 'proxy.company.example:8080'; Expected = $false }
    @{ Label = 'mixed local and remote'; Server = 'http=127.0.0.1:8080;https=proxy.company.example:8080'; Expected = $false }
    @{ Label = 'malformed token'; Server = '127.0.0.1:1080;remote-proxy'; Expected = $false }
    @{ Label = 'user info'; Server = 'http://user:secret@127.0.0.1:8080'; Expected = $false }
    @{ Label = 'path'; Server = '127.0.0.1:8080/path'; Expected = $false }
    @{ Label = 'public IPv4'; Server = '1.1.1.1:8080'; Expected = $false }
)
$passed = 0
foreach ($case in $cases) {
    $actual = [bool]$method.Invoke($null, [object[]]@($case.Server))
    if ($actual -ne $case.Expected) {
        throw ("FAIL: {0}, value '{1}', expected {2}, got {3}" -f $case.Label, $case.Server, $case.Expected, $actual)
    }
    $passed++
}
Write-Host ("HappRecovery: {0}/{0} proxy guard cases passed. No network settings were changed." -f $passed)

# Compile only the proxy methods from current sources, with in-memory IO.
# Never invoke SetLocalProxyEnabled from the real AppShelf assembly.
$source = [IO.File]::ReadAllText((Join-Path $root 'AppShelfExtras.cs'))
$start = $source.IndexOf('        internal sealed class ProxySnapshot')
$end = $source.IndexOf('        private static string DnsServersReport()', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Proxy source boundaries changed.' }
$proxySource = $source.Substring($start, $end - $start)
if ([regex]::Matches($proxySource, 'Registry\.CurrentUser').Count -ne 2) { throw 'Registry isolation anchors changed.' }
$proxySource = $proxySource.Replace('Registry.CurrentUser', 'MemoryProxyKey.CurrentUser').Replace('RegistryKey', 'MemoryProxyKey')
$native = '(?s)\[DllImport\("wininet.dll", SetLastError = true\)\]\s*\[return: MarshalAs\(UnmanagedType.Bool\)\]\s*private static extern bool InternetSetOption\(IntPtr handle, int option, IntPtr buffer, int length\);'
if ([regex]::Matches($proxySource, $native).Count -ne 1) { throw 'WinINET isolation anchor changed.' }
$proxySource = [regex]::Replace($proxySource, $native,
    'private static bool InternetSetOption(IntPtr h, int o, IntPtr b, int l) { MemoryProxyKey.Notifications++; return true; }')
# Fail closed if a native import or real registry entry point survives transformation.
if ($proxySource -match 'DllImport|\bextern\b|\bRegistry\b|\bRegistryKey\b|Microsoft\.Win32\.|\bProcess\b') {
    throw 'Unsafe IO remains in isolated proxy source.'
}
$fake = @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using Microsoft.Win32;
public sealed class MemoryProxyKey : IDisposable
{
    public static readonly MemoryProxyKey CurrentUser = new MemoryProxyKey();
    public static readonly Dictionary<string, object> Values = new Dictionary<string, object>();
    public static int Writes, Notifications;
    public MemoryProxyKey OpenSubKey(string path, bool writable) { return this; }
    public object GetValue(string name, object fallback) { object v; return Values.TryGetValue(name, out v) ? v : fallback; }
    public void SetValue(string name, object value, RegistryValueKind kind) { Values[name] = value; Writes++; }
    public void Dispose() { }
    public static void Reset(bool enabled, string server, string pac) {
        Values.Clear(); Values["ProxyEnable"] = enabled ? 1 : 0;
        Values["ProxyServer"] = server; Values["AutoConfigURL"] = pac;
        Writes = Notifications = 0;
    }
}
public static class IsolatedProxy {
'@
$isolatedTypes = Add-Type -TypeDefinition ($fake + $proxySource.Replace('internal sealed class ProxySnapshot', 'public sealed class ProxySnapshot') + '}') -PassThru
$proxyType = $isolatedTypes | Where-Object { $_.Name -eq 'IsolatedProxy' }
function Invoke-Guarded($enabled, $server) {
    try {
        [void]$proxyType.GetMethod('SetLocalProxyEnabled').Invoke($null, [object[]]@([bool]$enabled, [string]$server))
        return $null
    } catch {
        # PowerShell wraps the TargetInvocationException in a MethodInvocationException.
        $inner = $_.Exception
        while ($null -ne $inner -and -not ($inner -is [InvalidOperationException])) {
            $inner = $inner.InnerException
        }
        return $inner
    }
}

$guardCases = @(
    @{ Label='mismatch'; Server='localhost:1080'; Pac=''; Expected='localhost:1081'; Target=$false }
    @{ Label='unchanged state'; Server='localhost:1080'; Pac=''; Expected='localhost:1080'; Target=$true }
    @{ Label='remote'; Server='proxy.example:8080'; Pac=''; Expected='proxy.example:8080'; Target=$false }
    @{ Label='mixed'; Server='http=localhost:1080;https=proxy.example:8080'; Pac=''; Expected='http=localhost:1080;https=proxy.example:8080'; Target=$false }
    @{ Label='PAC'; Server='localhost:1080'; Pac='https://example.test/proxy.pac'; Expected='localhost:1080'; Target=$false }
)
foreach ($case in $guardCases) {
    [MemoryProxyKey]::Reset($true, $case.Server, $case.Pac)
    $ex = Invoke-Guarded $case.Target $case.Expected
    if ($null -eq $ex -or [MemoryProxyKey]::Writes -ne 0 -or [MemoryProxyKey]::Notifications -ne 0) {
        throw ('FAIL isolated guard: ' + $case.Label)
    }
    $passed++
    Write-Host ('PASS isolated guard: ' + $case.Label)
}
[MemoryProxyKey]::Reset($true, 'localhost:1080', '')
$snapshot = $proxyType.GetMethod('ReadProxy').Invoke($null, @())
if (-not $snapshot.Enabled -or $snapshot.Server -ne 'localhost:1080' -or $snapshot.AutoConfigUrl -ne '') { throw 'FAIL isolated snapshot.' }
$passed++
foreach ($enabled in @($false, $true)) {
    $ex = Invoke-Guarded $enabled 'localhost:1080'
    if ($null -ne $ex -or [int][MemoryProxyKey]::Values['ProxyEnable'] -ne [int]$enabled -or
        [MemoryProxyKey]::Values['ProxyServer'] -ne 'localhost:1080' -or
        [MemoryProxyKey]::Values['AutoConfigURL'] -ne '') { throw ("FAIL isolated disable/restore: enabled={0}, exception={1}, value={2}" -f $enabled, $ex, [MemoryProxyKey]::Values['ProxyEnable']) }
    $passed++
}
if ([MemoryProxyKey]::Writes -ne 2 -or [MemoryProxyKey]::Notifications -ne 4) { throw 'FAIL isolated IO counters.' }
Write-Host 'PASS: isolated snapshot, disable and restore (memory only).'

# --- Read-only diagnostics: report shape and process enumeration safety.
$proxyReport = $proxyType.GetMethod('ProxyReport').Invoke($null, @())
if ($proxyReport -notmatch 'WinINET' -or $proxyReport -notmatch 'Ручной прокси: (включён|выключен)' -or
    $proxyReport -notmatch 'PAC-скрипт:') { throw 'FAIL: ProxyReport misses the expected sections.' }
Write-Host 'PASS: ProxyReport contains proxy state, address and PAC sections.'
$passed++

$running = $type.GetMethod('RunningHappProcesses').Invoke($null, @())
if ($null -eq $running) { throw 'FAIL: RunningHappProcesses returned null.' }
foreach ($process in $running) {
    if (-not [String]::Equals($process.ProcessName, 'Happ', [StringComparison]::OrdinalIgnoreCase)) {
        throw ('FAIL: RunningHappProcesses returned a foreign process: ' + $process.ProcessName)
    }
    $process.Dispose()
}
Write-Host ("PASS: RunningHappProcesses returned {0} process(es), all named Happ; nothing was killed." -f $running.Length)
$passed++

# --- Analyzer window composition without showing it (no network, no settings changes).
$windowType = $assembly.GetType('AppShelf.HappDiagnosticsWindow', $true)
$window = [Activator]::CreateInstance($windowType)
$buttons = @()
$queue = New-Object 'System.Collections.Generic.Queue[Object]'
$queue.Enqueue($window)
while ($queue.Count -gt 0) {
    $node = $queue.Dequeue()
    if ($node -is [System.Windows.Controls.Button]) { $buttons += $node }
    if ($node -is [System.Windows.DependencyObject]) {
        foreach ($child in [System.Windows.LogicalTreeHelper]::GetChildren([System.Windows.DependencyObject]$node)) {
            $queue.Enqueue($child)
        }
    }
}
$labels = @{}
foreach ($button in $buttons) { $labels[[string]$button.Content] = $button }
foreach ($expected in @('Повторить проверку', 'Скопировать отчёт', 'Отключить Happ TUN…', 'Восстановить интернет…', 'Вернуть прокси…')) {
    if (-not $labels.ContainsKey($expected)) { throw "FAIL: analyzer window misses the '$expected' button." }
}
if ($labels['Вернуть прокси…'].Visibility -ne [System.Windows.Visibility]::Collapsed) {
    throw 'FAIL: restore-proxy button must start collapsed.'
}
if (-not $labels['Повторить проверку'].IsEnabled -or -not $labels['Восстановить интернет…'].IsEnabled) {
    throw 'FAIL: refresh and recover buttons must start enabled.'
}
if ($window.Title -ne 'AppShelf — Диагностика сети') {
    throw 'FAIL: window is still presented as a Happ-only analyzer.'
}
$expanders = @()
$queue2 = New-Object 'System.Collections.Generic.Queue[Object]'
$queue2.Enqueue($window)
while ($queue2.Count -gt 0) {
    $element = $queue2.Dequeue()
    if ($element -is [System.Windows.Controls.Expander]) { $expanders += $element }
    if ($element -is [System.Windows.DependencyObject]) {
        foreach ($nested in [System.Windows.LogicalTreeHelper]::GetChildren([System.Windows.DependencyObject]$element)) {
            $queue2.Enqueue($nested)
        }
    }
}
$sections = @($expanders | ForEach-Object { [string]$_.Tag })
foreach ($tag in @('NetworkDetailedReport','HappSpecialActions')) {
    if ($sections -notcontains $tag) { throw "FAIL: missing expandable network section $tag" }
}
foreach ($field in @('_internetStatus','_dnsStatus','_proxyStatus','_interfacesText','_checksText')) {
    $control = $windowType.GetField($field, [Reflection.BindingFlags]'Instance, NonPublic').GetValue($window)
    if ($null -eq $control -or [String]::IsNullOrWhiteSpace($control.Text)) {
        throw "FAIL: network status '$field' was not initialized"
    }
}
$overview = $type.GetMethod('ActiveInterfacesSummary',
    [Reflection.BindingFlags]'Static, NonPublic').Invoke($null, @())
if ([String]::IsNullOrWhiteSpace([string]$overview)) { throw 'FAIL: active interfaces summary is empty.' }
$reportField = $windowType.GetField('_report', [Reflection.BindingFlags]'Instance, NonPublic').GetValue($window)
if ($null -eq $reportField -or -not [String]::IsNullOrEmpty([string]$reportField.Text)) {
    throw 'FAIL: analyzer report box must start empty before the first refresh.'
}
# Render the real WPF layout without Show() so no Loaded/network probes run.
# This is an offline visual snapshot, not a claim about live network status.
$layout = [System.Windows.Controls.DockPanel]$window.Content
$layout.Background = $window.Background
$size = [System.Windows.Size]::new(746, 592)
$layout.Measure($size)
$layout.Arrange([System.Windows.Rect]::new(0, 0, 746, 592))
$layout.UpdateLayout()
$picture = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
    746, 592, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$picture.Render($layout)
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($picture))
$screenshotDir = Join-Path ([IO.Path]::GetTempPath()) 'opencode\AppShelf-Network-Preview'
[IO.Directory]::CreateDirectory($screenshotDir) | Out-Null
$stream = [IO.File]::Create((Join-Path $screenshotDir 'network-dashboard.png'))
try { $encoder.Save($stream) } finally { $stream.Dispose() }
$window.Close()
Write-Host 'PASS: universal network dashboard, diagnostic sections and guarded Happ actions.'
$passed++
Write-Host ("HappRecovery: {0} scenarios passed; proxy writes and notifications use memory only." -f $passed)
