# Windows PowerShell 5.1 -STA. Protocol codec/session; no external processes or network.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $source = [IO.File]::ReadAllText((Join-Path $root 'PluginProtocol.cs'))
    $harness = @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace AppShelf {
public static class PluginProtocolTests {
    static int count;
    static void Assert(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
    static void Reject<T>(Action action, string name) where T : Exception {
        try { action(); } catch (T) { count++; return; }
        throw new Exception("Accepted: " + name);
    }
    static PluginResponse Hello(int id = 1) {
        return new PluginResponse { ApiVersion=1, Id=id, Ok=true, PluginId="example.hello",
            PluginVersion="1.0.0", Commands=new[] { new PluginCommand { Id="open", Label="Открыть" } } };
    }
    static PluginResponse Ok(int id) { return new PluginResponse { ApiVersion=1, Id=id, Ok=true }; }
    static MemoryStream Frames(params PluginResponse[] responses) {
        MemoryStream s = new MemoryStream();
        foreach (PluginResponse reply in responses) PluginFrame.Write(s, reply);
        s.Position=0; return s;
    }
    static PluginProtocolSession Session(Stream send, Stream receive, Action abort, params string[] caps) {
        return new PluginProtocolSession(send, receive, abort, "example.hello", "1.0.0", caps);
    }
    static byte[] Raw(byte[] bytes) {
        byte[] result = new byte[bytes.Length+4];
        int n=bytes.Length; result[0]=(byte)n; result[1]=(byte)(n>>8);
        result[2]=(byte)(n>>16); result[3]=(byte)(n>>24);
        Array.Copy(bytes,0,result,4,n); return result;
    }
    sealed class BlockingRead : MemoryStream {
        internal readonly ManualResetEvent Released = new ManualResetEvent(false);
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
        public override int Read(byte[] buffer, int offset, int count) {
            Entered.Set(); Released.WaitOne(5000); return 0;
        }
    }
    static PluginWidget[] Widgets() {
        return new[] { new PluginWidget { Id="quotes", Title="Курсы акций", Kind="card",
            Elements=new[] {
                new PluginWidgetElement { Type="text", Label="Демонстрационные данные" },
                new PluginWidgetElement { Type="metric", Label="AAPL", Value="$200.00" },
                new PluginWidgetElement { Type="progress", Label="CPU", Percent=42 },
                new PluginWidgetElement { Type="action", Label="Открыть", CommandId="open" }
            } } };
    }
    static void WidgetChecks() {
        CancellationToken none=CancellationToken.None;
        PluginResponse snapshot=Ok(2); snapshot.Widgets=Widgets();
        using (MemoryStream send=new MemoryStream()) {
            PluginProtocolSession p=Session(send,Frames(Hello(),snapshot,Ok(3)),delegate {}, "window","widget");
            p.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult();
            PluginWidget[] widgets=p.GetWidgetsAsync(1000,none).GetAwaiter().GetResult().Widgets;
            Assert(widgets.Length==1 && widgets[0].Elements[1].Value=="$200.00" &&
                widgets[0].Elements[2].Percent==42 && widgets[0].Elements[3].CommandId=="open","widget snapshot roundtrip");
            p.InvokeAsync(widgets[0].Elements[3].CommandId,1000,none).GetAwaiter().GetResult();
            send.Position=0; PluginFrame.Read<PluginRequest>(send);
            Assert(PluginFrame.Read<PluginRequest>(send).Method=="getWidgets","widget request");
            Assert(PluginFrame.Read<PluginRequest>(send).CommandId=="open","widget action uses invoke");
        }
        PluginResponse sized=Ok(2); sized.Widgets=Widgets();
        PluginWidget sizedWidget=sized.Widgets[0];
        sizedWidget.DefaultSize="half-horizontal";
        sizedWidget.Layouts=new[] {
            new PluginWidgetLayout { Size="half-horizontal", Elements=sizedWidget.Elements },
            new PluginWidgetLayout { Size="quarter", Elements=new[] {
                new PluginWidgetElement { Type="metric", Label="AAPL", Value="compact" } } }
        };
        PluginProtocolSession sizedSession=Session(new MemoryStream(),Frames(Hello(),sized),delegate {},"window","widget");
        sizedSession.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult();
        PluginWidget parsed=sizedSession.GetWidgetsAsync(1000,none).GetAwaiter().GetResult().Widgets[0];
        Assert(parsed.PreferredSize=="half-horizontal" && parsed.SupportsSize("quarter") &&
            !parsed.SupportsSize("full") && parsed.ForSize("quarter").Elements[0].Value=="compact",
            "per-size widget layouts roundtrip");
        Assert(Widgets()[0].PreferredSize=="full" && Widgets()[0].SupportsSize("quarter"),
            "legacy card supports default group sizes");
        PluginProtocolSession old=Session(new MemoryStream(),Frames(Hello(),Ok(2)),delegate {}, "window");
        old.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult();
        Reject<InvalidOperationException>(delegate { old.GetWidgetsAsync(1000,none).GetAwaiter().GetResult(); },"undeclared widgets");
        old.PingAsync(1000,none).GetAwaiter().GetResult();
        Assert(!old.Failed,"old session unaffected");
        foreach (string reason in new[] {"missing","duplicate","kind","type","label","percent","action","fields","count","indicator","unexpected","error","size","size-duplicate","size-default","size-empty","size-action","indicator-layout"}) {
            PluginResponse bad=Ok(2); bad.Widgets=Widgets();
            if(reason=="missing") bad.Widgets=null;
            if(reason=="duplicate") bad.Widgets=new[]{bad.Widgets[0],bad.Widgets[0]};
            if(reason=="kind") bad.Widgets[0].Kind="native";
            if(reason=="type") bad.Widgets[0].Elements[0].Type="html";
            if(reason=="label") bad.Widgets[0].Elements[0].Label=new string('x',241);
            if(reason=="percent") bad.Widgets[0].Elements[2].Percent=101;
            if(reason=="action") bad.Widgets[0].Elements[3].CommandId="unknown";
            if(reason=="fields") bad.Widgets[0].Elements[0].Value="unexpected";
            if(reason=="count") bad.Widgets[0].Elements=new PluginWidgetElement[13];
            if(reason=="indicator") bad.Widgets[0].Kind="indicator";
            if(reason=="error") { bad.Ok=false; bad.Error="declined"; }
            if(reason.StartsWith("size") || reason=="indicator-layout") {
                PluginWidget w=bad.Widgets[0];
                w.DefaultSize="quarter";
                w.Layouts=new[] { new PluginWidgetLayout { Size="quarter", Elements=w.Elements } };
                if(reason=="size") w.Layouts[0].Size="unknown";
                if(reason=="size-duplicate") w.Layouts=new[]{w.Layouts[0],w.Layouts[0]};
                if(reason=="size-default") w.DefaultSize="full";
                if(reason=="size-empty") w.Layouts[0].Elements=new PluginWidgetElement[0];
                if(reason=="size-action") w.Layouts[0].Elements=new[]{
                    new PluginWidgetElement { Type="action", Label="Bad", CommandId="unknown" } };
                if(reason=="indicator-layout") { w.Kind="indicator"; w.Elements=new[]{w.Elements[0]}; }
            }
            int abort=0;
            PluginProtocolSession p=Session(new MemoryStream(),Frames(Hello(),bad),delegate { abort++; },"window","widget");
            p.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult();
            Reject<InvalidDataException>(delegate {
                if(reason=="unexpected") p.PingAsync(1000,none).GetAwaiter().GetResult();
                else p.GetWidgetsAsync(1000,none).GetAwaiter().GetResult();
            },"widget "+reason);
            Assert(p.Failed && abort==1,"invalid widget abort "+reason);
        }
        PluginResponse empty=Ok(2); empty.Widgets=new PluginWidget[0];
        PluginProtocolSession noneAvailable=Session(new MemoryStream(),Frames(Hello(),empty),delegate {},"window","widget");
        noneAvailable.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult();
        Assert(noneAvailable.GetWidgetsAsync(1000,none).GetAwaiter().GetResult().Widgets.Length==0,"empty snapshot");
    }
    public static void Run() {
        WidgetChecks();
        CancellationToken none=CancellationToken.None;
        using (MemoryStream s=Frames(Hello())) {
            Assert(s.ReadByte()>0 && s.Length<65540,"little endian header");
            s.Position=0; PluginResponse hello=PluginFrame.Read<PluginResponse>(s);
            Assert(hello.Commands[0].Label=="Открыть","UTF8 command");
        }
        Reject<InvalidDataException>(delegate { PluginFrame.Read<PluginResponse>(new MemoryStream(new byte[]{0,0,0,0})); },"zero length");
        Reject<InvalidDataException>(delegate { PluginFrame.Read<PluginResponse>(new MemoryStream(new byte[]{1,0,1,0})); },"oversized frame");
        Reject<EndOfStreamException>(delegate { PluginFrame.Read<PluginResponse>(new MemoryStream(new byte[]{1,0})); },"short header");
        Reject<EndOfStreamException>(delegate { PluginFrame.Read<PluginResponse>(new MemoryStream(new byte[]{10,0,0,0,123})); },"short payload");
        Reject<InvalidDataException>(delegate { PluginFrame.Read<PluginResponse>(new MemoryStream(Raw(new byte[]{123,255,125}))); },"invalid UTF8");
        Reject<InvalidDataException>(delegate { PluginFrame.Read<PluginResponse>(new MemoryStream(Raw(Encoding.UTF8.GetBytes("[]")))); },"not object");
        Reject<InvalidDataException>(delegate { PluginFrame.Read<PluginResponse>(new MemoryStream(Raw(Encoding.UTF8.GetBytes("{\"id\":1}")))); },"required members");
        Reject<InvalidDataException>(delegate { PluginFrame.Write(new MemoryStream(), new PluginResponse { Error=new string('a',65536) }); },"outgoing limit");

        int aborts=0;
        PluginResponse notices=Ok(5); notices.Notifications=new[] { new PluginNotice { Title="Title", Message="Message" } };
        using (MemoryStream send=new MemoryStream())
        using (MemoryStream receive=Frames(Hello(),Ok(2),Ok(3),Ok(4),notices,Ok(6))) {
            PluginProtocolSession p=Session(send,receive,delegate { aborts++; },"window","settings","background","notification");
            Reject<InvalidOperationException>(delegate { p.InvokeAsync("open",1000,none).GetAwaiter().GetResult(); },"no hello");
            Assert(p.HelloAsync("ru-RU",@"C:\PluginData",1000,none).GetAwaiter().GetResult().Commands.Length==1,"hello");
            Reject<InvalidOperationException>(delegate { p.InvokeAsync("unknown",1000,none).GetAwaiter().GetResult(); },"unknown command");
            p.InvokeAsync("open",1000,none).GetAwaiter().GetResult();
            p.SettingsAsync(1000,none).GetAwaiter().GetResult();
            p.StartBackgroundAsync(1000,none).GetAwaiter().GetResult();
            Reject<InvalidOperationException>(delegate { p.StartBackgroundAsync(1000,none).GetAwaiter().GetResult(); },"duplicate background");
            Assert(p.PollAsync(1000,none).GetAwaiter().GetResult().Notifications.Length==1,"poll");
            p.ShutdownAsync(1000,none).GetAwaiter().GetResult();
            Reject<InvalidOperationException>(delegate { p.PollAsync(1000,none).GetAwaiter().GetResult(); },"closed");
            Assert(!p.Failed && aborts==0,"normal shutdown");
            send.Position=0;
            PluginRequest first=PluginFrame.Read<PluginRequest>(send);
            Assert(first.Method=="hello" && first.Id==1 && first.Locale=="ru-RU" && first.DataDirectory==@"C:\PluginData","hello request");
            PluginRequest second=PluginFrame.Read<PluginRequest>(send);
            Assert(second.Method=="invoke" && second.CommandId=="open" && second.Id==2,"invoke request");
        }
        foreach (string reason in new[] {"id","api","identity","duplicate","undeclared"}) {
            PluginResponse bad=Hello();
            if(reason=="id") bad.Id=2;
            if(reason=="api") bad.ApiVersion=2;
            if(reason=="identity") bad.PluginId="other.id";
            if(reason=="duplicate") bad.Commands=new[]{bad.Commands[0],bad.Commands[0]};
            int calls=0;
            PluginProtocolSession p=Session(new MemoryStream(),Frames(bad),delegate { calls++; },
                reason=="undeclared" ? "settings" : "window");
            Reject<InvalidDataException>(delegate { p.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult(); },reason);
            Assert(p.Failed && calls==1,"abort "+reason);
            Reject<InvalidOperationException>(delegate { p.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult(); },"no reuse "+reason);
        }
        PluginResponse err=new PluginResponse { ApiVersion=1,Id=2,Ok=false,Error="declined" };
        PluginProtocolSession remote=Session(new MemoryStream(),Frames(Hello(),err,Ok(3)),delegate { aborts++; },"window");
        remote.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult();
        Reject<PluginRemoteException>(delegate { remote.InvokeAsync("open",1000,none).GetAwaiter().GetResult(); },"remote error");
        Assert(!remote.Failed && remote.InvokeAsync("open",1000,none).GetAwaiter().GetResult().Ok,"remote recovery");
        Reject<InvalidOperationException>(delegate { remote.SettingsAsync(1000,none).GetAwaiter().GetResult(); },"undeclared settings");
        BlockingRead block=new BlockingRead();
        int killed=0;
        PluginProtocolSession timeout=Session(new MemoryStream(),block,delegate { Interlocked.Increment(ref killed); block.Released.Set(); },"window");
        Task<PluginResponse> pending=timeout.HelloAsync("ru-RU","data",2000,none);
        Assert(block.Entered.WaitOne(1000),"worker entered");
        Reject<InvalidOperationException>(delegate { timeout.HelloAsync("ru-RU","data",1000,none).GetAwaiter().GetResult(); },"concurrent");
        Reject<TimeoutException>(delegate { pending.GetAwaiter().GetResult(); },"timeout");
        Assert(timeout.Failed && killed==1,"timeout abort");
        BlockingRead block2=new BlockingRead();
        using (CancellationTokenSource cancel=new CancellationTokenSource()) {
            PluginProtocolSession canceled=Session(new MemoryStream(),block2,delegate { block2.Released.Set(); },"window");
            Task<PluginResponse> op=canceled.HelloAsync("ru-RU","data",5000,cancel.Token);
            Assert(block2.Entered.WaitOne(1000),"cancel worker entered");
            cancel.Cancel();
            Reject<OperationCanceledException>(delegate { op.GetAwaiter().GetResult(); },"canceled");
            Assert(canceled.Failed,"cancel abort");
        }
        Console.WriteLine("PASS: " + count + " plugin protocol checks");
    }
}}
'@
    $parameters = New-Object System.CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) {
        [void]$parameters.ReferencedAssemblies.Add($ref)
    }
    $result = $provider.CompileAssemblyFromSource($parameters, [string[]]@($source,$harness))
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('AppShelf.PluginProtocolTests').GetMethod('Run').Invoke($null,@())
} finally { $provider.Dispose() }
