# Windows PowerShell 5.1 -STA, isolated package storage. No plugin execution.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $source = [IO.File]::ReadAllText((Join-Path $root 'PluginPackages.cs'))
    $harness = @'
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
namespace AppShelf {
public static class PluginPackageTests {
    static int count;
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    static void Reject(Action action, string message) {
        try { action(); } catch (InvalidDataException) { count++; return; }
        throw new Exception("Accepted: " + message);
    }
    static string Manifest(string version, string id, int api) {
        return "{\"schemaVersion\":1,\"apiVersion\":" + api + ",\"id\":\"" + id +
            "\",\"name\":\"Hello\",\"version\":\"" + version +
            "\",\"entryPoint\":\"bin/Hello.exe\",\"capabilities\":[\"command\",\"window\"]}";
    }
    static void Add(ZipArchive zip, string name, string value) {
        using (StreamWriter w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(value);
    }
    static string Package(string root, string manifest, string extra, bool includeExe) {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".asplugin");
        using (FileStream stream = File.Create(path))
        using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create)) {
            Add(zip, "plugin.json", manifest);
            if (includeExe) Add(zip, "bin/Hello.exe", "test payload; never executed");
            if (extra != null) Add(zip, extra, "test");
        }
        return path;
    }
    public static void Run() {
        string root = Path.Combine(Path.GetTempPath(), "AppShelf-PluginTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            PluginPackageStore store = new PluginPackageStore(root);
            string v1 = Manifest("1.0.0", "example.hello", 1);
            string v2 = Manifest("1.1.0", "example.hello", 1);
            string initial = Package(root, v1, null, true);
            Assert(store.Install(initial).Id == "example.hello", "install manifest");
            Assert(store.ReadInstalled("example.hello").Version == "1.0.0", "read installed");
            string data = Path.Combine(store.GetDataDirectory("example.hello"), "data.txt");
            File.WriteAllText(data, "keep");
            Reject(delegate { store.Install(initial); }, "same version");
            foreach (string bad in new[] { "../escape.txt", "bin/../escape.txt", "C:/escape.txt",
                "bin/COM1.txt", "bin/evil.txt:stream", "bin/hello.exe", "bin/trailing.",
                "bin\\evil.exe", "data.json", "/bin/evil.exe" }) {
                string invalid = Package(root, v2, bad, true);
                Reject(delegate { store.Install(invalid); }, bad);
            }
            Reject(delegate { store.Install(Package(root, v2, null, false)); }, "missing executable");
            Reject(delegate { store.Install(Package(root, Manifest("1.1.0", "example.hello", 2), null, true)); }, "API");
            Reject(delegate { store.Install(Package(root, Manifest("1.1.0", "../escape", 1), null, true)); }, "id");
            Assert(store.ReadInstalled("example.hello").Version == "1.0.0", "failed update preserved");
            Assert(!File.Exists(Path.Combine(root, "escape.txt")), "no escape");
            Assert(store.Install(Package(root, v2, null, true)).Version == "1.1.0", "update");
            Assert(File.ReadAllText(data) == "keep", "update preserved data");
            store.Remove("example.hello");
            Assert(File.ReadAllText(data) == "keep", "remove preserved data");
            Assert(!Directory.Exists(Path.Combine(root, "Plugins", "Installed", "example.hello")), "code removed");
            Assert(Directory.GetDirectories(Path.Combine(root, "Plugins", "Installed")).Length == 0, "no staging leftovers");
            Console.WriteLine("PASS: " + count + " package checks");
        } finally { Directory.Delete(root, true); }
    }
}}
'@
    $parameters = New-Object System.CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.IO.Compression.dll')) {
        [void]$parameters.ReferencedAssemblies.Add($ref)
    }
    $result = $provider.CompileAssemblyFromSource($parameters, [string[]]@($source,$harness))
    if ($result.Errors.HasErrors) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('AppShelf.PluginPackageTests').GetMethod('Run').Invoke($null,@())
} finally { $provider.Dispose() }
