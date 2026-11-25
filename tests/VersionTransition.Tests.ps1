# Real released stable EXE and the current compiled beta, in a disposable GitHub-hosted Windows profile.
# Do not run against a developer's profile. Local regression runs skip this test.
param([string]$Worker = '', [string]$Binary = '', [string]$ExpectedProfile = '')
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    if ($Worker) { throw 'Worker may only run in GitHub-hosted CI.' }
    Write-Output 'SKIP: real version transition requires a fresh GitHub-hosted Windows profile.'
    return
}
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$profileRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) 'AppShelf'
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }

if ($Worker) {
    Assert ($profileRoot -eq $ExpectedProfile) 'Worker profile mismatch'
    $assembly = [Reflection.Assembly]::LoadFrom($Binary)
    $store = $assembly.GetType('AppShelf.ConfigStore', $true)
    Assert ($store.GetField('DirectoryPath').GetValue($null) -eq $profileRoot) 'Compiled assembly uses another profile'
    $configType = $assembly.GetType('AppShelf.AppConfig', $true)
    $save = $store.GetMethod('Save', [type[]]@($configType))
    $load = $store.GetMethod('Load')
    function Read-Config {
        $argsForLoad = [object[]]@($null, $false)
        $value = $load.Invoke($null, $argsForLoad)
        Assert ($argsForLoad[1] -eq $true -and $null -eq $argsForLoad[0]) 'ConfigStore.Load failed'
        return $value
    }
    function New-Placement([string]$id, [string]$size) {
        $p = [Activator]::CreateInstance($assembly.GetType('AppShelf.PluginWidgetPlacementConfig', $true))
        $p.PluginId = 'example.stocks'; $p.Version = '1.0.0'
        $p.WidgetId = $id; $p.Title = $id; $p.Size = $size
        return $p
    }
    function New-TypedList($element) {
        $listType = [System.Collections.Generic.List[int]].GetGenericTypeDefinition().MakeGenericType([type[]]@($element))
        return ,([Activator]::CreateInstance($listType))
    }
    if ($Worker -eq 'stable-seed') {
        $c = [Activator]::CreateInstance($configType)
        $c.CloseToTray = $false; $c.ShowNetwork = $false; $c.ShowWeather = $false
        $c.AutomaticBackupsEnabled = $false; $c.RdpKeepAwake = $false
        $c.WeatherCity = 'Stable initial'; $c.GroupingEnabled = $true; $c.GroupLayoutMode = 'free'
        $c.Groups.Add('Transition group')
        $item = [Activator]::CreateInstance($assembly.GetType('AppShelf.AppItem', $true))
        $item.Name = 'Transition shortcut'; $item.Path = Join-Path $env:WINDIR 'notepad.exe'; $item.Group = 'Transition group'
        $c.Items.Add($item)
        $save.Invoke($null, [object[]]@($c))
    } elseif ($Worker -eq 'beta-seed') {
        $c = Read-Config
        Assert ($c.WeatherCity -eq 'Stable initial' -and $c.Items.Count -eq 1 -and $c.Groups.Count -eq 1) 'Upgrade lost common settings'
        $placement = New-Placement 'quotes' 'quarter'
        $placement.Row = 1; $placement.Column = 1
        $group = [Activator]::CreateInstance($assembly.GetType('AppShelf.PluginWidgetGroupConfig', $true))
        $group.Number = 1; $group.Title = 'Transition widgets'; $group.Zone = 'middle-center'
        $group.Widgets = New-TypedList ($placement.GetType())
        $group.Widgets.Add($placement)
        $c.WidgetGroups = New-TypedList ($group.GetType())
        $c.WidgetGroups.Add($group)
        $indicator = New-Placement 'market-demo' 'indicator'
        $c.WidgetIndicators = New-TypedList ($indicator.GetType())
        $c.WidgetIndicators.Add($indicator)
        $c.BackupBackgrounds = $false; $c.BackupExecutable = $false
        $save.Invoke($null, [object[]]@($c))
    } elseif ($Worker -eq 'stable-edit') {
        $c = Read-Config
        Assert ($c.Items.Count -eq 1 -and $c.Items[0].Name -eq 'Transition shortcut') 'Stable could not read beta settings'
        $c.WeatherCity = 'Stable edited'; $c.Items[0].Name = 'Edited in stable'
        $save.Invoke($null, [object[]]@($c))
    } elseif ($Worker -eq 'beta-verify') {
        $c = Read-Config
        Assert ($c.WeatherCity -eq 'Stable edited' -and $c.Items.Count -eq 1 -and $c.Items[0].Name -eq 'Edited in stable') 'Stable edits lost'
        Assert ($c.Groups.Count -eq 1 -and $c.Groups[0] -eq 'Transition group') 'Group lost'
        Assert ($c.WidgetGroups.Count -eq 1 -and $c.WidgetGroups[0].Title -eq 'Transition widgets' -and $c.WidgetGroups[0].Zone -eq 'middle-center') 'Widget group lost'
        $p = $c.WidgetGroups[0].Widgets[0]
        Assert ($p.WidgetId -eq 'quotes' -and $p.Size -eq 'quarter' -and $p.Row -eq 1 -and $p.Column -eq 1) 'Widget placement lost'
        Assert ($c.WidgetIndicators.Count -eq 1 -and $c.WidgetIndicators[0].WidgetId -eq 'market-demo') 'Indicator lost'
        Assert ($c.BackupBackgrounds -eq $false -and $c.BackupExecutable -eq $false) 'Backup policy lost'
    } else { throw "Unknown worker: $Worker" }
    Write-Output "PASS: $Worker using $($assembly.Location)"
    return
}

