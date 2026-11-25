# Windows PowerShell 5.1 -STA. Real WPF and child-process restarts; isolated storage.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5 -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-Ui218-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs','PluginWidgetView.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $text = $text.Replace('if (_networkTimer == null) StartMonitors();', '')
            $text = $text.Replace('startupState = WindowsStartup.Read();', 'startupState = new WindowsStartup.State();')
            $text = $text.Replace('            ContextMenu tools = new ContextMenu();',
                '            ContextMenu tools = new ContextMenu(); Ui218Probe.Tools = tools;')
            $errorDialog = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $errorDialog).Count -ne 1) { throw 'Error boundary missing.' }
            $text = [regex]::Replace($text, $errorDialog, 'Ui218Probe.SaveErrors++;')
        }
        if ($file -eq 'AppShelfExtras.cs') {
            $anchor = 'Loaded += async delegate { await RefreshReportAsync(); };'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Diagnostics isolation missing.' }
            $text = $text.Replace($anchor, '')
        }
        $text = $text.Replace('dialog.ShowDialog()', 'Ui218Probe.Modal(dialog)')
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text = $text.Replace('SiteLinks.FetchFavicon(site)', 'false')
        $text.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
    }
    $sources += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Ui218Probe.cs'))
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = Join-Path $temp 'Ui218Probe.exe'
    $options.CompilerOptions = '/main:Ui218Probe'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    & $options.OutputAssembly
    if ($LASTEXITCODE -ne 0) { throw "UI 2.18 checks failed. Isolated data: $temp" }
    Write-Output "PASS UI 2.18 tests. Isolated data: $temp"
} finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $previous }
