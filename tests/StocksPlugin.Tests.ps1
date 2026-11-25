# Windows PowerShell 5.1 -STA. Build and test the actual offline stocks package.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\AppShelf-StocksTest-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    & (Join-Path $root 'samples\StocksPlugin\Build.ps1') -OutputDirectory $temp
    $package = Join-Path $temp 'StocksPlugin-1.0.0.asplugin'
    if (-not (Test-Path -LiteralPath $package)) { throw 'Stocks demo package missing.' }
    $harness = @'
using System;
using System.Diagnostics;
using System.IO;
using AppShelf;

public static class StocksPluginTests
{
    private static void Assert(bool value, string message)
    { if (!value) throw new Exception(message); }

    public static void Run(string folder, string package)
    {
        const string id = "example.stocks";
        const string version = "1.0.0";
        PluginPackageStore store = new PluginPackageStore(folder);
        store.Install(package, delegate { return true; });
        Assert(!store.List()[0].Enabled, "Demo installed enabled");
        int pid = 0;
        using (PluginRuntime runtime = new PluginRuntime(store, 3000))
        {
            runtime.SetEnabledAsync(id, true).GetAwaiter().GetResult();
            Assert(runtime.State(id).Status == "idle", "Widget-only activation not explicit");
            PluginWidgetSnapshot first = runtime.ReadWidgetSnapshotAsync(id, version, true).GetAwaiter().GetResult();
            pid = first.ProcessId;
            Assert(first.Version == version && pid > 0 && first.Widgets.Length == 3, "Invalid demo snapshot");
            PluginWidget quotes = Array.Find(first.Widgets, delegate(PluginWidget w) { return w.Id == "quotes"; });
            Assert(quotes != null && quotes.PreferredSize == "full" && quotes.Layouts.Length == 4 &&
                quotes.ForSize("full").Elements.Length == 6 && quotes.ForSize("quarter").Elements.Length == 1 &&
                quotes.ForSize("half-horizontal").Elements.Length == 3 &&
                quotes.ForSize("half-vertical").Elements.Length == 4, "Incorrect size variants");
            Assert(quotes.Title.Contains("\u0434\u0435\u043c\u043e"), "Simulated values not labeled");
            string before = quotes.ForSize("quarter").Elements[0].Value;
            runtime.InvokeWidgetAsync(id, version, pid, "next").GetAwaiter().GetResult();
            PluginWidgetSnapshot second = runtime.ReadWidgetSnapshotAsync(id, version, false).GetAwaiter().GetResult();
            PluginWidget updated = Array.Find(second.Widgets, delegate(PluginWidget w) { return w.Id == "quotes"; });
            Assert(second.ProcessId == pid && updated.ForSize("quarter").Elements[0].Value != before, "Demo data did not update");
            runtime.SetEnabledAsync(id, false).GetAwaiter().GetResult();
            Assert(runtime.State(id).Status == "idle", "Disable did not stop child");
        }
        try { using (Process child = Process.GetProcessById(pid)) Assert(child.WaitForExit(3000), "Child still running"); }
        catch (ArgumentException) {}
        Console.WriteLine("PASS: real offline stocks demo package, size variants, updates and lifecycle");
    }
}
'@
    $sources = @(
        [IO.File]::ReadAllText((Join-Path $root 'PluginProtocol.cs')),
        [IO.File]::ReadAllText((Join-Path $root 'PluginPackages.cs')),
        [IO.File]::ReadAllText((Join-Path $root 'PluginRuntime.cs')),
        $harness)
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.IO.Compression.dll')) {
        [void]$parameters.ReferencedAssemblies.Add($ref)
    }
    $result = $provider.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('StocksPluginTests').GetMethod('Run').Invoke($null,[object[]]@([string]$temp,[string]$package))
} finally {
    $provider.Dispose()
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