$root = Split-Path $PSScriptRoot -Parent
$beta = Join-Path $root 'AppShelf.exe'
Assert (Test-Path -LiteralPath $beta -PathType Leaf) 'Current compiled AppShelf.exe is missing'
if ((Get-Item -LiteralPath $beta).VersionInfo.ProductVersion -notmatch '^\d+\.\d+\.\d+-beta\.\d+$') {
    Write-Output 'SKIP: transition regression applies only to beta releases.'
    return
}
# The hosted runner is a disposable Windows VM. Refuse to touch any existing profile,
# even in CI. Other suites use their own isolated temporary roots.
Assert (-not (Test-Path -LiteralPath $profileRoot)) "AppShelf profile already exists: $profileRoot"
Assert (-not (Get-Process -Name AppShelf -ErrorAction SilentlyContinue)) 'AppShelf is already running'
$temp = Join-Path $env:RUNNER_TEMP ('AppShelf-Transition-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$stable = Join-Path $temp 'AppShelf.exe'
$ps = Join-Path $PSHOME 'powershell.exe'
function Run-Worker([string]$phase, [string]$exe) {
    & $ps -NoProfile -STA -ExecutionPolicy Bypass -File $PSCommandPath -Worker $phase -Binary $exe -ExpectedProfile $profileRoot
    if ($LASTEXITCODE -ne 0) { throw "Worker failed: $phase" }
}
function Run-RealApp([string]$exe) {
    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 250
            $process.Refresh()
            if ($process.HasExited) { throw "App exited before main window: $exe" }
        } until (($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.MainWindowTitle -like 'AppShelf*') -or [DateTime]::UtcNow -gt $deadline)
        Assert ($process.MainWindowHandle -ne [IntPtr]::Zero) 'Main window did not appear'
        Start-Sleep -Milliseconds 1500
        Assert ($process.CloseMainWindow()) 'Could not close real app normally'
        Assert ($process.WaitForExit(15000)) 'App did not exit normally (possible modal dialog)'
        Assert ($process.ExitCode -eq 0) "App exited with $($process.ExitCode)"
        $saved = Get-Content -LiteralPath (Join-Path $profileRoot 'apps.json') -Raw | ConvertFrom-Json
        Assert ($saved.windowPlacementSaved -eq $true) 'Real app did not save window placement'
        Write-Output "PASS: real executable opened and closed: $((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion)"
    } finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
try {
    # Pin the exact currently supported stable baseline. Use the published asset,
    # not a rebuild of main or a substitute serializer.
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/mishakov1c/app_shelf/releases/tags/2.23.0' -Headers @{ 'User-Agent' = 'AppShelf-Transition-CI' }
    Assert ($release.tag_name -eq '2.23.0' -and -not $release.prerelease -and -not $release.draft) 'Stable release metadata mismatch'
    $asset = @($release.assets | Where-Object { $_.name -eq 'AppShelf.exe' })
    Assert ($asset.Count -eq 1 -and $asset[0].digest -match '^sha256:[0-9a-fA-F]{64}$') 'Stable EXE or published digest missing'
    Invoke-WebRequest -UseBasicParsing -Uri $asset[0].browser_download_url -OutFile $stable
    Assert ((Get-FileHash -LiteralPath $stable -Algorithm SHA256).Hash -eq $asset[0].digest.Substring(7)) 'Stable executable SHA-256 mismatch'
    Assert ((Get-Item -LiteralPath $stable).VersionInfo.ProductVersion -eq '2.23.0') 'Stable executable version mismatch'
    Run-Worker 'stable-seed' $stable
    Run-RealApp $stable
    Run-Worker 'beta-seed' $beta
    Run-RealApp $beta
    $sidecar = Join-Path $profileRoot 'plugins-settings.json'
    $before = (Get-FileHash -LiteralPath $sidecar).Hash
    Run-Worker 'stable-edit' $stable
    Run-RealApp $stable
    Assert ((Get-FileHash -LiteralPath $sidecar).Hash -eq $before) 'Stable changed beta-only settings file'
    Run-Worker 'beta-verify' $beta
    Run-RealApp $beta
    Run-Worker 'beta-verify' $beta
    Write-Output 'PASS: real stable -> beta -> stable -> beta transition'
} finally {
    # The path was verified absent before this test. Never delete an existing user profile.
    if (Test-Path -LiteralPath $profileRoot) { Remove-Item -LiteralPath $profileRoot -Recurse -Force }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
