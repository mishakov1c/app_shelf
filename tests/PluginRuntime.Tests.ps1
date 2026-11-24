# Windows PowerShell 5.1 -STA. Real trusted test EXE, isolated profile, no network.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\AppShelf-Runtime-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $protocol = [IO.File]::ReadAllText((Join-Path $root 'PluginProtocol.cs'))
    $helper = @'
using System;
using System.IO;
using System.Threading;
using AppShelf;
public static class FakePlugin {
    public static void Main() {
        try { Run(); }
        catch(Exception error) {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-error.txt"),error.ToString());
            Environment.Exit(14);
        }
    }
    static void Run() {
        Stream input=Console.OpenStandardInput(), output=Console.OpenStandardOutput();
        string data=null;
        while (true) {
            PluginRequest request=PluginFrame.Read<PluginRequest>(input);
            PluginResponse reply=new PluginResponse { ApiVersion=1,Id=request.Id,Ok=true };
            string method=request.Method;
            if (method=="hello") {
                data=request.DataDirectory;
                File.WriteAllText(Path.Combine(data,"pid"),System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                File.WriteAllText(Path.Combine(data,"hello"),"yes");
                // More than a pipe buffer: host must drain stderr independently.
                Console.Error.Write(new string('x',65536)); Console.Error.Flush();
                reply.PluginId=request.PluginId; reply.PluginVersion=request.PluginVersion;
                if(request.PluginId=="test.foreground") reply.Commands=new[]{new PluginCommand{Id="open",Label="Open"},new PluginCommand{Id="decline",Label="Decline"}};
                if (File.Exists(Path.Combine(data,"mode")) && File.ReadAllText(Path.Combine(data,"mode"))=="hello-hang")
                    Thread.Sleep(Timeout.Infinite);
            } else if(method=="invoke") {
                if(request.CommandId=="decline") { reply.Ok=false; reply.Error="declined"; }
                else File.WriteAllText(Path.Combine(data,"invoked"),request.CommandId);
            } else if(method=="settings") {
                File.WriteAllText(Path.Combine(data,"settings"),"opened");
            } else if(method=="poll") {
                reply.Notifications=new[]{new PluginNotice{Title="Notice",Message="First"},
                    new PluginNotice{Title="Excess",Message="Second"}};
            } else if (method=="startBackground") {
                File.WriteAllText(Path.Combine(data,"started"),"yes");
            } else if (method=="ping") {
                string mode=File.Exists(Path.Combine(data,"mode")) ? File.ReadAllText(Path.Combine(data,"mode")) : "";
                if(mode=="crash") Environment.Exit(13);
                if(mode=="hang") Thread.Sleep(Timeout.Infinite);
            } else if (method=="shutdown") {
                File.WriteAllText(Path.Combine(data,"shutdown"),"yes");
                PluginFrame.Write(output,reply); return;
            }
            PluginFrame.Write(output,reply);
        }
    }
}
'@
    $refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.IO.Compression.dll')
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = Join-Path $temp 'FakePlugin.exe'
    $options.CompilerOptions = '/main:FakePlugin'
    $options.TreatWarningsAsErrors = $true
    foreach ($ref in $refs) { [void]$options.ReferencedAssemblies.Add($ref) }
    $result = $provider.CompileAssemblyFromSource($options,[string[]]@($protocol,$helper))
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $harness = @'
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using AppShelf;
public static class PluginRuntimeTests {
    static int count;
    static void Assert(bool ok,string name) { if(!ok) throw new Exception(name); count++; }
    static string Package(string root,string exe,string id,string version,bool background) {
        string filename=Path.Combine(root,Guid.NewGuid().ToString("N")+".asplugin");
        using(FileStream s=File.Create(filename))
        using(ZipArchive zip=new ZipArchive(s,ZipArchiveMode.Create)) {
            using(StreamWriter w=new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                w.Write("{\"schemaVersion\":1,\"apiVersion\":1,\"id\":\""+id+"\",\"name\":\"Test\",\"version\":\""+version+
                    "\",\"entryPoint\":\"bin/FakePlugin.exe\",\"capabilities\":["+(background?"\"background\",\"notification\"":"\"window\",\"settings\"")+"]}");
            using(Stream target=zip.CreateEntry("bin/FakePlugin.exe").Open())
            using(FileStream source=File.OpenRead(exe)) source.CopyTo(target);
        }
        return filename;
    }
    static int Pid(string data) { return Int32.Parse(File.ReadAllText(Path.Combine(data,"pid"))); }
    static bool Exited(int pid) {
        try { using(Process p=Process.GetProcessById(pid)) return p.WaitForExit(3000); }
        catch(ArgumentException) { return true; }
    }
    static void State(PluginRuntime runtime,string id,string expected) {
        PluginRuntimeState state=runtime.State(id);
        Assert(state.Status==expected,id+" expected "+expected+" got "+state.Status+": "+state.Error);
    }
    public static void Run(string root,string exe) {
        PluginPackageStore store=new PluginPackageStore(root);
        string bg="test.background", fg="test.foreground";
        string bg1=Package(root,exe,bg,"1.0.0",true), bg2=Package(root,exe,bg,"2.0.0",true);
        string fg1=Package(root,exe,fg,"1.0.0",false);
        store.Install(bg1,delegate { return true; });
        store.Install(fg1,delegate { return true; });
        string data=store.GetDataDirectory(bg), fgData=store.GetDataDirectory(fg);
        store.SetEnabled(bg,true); store.SetEnabled(fg,true);
        int last=0, foreground=0;
        using(PluginRuntime runtime=new PluginRuntime(store,1500)) {
            runtime.StartBackgroundsAsync().GetAwaiter().GetResult();
            if(runtime.State(bg).Status!="running") {
                string diag=Path.Combine(root,"Plugins","Installed",bg,"bin","fake-error.txt");
                if(File.Exists(diag)) Console.WriteLine(File.ReadAllText(diag));
            }
            State(runtime,bg,"running"); State(runtime,fg,"idle");
            Assert(File.Exists(Path.Combine(data,"started")),"background starts");
            Assert(!File.Exists(Path.Combine(fgData,"pid")),"foreground remains idle");
            int first=Pid(data);
            runtime.StartAsync(bg).GetAwaiter().GetResult();
            Assert(Pid(data)==first,"idempotent start");
            var notices=runtime.CheckHealthAsync().GetAwaiter().GetResult(); State(runtime,bg,"running");
            Assert(notices.Count==1 && notices[0].Id==bg && notices[0].Notice.Message=="First","notification accepted and excess bounded");
            Assert(runtime.CheckHealthAsync().GetAwaiter().GetResult().Count==0,"notification rate limited");
            runtime.StartAsync(bg,true).GetAwaiter().GetResult();
            int second=Pid(data);
            Assert(first!=second && Exited(first),"restart replaced process");
            Assert(File.Exists(Path.Combine(data,"shutdown")),"graceful shutdown");
            Assert(store.Install(bg2,delegate { return false; },bg,runtime.BeforePackageCommit)==null,"cancel update");
            Assert(Pid(data)==second && runtime.State(bg).Status=="running","cancel kept process");
            store.Install(bg2,delegate { return true; },bg,runtime.BeforePackageCommit);
            Assert(Exited(second),"update stopped old process");
            State(runtime,bg,"idle");
            Assert(store.ReadInstalled(bg).Version=="2.0.0","new version");
            Assert(!store.List().Find(delegate(InstalledPlugin p){return p.Id==bg;}).Enabled,"update disabled");
            Assert(File.Exists(Path.Combine(data,"started")),"update kept data");
            runtime.SetEnabledAsync(bg,true).GetAwaiter().GetResult(); State(runtime,bg,"running");
            int third=Pid(data);
            File.WriteAllText(Path.Combine(data,"mode"),"crash");
            runtime.CheckHealthAsync().GetAwaiter().GetResult(); State(runtime,bg,"error");
            Assert(Exited(third),"crash process exited");
            runtime.StartBackgroundsAsync().GetAwaiter().GetResult();
            Assert(Pid(data)==third,"no automatic retry");
            File.Delete(Path.Combine(data,"mode"));
            runtime.StartAsync(bg,true).GetAwaiter().GetResult(); State(runtime,bg,"running");
            int fourth=Pid(data);
            File.WriteAllText(Path.Combine(data,"mode"),"hang");
            Stopwatch clock=Stopwatch.StartNew();
            runtime.CheckHealthAsync().GetAwaiter().GetResult();
            State(runtime,bg,"error"); Assert(Exited(fourth),"hung process terminated");
            Assert(clock.ElapsedMilliseconds<6500,"bounded ping");
            File.Delete(Path.Combine(data,"mode"));
            runtime.StartAsync(bg,true).GetAwaiter().GetResult();
            int fifth=Pid(data);
            runtime.SetEnabledAsync(bg,false).GetAwaiter().GetResult();
            Assert(Exited(fifth),"disable stopped process"); State(runtime,bg,"idle");
            File.WriteAllText(Path.Combine(data,"mode"),"hello-hang");
            try { runtime.SetEnabledAsync(bg,true).GetAwaiter().GetResult(); throw new Exception("hung hello accepted"); }
            catch(TimeoutException) { count++; }
            State(runtime,bg,"error"); Assert(Exited(Pid(data)),"hung hello terminated");
            File.Delete(Path.Combine(data,"mode"));
            runtime.StartAsync(bg,true).GetAwaiter().GetResult(); last=Pid(data);
            try { runtime.InvokeAsync(fg,"9.0.0","open").GetAwaiter().GetResult(); throw new Exception("stale version accepted"); }
            catch(InvalidOperationException) { count++; }
            Assert(!File.Exists(Path.Combine(fgData,"pid")),"stale action did not launch");
            PluginCommand[] commands=runtime.GetCommandsAsync(fg,"1.0.0").GetAwaiter().GetResult();
            Assert(commands.Length==2 && commands[0].Id=="open","commands discovered on demand");
            State(runtime,fg,"running");
            runtime.InvokeAsync(fg,"1.0.0","open").GetAwaiter().GetResult();
            Assert(File.ReadAllText(Path.Combine(fgData,"invoked"))=="open","command invoked");
            runtime.OpenSettingsAsync(fg,"1.0.0").GetAwaiter().GetResult();
            Assert(File.ReadAllText(Path.Combine(fgData,"settings"))=="opened","settings invoked");
            try { runtime.InvokeAsync(fg,"1.0.0","decline").GetAwaiter().GetResult(); throw new Exception("remote error accepted"); }
            catch(PluginRemoteException) { count++; }
            State(runtime,fg,"running");
            try { runtime.InvokeAsync(fg,"1.0.0","unknown").GetAwaiter().GetResult(); throw new Exception("unknown command accepted"); }
            catch(InvalidOperationException) { count++; }
            Assert(File.ReadAllText(Path.Combine(fgData,"invoked"))=="open","unknown command not invoked");
            foreground=Pid(fgData);
            runtime.RemoveAsync(fg).GetAwaiter().GetResult();
            Assert(Exited(foreground) && File.Exists(Path.Combine(fgData,"hello")),"remove stopped process and kept data");
            store.Install(fg1,delegate {return true;},null,runtime.BeforePackageCommit);
            runtime.SetEnabledAsync(fg,true).GetAwaiter().GetResult(); State(runtime,fg,"idle");
            runtime.StartAsync(fg).GetAwaiter().GetResult(); foreground=Pid(fgData);
        }
        Assert(Exited(last) && Exited(foreground),"host shutdown closed both processes");
        Console.WriteLine("PASS: "+count+" real plugin lifecycle checks");
    }
}
'@
    $sources = @($protocol,
        [IO.File]::ReadAllText((Join-Path $root 'PluginPackages.cs')),
        [IO.File]::ReadAllText((Join-Path $root 'PluginRuntime.cs')),
        $harness)
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($ref in $refs) { [void]$parameters.ReferencedAssemblies.Add($ref) }
    $result = $provider.CompileAssemblyFromSource($parameters,[string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('PluginRuntimeTests').GetMethod('Run').Invoke($null,[object[]]@([string]$temp,[string]$options.OutputAssembly))
} finally {
    $provider.Dispose()
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
