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
    static PluginManifest Install(PluginPackageStore store, string package) { return store.Install(package, delegate { return true; }); }
    static void Recovery(string root, string scenario) {
        string profile=Path.Combine(root,scenario);
        Directory.CreateDirectory(profile);
        PluginPackageStore store=new PluginPackageStore(profile);
        string id="example.recovery", token=Guid.NewGuid().ToString("N");
        string installed=Path.Combine(profile,"Plugins","Installed");
        string target=Path.Combine(installed,id);
        string previous=Path.Combine(installed,".previous-"+token);
        string stage=Path.Combine(installed,".stage-"+token);
        string journal=Path.Combine(installed,".update-"+id+".json");
        Install(store,Package(root,Manifest("1.0.0",id,1),null,true));
        store.SetEnabled(id,true);
        string data=Path.Combine(store.GetDataDirectory(id),"keep.txt");
        File.WriteAllText(data,"keep");
        // Create valid new package using an independent profile, then stage its code.
        PluginPackageStore other=new PluginPackageStore(Path.Combine(profile,"other"));
        Install(other,Package(root,Manifest("1.1.0",id,1),null,true));
        Directory.Move(Path.Combine(profile,"other","Plugins","Installed",id),stage);
        File.WriteAllText(journal,"{\"id\":\""+id+"\",\"token\":\""+token+
            "\",\"oldVersion\":\"1.0.0\",\"newVersion\":\"1.1.0\"}",new UTF8Encoding(false));
        if(scenario!="before") Directory.Move(target,previous);
        if(scenario=="after" || scenario=="cleanup") Directory.Move(stage,target);
        if(scenario=="cleanup") File.Delete(Path.Combine(previous,"plugin.json")); // interrupted old tree deletion
        if(scenario=="missing") Directory.Delete(previous,true);
        if(scenario=="badjournal") File.WriteAllText(journal,"{\"id\":\""+id+"\",\"token\":\"../escape\"}");
        var rows=new PluginPackageStore(profile).List();
        Assert(rows.Count==1,"recovery list count "+scenario);
        if(scenario=="missing" || scenario=="badjournal") {
            Assert(rows[0].Error!=null && !rows[0].Enabled,"unresolved recovery disabled "+scenario);
            Assert(File.Exists(journal)&&Directory.Exists(stage),"unresolved files preserved "+scenario);
            Reject(delegate { string exe; store.ReadEnabled(id,out exe); },"unresolved cannot execute "+scenario);
            Reject(delegate { store.Remove(id); },"unresolved cannot remove "+scenario);
        } else {
            bool committed=scenario=="after" || scenario=="cleanup";
            Assert(rows[0].Error==null && rows[0].Manifest.Version==(committed?"1.1.0":"1.0.0"),"recovered version "+scenario);
            Assert(rows[0].Enabled==!committed,"recovered enabled state "+scenario);
            Assert(!File.Exists(journal)&&!Directory.Exists(stage)&&!Directory.Exists(previous),"cleaned transaction "+scenario);
            Assert(new PluginPackageStore(profile).List()[0].Manifest.Version==rows[0].Manifest.Version,"idempotent recovery "+scenario);
        }
        Assert(File.ReadAllText(data)=="keep","recovery preserved user data "+scenario);
    }
    static void BackupChecks(string root) {
        string profile=Path.Combine(root,"backup-profile"), safety=Path.Combine(root,"backup-safety");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(safety);
        PluginPackageStore store=new PluginPackageStore(profile);
        string id="example.backup";
        string m=Manifest("1.0.0",id,1).Replace("\"capabilities\"", "\"backupFiles\":[\"settings.json\",\"sub/state.dat\"],\"capabilities\"");
        string package=Package(root,m,null,true);
        Install(store,package);
        string data=store.GetDataDirectory(id);
        string settings=Path.Combine(data,"settings.json"), privateFile=Path.Combine(data,"history.txt");
        File.WriteAllText(settings,"settings-old");
        File.WriteAllText(privateFile,"private-history");
        Directory.CreateDirectory(Path.Combine(data,"sub"));
        File.WriteAllText(Path.Combine(data,"sub","state.dat"),"state-old");
        store.SetEnabled(id,true);
        byte[] first=store.CaptureBackup();
        string inventory=Encoding.UTF8.GetString(first);
        Assert(inventory.Contains(id)&&inventory.Contains("\"dataIncluded\":false"),"inventory without data by default");
        Assert(!inventory.Contains("settings.json")&&!inventory.Contains("history.txt"),"no private file names by default");
        store.SetBackupDataEnabled(id,true);
        Assert(store.BackupDataEnabled(id),"explicit consent");
        byte[] snapshot=store.CaptureBackup();
        string json=Encoding.UTF8.GetString(snapshot);
        Assert(json.Contains("settings.json")&&json.Contains("state.dat")&&!json.Contains("history.txt"),"only allowlisted data");
        Assert(Convert.ToBase64String(snapshot)==Convert.ToBase64String(store.CaptureBackup()),"stable capture");
        using(MemoryStream input=new MemoryStream(snapshot))
            Assert(Convert.ToBase64String(PluginPackageStore.ReadBackupBytes(input))==Convert.ToBase64String(snapshot),"validated backup");
        File.WriteAllBytes(store.RecoveredPath(),snapshot);
        var recovered=store.ReadRecovered();
        Assert(recovered.Count==1&&recovered[0].Files.Count==2&&recovered[0].Enabled,"recovery inventory");
        File.WriteAllText(settings,"settings-current");
        File.WriteAllText(privateFile,"private-current");
        Assert(File.ReadAllText(settings)=="settings-current","import never changes live data");
        Reject(delegate { store.ApplyRecoveredData(id,"2.0.0",safety); },"wrong restore version");
        store.ApplyRecoveredData(id,"1.0.0",safety);
        Assert(File.ReadAllText(settings)=="settings-old","settings restored");
        Assert(File.ReadAllText(privateFile)=="private-current","undeclared private data untouched");
        Assert(!store.List()[0].Enabled,"restore leaves plugin disabled");
        string[] safetyFiles=Directory.GetFiles(safety,"settings.json",SearchOption.AllDirectories);
        Assert(safetyFiles.Length==1&&File.ReadAllText(safetyFiles[0])=="settings-current","previous data preserved");
        string invalid=json.Replace("settings.json","../escape");
        Reject(delegate { using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(invalid))) PluginPackageStore.ReadBackupBytes(stream); },"unsafe backup path");
        string extra=json.Replace("settings.json","history.txt");
        File.WriteAllText(store.RecoveredPath(),extra,new UTF8Encoding(false));
        Reject(delegate { store.ApplyRecoveredData(id,"1.0.0",safety); },"undeclared restoration rejected");
        Assert(File.ReadAllText(privateFile)=="private-current","undeclared restoration preserved data");
        string m2=m.Replace("1.0.0","1.1.0");
        string update=Package(root,m2,null,true);
        Reject(delegate { store.Install(update,delegate {return true;},null,null,null,true); },"update requires backup root");
        Assert(store.ReadInstalled(id).Version=="1.0.0","blocked update preserves version");
        store.Install(update,delegate {return true;},null,null,safety,true);
        string[] oldPackages=Directory.GetFiles(Path.Combine(safety,"BeforePluginUpdate"),"*.asplugin");
        Assert(oldPackages.Length==1,"previous package saved");
        PluginPackageStore previous=new PluginPackageStore(Path.Combine(profile,"previous-test"));
        Assert(previous.Install(oldPackages[0],delegate{return true;}).Version=="1.0.0","previous package reinstallable");
        Assert(!store.BackupDataEnabled(id),"new version needs consent");
        Assert(!Encoding.UTF8.GetString(store.CaptureBackup()).Contains("settings.json"),"new version no data by default");
        store.SetBackupDataEnabled(id,true);
        File.WriteAllBytes(settings,new byte[4*1024*1024+1]);
        Reject(delegate { store.CaptureBackup(); },"data size limit");
        store.SetBackupDataEnabled(id,false);
        Assert(store.CaptureBackup()!=null,"oversized unselected file not read");
        store.SetBackupDataEnabled(id,true);
        store.Remove(id);
        Install(store,Package(root,m2,null,true));
        Assert(!store.BackupDataEnabled(id),"reinstall requires consent");
        string unsafeManifest=Manifest("1.0.0","example.unsafe",1).Replace("\"capabilities\"","\"backupFiles\":[\"../bad\"],\"capabilities\"");
        Reject(delegate { Install(store,Package(root,unsafeManifest,null,true)); },"unsafe manifest backup path");
    }
    public static void Run() {
        string root = Path.Combine(Path.GetTempPath(), "AppShelf-PluginTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            PluginPackageStore store = new PluginPackageStore(root);
            string v1 = Manifest("1.0.0", "example.hello", 1);
            string v2 = Manifest("1.1.0", "example.hello", 1);
            string initial = Package(root, v1, null, true);
            Assert(Install(store, initial).Id == "example.hello", "install manifest");
            Assert(store.ReadInstalled("example.hello").Version == "1.0.0", "read installed");
            Assert(store.List().Count == 1 && !store.List()[0].Enabled, "default disabled");
            store.SetEnabled("example.hello", true);
            Assert(new PluginPackageStore(root).List()[0].Enabled, "enabled state persisted");
            Assert(store.Install(Package(root, v2, null, true), delegate { return false; }) == null, "cancelled update");
            Assert(store.List()[0].Enabled && store.ReadInstalled("example.hello").Version == "1.0.0", "cancel leaves state");
            Reject(delegate { store.Install(Package(root, v2, null, true), delegate { return true; }, "example.other"); }, "wrong update id");
            store.SetEnabled("example.hello", false);
            Assert(!store.List()[0].Enabled, "disabled state persisted");
            store.SetEnabled("example.hello", true);
            string data = Path.Combine(store.GetDataDirectory("example.hello"), "data.txt");
            File.WriteAllText(data, "keep");
            Reject(delegate { Install(store, initial); }, "same version");
            foreach (string bad in new[] { "../escape.txt", "bin/../escape.txt", "C:/escape.txt",
                "bin/COM1.txt", "bin/evil.txt:stream", "bin/hello.exe", "bin/trailing.",
                "bin\\evil.exe", "data.json", "/bin/evil.exe" }) {
                string invalid = Package(root, v2, bad, true);
                Reject(delegate { Install(store, invalid); }, bad);
            }
            Reject(delegate { Install(store, Package(root, v2, null, false)); }, "missing executable");
            Reject(delegate { Install(store, Package(root, Manifest("1.1.0", "example.hello", 2), null, true)); }, "API");
            Reject(delegate { Install(store, Package(root, Manifest("1.1.0", "../escape", 1), null, true)); }, "id");
            Assert(store.ReadInstalled("example.hello").Version == "1.0.0", "failed update preserved");
            Assert(!File.Exists(Path.Combine(root, "escape.txt")), "no escape");
            Assert(Install(store, Package(root, v2, null, true)).Version == "1.1.0", "update");
            Assert(File.ReadAllText(data) == "keep", "update preserved data");
            Assert(!store.List()[0].Enabled, "new version disabled");
            store.Remove("example.hello");
            Assert(File.ReadAllText(data) == "keep", "remove preserved data");
            Assert(!Directory.Exists(Path.Combine(root, "Plugins", "Installed", "example.hello")), "code removed");
            Assert(Directory.GetDirectories(Path.Combine(root, "Plugins", "Installed")).Length == 0, "no staging leftovers");
            foreach(string scenario in new[]{"before","between","after","cleanup","missing","badjournal"}) Recovery(root,scenario);
            BackupChecks(root);
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
