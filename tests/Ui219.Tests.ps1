# Windows PowerShell 5.1 -STA. Real WPF, isolated storage, no startup/network changes.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5 -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-Ui219-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $text = $text.Replace('if (_networkTimer == null) StartMonitors();', '')
            $anchor = 'bool? savedSettings = dialog.ShowDialog();'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Settings boundary missing.' }
            $text = $text.Replace($anchor, 'bool? savedSettings = Ui219Probe.Settings(dialog);')
            $text = $text.Replace('startupState = WindowsStartup.Read();', 'startupState = new WindowsStartup.State();')
            $text = $text.Replace('WindowsStartup.State currentRun = WindowsStartup.Read();', 'WindowsStartup.State currentRun = new WindowsStartup.State();')
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text.Replace('SiteLinks.FetchFavicon(site)', 'false')
    }
    $sources += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Ui219Probe.cs'))
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = Join-Path $temp 'Ui219Probe.exe'
    $options.CompilerOptions = '/main:Ui219Probe'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    & $options.OutputAssembly
    if ($LASTEXITCODE -ne 0) { throw "UI 2.19 checks failed. Isolated data: $temp" }
    Write-Output "PASS UI 2.19 tests. Isolated data and images: $temp"
} finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $previous }
