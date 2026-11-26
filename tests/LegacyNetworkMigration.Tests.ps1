# Windows PowerShell 5.1: pure legacy migration target validation.
# Run after Build-AppShelf.cmd --no-start --no-pause.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'AppShelf.exe'
if (-not [IO.File]::Exists($exe)) { throw 'Build AppShelf.exe before running the tests.' }
$assembly = [Reflection.Assembly]::LoadFrom($exe)
# --- Two replacement network targets: validation must happen before any IO.
$targetType = $assembly.GetType('AppShelf.LegacyNetworkTargets', $true)
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

