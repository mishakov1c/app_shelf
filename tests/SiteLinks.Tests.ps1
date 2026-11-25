# Windows PowerShell 5.1 -STA. Local HTTP only, isolated storage, real WPF windows.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-Sites-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','VersionInfo.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs','PluginWidgetView.cs')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $anchor = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Storage isolation anchor missing.' }
            $text = $text.Replace($anchor, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $errorDialog = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($text, $errorDialog).Count -ne 1) { throw 'Error boundary missing.' }
            $text = [regex]::Replace($text, $errorDialog, 'SiteTests.Errors++;')
            $text = [regex]::Replace($text, 'MessageBox.Show\(this, "(?:Введите корректный HTTP\(S\)-адрес\.|Этот сайт уже добавлен\.)",\s*"AppShelf", MessageBoxButton.OK, MessageBoxImage.(?:Warning|Information)\);', 'SiteTests.Invalid++;')
            $text = $text.Replace('return dialog.ShowDialog() == true ? edit.Text.Trim() : null;', 'return SiteTests.Input;')
            $text = $text.Replace('            dialog.ShowDialog();', '            SiteTests.Drive(dialog);')
            $text = $text.Replace('            ContextMenu addMenu = new ContextMenu();', '            ContextMenu addMenu = new ContextMenu(); SiteTests.AddMenu = addMenu;')
            $text = $text.Replace('Clipboard.SetText(', 'SiteTests.Copy(')
            $text = $text.Replace('SiteLinks.FetchFavicon(site)', 'SiteTests.Fetch(site)')
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $text
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;

public sealed class IconServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Thread thread;
    private volatile bool stopped;
    public readonly Uri Url;
    public readonly string Mode;
    public int Requests;
    public IconServer(string mode)
    {
        Mode = mode; listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Url = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
        thread = new Thread(Run); thread.IsBackground = true; thread.Start();
    }
    private void Run()
    {
        while (!stopped) try { TcpClient client = listener.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(delegate { Serve(client); }); }
        catch (SocketException) { if (!stopped) throw; }
    }
    private static void Write(NetworkStream s, string text) { byte[] bytes = Encoding.ASCII.GetBytes(text); s.Write(bytes, 0, bytes.Length); }
    private static void Respond(NetworkStream s, int status, byte[] bytes, string extra)
    {
        Write(s, "HTTP/1.1 " + status + " Result\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n" + extra + "\r\n");
        s.Write(bytes, 0, bytes.Length);
    }
    private void Serve(TcpClient client)
    {
        using (client) try {
            NetworkStream s = client.GetStream(); s.ReadTimeout = 3000;
            StreamReader reader = new StreamReader(s, Encoding.ASCII, false, 1024, true);
            string line = reader.ReadLine(); if (line == null) return; string path = line.Split(' ')[1];
            while (!String.IsNullOrEmpty(reader.ReadLine())) { }
            Interlocked.Increment(ref Requests);
            if (path == "/favicon.ico") {
                if (Mode == "headers-timeout") { Thread.Sleep(5500); return; }
                if (Mode == "body-timeout" || Mode == "trickle") {
                    Write(s, "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 100\r\n\r\n");
                    if (Mode == "body-timeout") { Thread.Sleep(5500); return; }
                    for (int i = 0; i < 100 && !stopped; i++) { s.WriteByte(0); s.Flush(); Thread.Sleep(200); } return;
                }
                if (Mode == "redirect") { Respond(s, 302, new byte[0], "Location: /icon.png\r\n"); return; }
                if (Mode == "redirect-loop") { Respond(s, 302, new byte[0], "Location: /favicon.ico\r\n"); return; }
                if (Mode == "png" || Mode == "ico" || Mode == "size-boundary") {
                    byte[] bytes = Mode == "ico" ? SiteTests.Ico : SiteTests.Png;
                    if (Mode == "size-boundary") { bytes = new byte[262144]; Array.Copy(SiteTests.Png, bytes, SiteTests.Png.Length); }
                    Respond(s, 200, bytes, ""); return;
                }
                if (Mode == "oversized" || Mode == "unknown-size") {
                    byte[] bytes = new byte[262145];
                    if (Mode == "oversized") Respond(s, 200, bytes, "");
                    else { Write(s, "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"); s.Write(bytes, 0, bytes.Length); }
                    return;
                }
                if (Mode == "corrupt") { Respond(s, 200, Encoding.ASCII.GetBytes("not an image at all"), ""); return; }
                Respond(s, Mode == "http-error" ? 500 : 404, new byte[0], ""); return;
            }
            if (path == "/") {
                if (Mode == "html-redirect") { Respond(s, 302, new byte[0], "Location: /nested/page\r\n"); return; }
                string html = "<html><head>";
                if (Mode == "linked" || Mode == "large-html") html += "<link href='/icon.png?x=1&amp;y=2' rel='shortcut icon'>";
                if (Mode == "multiple") html += "<link rel='icon' href='/broken.png'><link rel='icon' href='/icon.png'>";
                html += "</head><body>" + (Mode == "large-html" ? new string('x', 200000) : "") + "</body></html>";
                Respond(s, 200, Encoding.UTF8.GetBytes(html), ""); return;
            }
            if (path == "/nested/page") { Respond(s, 200, Encoding.UTF8.GetBytes("<link rel='icon' href='icon.png'>"), ""); return; }
            if ((Mode != "html-redirect" && path.StartsWith("/icon.png", StringComparison.Ordinal)) || path == "/nested/icon.png") { Respond(s, 200, SiteTests.Png, ""); return; }
            Respond(s, 404, new byte[0], "");
        } catch (Exception) { } // Clients abort timeout/oversize fixtures deliberately.
    }
    public void Dispose() { stopped = true; listener.Stop(); thread.Join(1000); }
}

public sealed class ForwardOnly : MemoryStream
{
    public ForwardOnly(byte[] bytes) : base(bytes) { }
    public override bool CanSeek { get { return false; } }
}

public static class SiteTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static byte[] Png, Ico;
    public static string Input, Copied;
    public static int Errors, Invalid;
    public static ContextMenu AddMenu;
    private static Action<Window> drive;
    private static int passed, failed;
    public static void Copy(string value) { Copied = value; }
    public static void CacheProbe(string address)
    {
        Assert(SiteLinks.LoadCachedIcon(new Uri(address)) != null, "Fresh process could not decode cached favicon");
    }
    public static bool Fetch(Uri site) { if (!site.IsLoopback) throw new Exception("Nonlocal HTTP is prohibited in tests"); return SiteLinks.FetchFavicon(site); }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {})); }
    private static object Get(MainWindow w, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(w); }
    private static void Set(MainWindow w, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(w, value); }
    private static object Call(MainWindow w, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(w, args); } catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static AppConfig Live(MainWindow w) { return (AppConfig)Get(w, "_config"); }
    public static void Drive(Window d)
    {
        d.ShowInTaskbar = false;
        Exception error = null;
        d.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { try { drive(d); } catch (Exception ex) { error = ex; } finally { if (d.IsVisible) d.Close(); } }));
        d.ShowDialog(); Drain(); if (error != null) throw error;
    }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> list = new List<T>(); if (root is T) list.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject) list.AddRange(Controls<T>((DependencyObject)child)); return list;
    }
    private static Button ButtonNamed(Window w, string text)
    {
        foreach (Button b in Controls<Button>(w)) if (Convert.ToString(b.Content) == text || b.Name == text) return b;
        throw new Exception("Button missing: " + text);
    }
    private static MenuItem Menu(ContextMenu menu, string text)
    {
        foreach (object item in menu.Items) { MenuItem m = item as MenuItem; if (m != null && Convert.ToString(m.Header) == text) return m; } throw new Exception("Menu missing: " + text);
    }
    private static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain(); }
    private static void Click(MenuItem m) { m.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Drain(); }
    private static MainWindow Open(string mode)
    {
        ConfigStore.Save(new AppConfig { TileDisplayMode = mode, GroupingEnabled = true, Groups = new List<string>(new string[] { "A", "B" }), ShowWeather = false, ShowNetwork = false, MinimizeToTray = false });
        ReminderStore.Save(new ReminderDocument()); MainWindow w = new MainWindow();
        w.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), w, typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        Set(w, "_networkTimer", new DispatcherTimer()); w.ShowInTaskbar = false; w.Show(); Drain(); return w;
    }
    private static Uri Url(string value) { Uri uri; Assert(SiteLinks.TryNormalize(value, out uri), "Bad fixture URI"); return uri; }
    private static void Picture(FrameworkElement view, string name)
    {
        view.UpdateLayout(); RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Ceiling(view.RenderSize.Width), (int)Math.Ceiling(view.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new DrawingVisual(); using (DrawingContext dc = visual.RenderOpen()) { dc.DrawRectangle(new VisualBrush(view) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(view.RenderSize) }, null, new Rect(view.RenderSize)); }
        bmp.Render(visual); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (FileStream s = File.Create(Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), name + ".png"))) encoder.Save(s);
    }
    private static void TextCentered(TextBox field)
    {
        Drain(); Rect caret = field.GetRectFromCharacterIndex(0);
        Assert(!caret.IsEmpty && Math.Abs(caret.Top + caret.Height / 2 - field.ActualHeight / 2) <= 1.5,
            "Text is not vertically centered: caret=" + caret + ", field=" + field.RenderSize);
    }
    private static void Case(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static DragEventArgs Drag(FrameworkElement target, DataObject data, DragDropEffects allowed, RoutedEvent routedEvent)
    {
        ConstructorInfo c = typeof(DragEventArgs).GetConstructor(Private, null, new Type[] { typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point) }, null);
        DragEventArgs e = (DragEventArgs)c.Invoke(new object[] { data, DragDropKeyStates.LeftMouseButton, allowed, target, new Point(0, 0) }); e.RoutedEvent = routedEvent; target.RaiseEvent(e); Drain(); return e;
    }
    public static void Run()
    {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Isolation failed");
        Application app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; FluentUi.Install(app);
        using (MemoryStream s = new MemoryStream()) { PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0,0,255,255, 0,255,0,255, 255,0,0,255, 255,255,255,255 }, 8))); encoder.Save(s); Png = s.ToArray(); }
        using (MemoryStream s = new MemoryStream()) { System.Drawing.SystemIcons.Application.Save(s); Ico = s.ToArray(); }
        foreach (string mode in new string[] { "png", "ico", "linked", "large-html", "redirect", "html-redirect", "multiple", "size-boundary" }) { string m = mode; Case("favicon success " + m, delegate {
            using (IconServer server = new IconServer(m)) { Assert(Fetch(server.Url), "Favicon not discovered"); ImageSource image = SiteLinks.LoadCachedIcon(server.Url); Assert(image != null && image.IsFrozen, "Cache undecodable/unfrozen"); int requests = server.Requests; Assert(Fetch(server.Url) && server.Requests == requests, "Cache hit contacted network"); }
        }); }
        foreach (string mode in new string[] { "missing", "http-error", "corrupt", "oversized", "unknown-size", "redirect-loop", "headers-timeout", "body-timeout", "trickle" }) { string m = mode; Case("favicon bounded failure " + m, delegate {
            using (IconServer server = new IconServer(m)) { Task<bool> fetch = Task.Run<bool>(delegate { return Fetch(server.Url); }); Stopwatch watch = Stopwatch.StartNew(); bool ended = fetch.Wait(11000); Assert(ended, "Favicon fetch exceeded total deadline"); Assert(!fetch.Result && SiteLinks.LoadCachedIcon(server.Url) == null, "Invalid response cached"); Console.WriteLine("TIME " + m + "=" + watch.ElapsedMilliseconds + "ms"); }
        }); }
        Case("cache reload and corrupt cache recovery", delegate {
            using (IconServer server = new IconServer("png")) { Directory.CreateDirectory(SiteLinks.IconsDirectory); File.WriteAllText(SiteLinks.IconPath(server.Url), "broken"); Assert(SiteLinks.LoadCachedIcon(server.Url) == null && !File.Exists(SiteLinks.IconPath(server.Url)), "Corrupt cache retained"); Assert(Fetch(server.Url), "Corrupt cache blocked refetch"); ImageSource first = SiteLinks.LoadCachedIcon(server.Url); ImageSource second = SiteLinks.LoadCachedIcon(server.Url); Assert(first != null && second != null && !Object.ReferenceEquals(first, second), "Reload failed"); }
        });
        Case("cached favicon survives a fresh process without server", delegate {
            string address;
            using (IconServer server = new IconServer("png")) { Assert(Fetch(server.Url), "Initial cache fill failed"); address = server.Url.AbsoluteUri; }
            string command = "$ErrorActionPreference='Stop';[void][Reflection.Assembly]::LoadFrom('" + typeof(SiteTests).Assembly.Location.Replace("'", "''") + "');[SiteTests]::CacheProbe('" + address + "')";
            using (Process process = new Process()) {
                process.StartInfo = new ProcessStartInfo("powershell.exe", "-NoProfile -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command))) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                process.Start(); string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit(); Assert(process.ExitCode == 0, "Fresh process failed: " + output);
            }
        });
        foreach (string value in new string[] { "javascript:alert(1)", "file:///C:/Windows", "ftp://localhost/", "https://user:password@localhost/", "https://localhost/\r\nURL=x", "", "mailto:a@b" }) { string v = value; Case("reject URL " + v.Replace("\r", "").Replace("\n", ""), delegate { Uri uri; Assert(!SiteLinks.TryNormalize(v, out uri), "Invalid URL accepted"); }); }
        Case("normalize URL and read legacy shortcuts", delegate {
            Uri uri = Url("www.example.com/path?q=1"); Assert(uri.AbsoluteUri == "https://www.example.com/path?q=1" && SiteLinks.SuggestedName(uri) == "example.com", "Normalization/name incorrect");
            Directory.CreateDirectory(ConfigStore.ShortcutsDirectory); string path = Path.Combine(ConfigStore.ShortcutsDirectory, "legacy.url"); File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/old\r\n", Encoding.Unicode); Assert(SiteLinks.ReadShortcutUrl(path).AbsoluteUri == "https://example.com/old", "Legacy UTF16 shortcut unreadable");
        });
        Case("add, duplicate and write failure", delegate {
            using (IconServer server = new IconServer("missing")) { MainWindow w = Open("compact"); try {
                Uri uri = new Uri(server.Url, "one"); Assert((bool)Call(w, "AddSite", uri, "My name", "B"), "Add failed"); AppItem item = Live(w).Items[0]; Assert(item.Name == "My name" && item.Group == "B" && ConfigStore.IsOwnedShortcut(item.Path) && SiteLinks.ReadShortcutUrl(item.Path) == uri, "Site values wrong");
                Assert(!(bool)Call(w, "AddSite", uri, "Duplicate", "A") && Live(w).Items.Count == 1, "Duplicate accepted");
                int files = Directory.GetFiles(ConfigStore.ShortcutsDirectory).Length; string before = File.ReadAllText(ConfigStore.FilePath);
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) { int errors = Errors; Assert(!(bool)Call(w, "AddSite", new Uri(server.Url, "two"), "Second", "A") && Errors == errors + 1 && Live(w).Items.Count == 1 && File.ReadAllText(ConfigStore.FilePath) == before && Directory.GetFiles(ConfigStore.ShortcutsDirectory).Length == files, "Failed add changed data or leaked shortcut"); }
            } finally { w.Close(); } }
        });
        Case("edit, invalid/duplicate rejection, write failure and ZIP", delegate {
            using (IconServer server = new IconServer("missing")) { MainWindow w = Open("normal"); try {
                Uri original = new Uri(server.Url, "old"), updated = new Uri(server.Url, "new"), duplicate = new Uri(server.Url, "other");
                Call(w, "AddSite", original, "Keep name", "B"); Call(w, "AddSite", duplicate, "Other", "A"); AppItem item = Live(w).Items[0]; string originalPath = item.Path;
                Input = "file:///C:/Windows"; Call(w, "EditSiteAddress", item); Assert(item.Path == originalPath && Invalid > 0, "Invalid scheme accepted");
                Input = duplicate.AbsoluteUri; Call(w, "EditSiteAddress", item); Assert(item.Path == originalPath, "Duplicate edit accepted");
                Input = updated.AbsoluteUri; int files = Directory.GetFiles(ConfigStore.ShortcutsDirectory).Length;
                using (FileStream locked = new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) { Call(w, "EditSiteAddress", item); Assert(item.Path == originalPath && SiteLinks.ReadShortcutUrl(item.Path) == original && Directory.GetFiles(ConfigStore.ShortcutsDirectory).Length == files, "Failed edit changed original/leaked file"); }
                Call(w, "EditSiteAddress", item); Assert(item.Name == "Keep name" && item.Group == "B" && SiteLinks.ReadShortcutUrl(item.Path) == updated && File.Exists(originalPath), "Edit lost name/group or backup shortcut");
                string zip = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "sites.zip"); BackupStore.ExportAll(zip, Live(w), new ReminderDocument()); BackupStore.RestoreAll(zip); string warning; bool safe; AppConfig restored = ConfigStore.Load(out warning, out safe); Assert(safe && restored.Items[0].Name == "Keep name" && restored.Items[0].Group == "B" && SiteLinks.ReadShortcutUrl(restored.Items[0].Path) == updated, "ZIP lost edited URL");
            } finally { w.Close(); } }
        });
        Case("site menu, view and copy boundary", delegate {
            using (IconServer server = new IconServer("missing")) { MainWindow w = Open("compact"); try {
                Call(w, "AddSite", server.Url, "Website", "A"); ContextMenu menu = (ContextMenu)Call(w, "MakeContextMenu", Live(w).Items[0]); Click(Menu(menu, "Копировать адрес")); Assert(Copied == server.Url.AbsoluteUri, "Context copy wrong");
                drive = delegate(Window d) { TextBox field = Controls<TextBox>(d)[0]; Assert(field.IsReadOnly && field.Text == server.Url.AbsoluteUri, "URL view wrong"); TextCentered(field); Picture(d, "view-url"); Click(ButtonNamed(d, "Копировать")); Assert(Copied == server.Url.AbsoluteUri, "Dialog copy wrong"); Click(ButtonNamed(d, "Закрыть")); };
                Click(Menu(menu, "Посмотреть адрес…")); menu.PlacementTarget = Controls<Button>((DependencyObject)Get(w, "_tileHost"))[0]; menu.IsOpen = true; Drain(); Picture(menu, "site-context"); menu.IsOpen = false;
            } finally { w.Close(); } }
        });
        foreach (double scale in new double[] { 1, 1.25, 1.5 }) { double s = scale; Case("add menu/dialog alignment scale=" + s, delegate {
            using (IconServer server = new IconServer("missing")) { MainWindow w = Open("compact"); try {
                w.Width = w.MinWidth * s; w.Height = 580 * s; ((FrameworkElement)w.Content).LayoutTransform = new ScaleTransform(s, s); Drain();
                Button add = ButtonNamed(w, "AddItemButton"); FrameworkElement contents = (FrameworkElement)add.Content; Point center = contents.TransformToAncestor(add).Transform(new Point(contents.ActualWidth / 2, contents.ActualHeight / 2));
                Assert(Math.Abs(center.X - add.ActualWidth / 2) < 1 && Math.Abs(center.Y - add.ActualHeight / 2) < 1, "Add content is not centered");
                Click(add);
                Assert(AddMenu.IsOpen && AddMenu.Items.Count == 4 &&
                    Menu(AddMenu, "Приложение или ярлык…") != null &&
                    Menu(AddMenu, "Сайт по ссылке…") != null &&
                    Menu(AddMenu, "Папку…") != null && Menu(AddMenu, "Виджет плагина…") != null, "Add menu choices missing");
                Picture(w, "header-" + s.ToString(CultureInfo.InvariantCulture)); if (s == 1) Picture(AddMenu, "add-menu");
                drive = delegate(Window d) {
                    ((FrameworkElement)d.Content).LayoutTransform = new ScaleTransform(s, s); d.Width *= s; d.Height *= s; Drain();
                    List<TextBox> fields = Controls<TextBox>(d); fields[0].Text = server.Url.AbsoluteUri; Assert(fields[1].Text == "127.0.0.1", "Suggested name missing"); fields[1].Text = "Custom title"; fields[0].Text = new Uri(server.Url, "changed").AbsoluteUri; Assert(fields[1].Text == "Custom title", "Custom name overwritten");
                    TextCentered(fields[0]); TextCentered(fields[1]);
                    Picture(d, "add-site-" + s.ToString(CultureInfo.InvariantCulture)); Click(ButtonNamed(d, "Добавить сайт"));
                }; Click(Menu(AddMenu, "Сайт по ссылке…")); AddMenu.IsOpen = false; Assert(Live(w).Items.Count == 1 && Live(w).Items[0].Name == "Custom title", "Dialog did not save");
            } finally { w.Close(); } }
        }); }
        foreach (string format in new string[] { "UniformResourceLocatorW", "text/uri-list", "text/x-moz-url", DataFormats.UnicodeText, "UniformResourceLocator", DataFormats.Text }) { string f = format; Case("browser drop format " + f, delegate {
            using (IconServer server = new IconServer("missing")) { MainWindow w = Open("compact"); try {
                DataObject data = new DataObject(); string text = f == "text/uri-list" ? "#comment\r\n" + server.Url.AbsoluteUri : f == "text/x-moz-url" ? server.Url.AbsoluteUri + "\nTitle" : server.Url.AbsoluteUri;
                if (f == "UniformResourceLocatorW") data.SetData(f, new MemoryStream(Encoding.Unicode.GetBytes(text + "\0"))); else data.SetData(f, text);
                Expander target = Controls<Expander>((DependencyObject)Get(w, "_tileHost"))[1]; FrameworkElement header = (FrameworkElement)target.Header;
                Assert(Drag(header, data, DragDropEffects.Copy | DragDropEffects.Link, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.Copy, "URL drag not accepted");
                Assert(Drag(header, data, DragDropEffects.Copy | DragDropEffects.Link, DragDrop.PreviewDropEvent).Effects == DragDropEffects.Copy && Live(w).Items.Count == 1 && Live(w).Items[0].Group == "B", "URL drop failed/wrong group");
            } finally { w.Close(); } }
        }); }
        Case("URL drop to window and unsupported drag rejection", delegate {
            using (IconServer server = new IconServer("missing")) { MainWindow w = Open("compact"); try {
                DataObject data = new DataObject(DataFormats.UnicodeText, server.Url.AbsoluteUri);
                Assert(Drag(w, data, DragDropEffects.Link, DragDrop.PreviewDropEvent).Effects == DragDropEffects.Link && Live(w).Items.Count == 1 && Live(w).Items[0].Group == "", "Window URL drop failed");
                data = new DataObject(DataFormats.UnicodeText, "javascript:alert(1)"); Assert(Drag(w, data, DragDropEffects.Copy, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None, "Unsafe drag accepted");
                data = new DataObject(DataFormats.UnicodeText, new Uri(server.Url, "second").AbsoluteUri); Assert(Drag(w, data, DragDropEffects.Move, DragDrop.PreviewDropEvent).Effects == DragDropEffects.None && Live(w).Items.Count == 1, "External Move consumed URL");
            } finally { w.Close(); } }
        });
        Case("oversized forward-only drag stream is rejected, not parsed as a prefix", delegate {
            byte[] bytes = Encoding.UTF8.GetBytes("http://127.0.0.1/\0" + new string('x', 16384));
            DataObject data = new DataObject(); data.SetData("UniformResourceLocator", new ForwardOnly(bytes));
            MethodInfo parser = typeof(MainWindow).GetMethod("DroppedSiteUrl", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(parser.Invoke(null, new object[] { data }) == null, "Oversized stream accepted its truncated prefix");
        });
        Case("file drag into group preserves original", delegate {
            MainWindow w = Open("compact"); try {
                string path = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "external.cmd"); File.WriteAllText(path, "@echo off\r\n");
                DataObject data = new DataObject(DataFormats.FileDrop, new string[] { path }); Expander target = Controls<Expander>((DependencyObject)Get(w, "_tileHost"))[0];
                Assert(Drag((FrameworkElement)target.Header, data, DragDropEffects.Copy | DragDropEffects.Move, DragDrop.PreviewDropEvent).Effects == DragDropEffects.Copy && Live(w).Items[0].Group == "A" && File.Exists(path), "File drop regressed");
            } finally { w.Close(); }
        });
        Case("add dialog rejects invalid input and preserves group and title", delegate {
            using (IconServer server = new IconServer("missing")) { MainWindow w = Open("compact"); try {
                drive = delegate(Window d) { List<TextBox> fields = Controls<TextBox>(d); fields[0].Text = "file:///C:/Windows"; fields[1].Text = "Keep title"; Click(ButtonNamed(d, "Добавить сайт")); Assert(d.IsVisible && fields[0].Text == "file:///C:/Windows" && fields[1].Text == "Keep title" && Live(w).Items.Count == 0, "Invalid input lost or saved"); fields[0].Text = server.Url.AbsoluteUri; Click(ButtonNamed(d, "Добавить сайт")); };
                Call(w, "ShowAddSiteDialog", null, "B"); Assert(Live(w).Items.Count == 1 && Live(w).Items[0].Group == "B" && Live(w).Items[0].Name == "Keep title", "Dialog lost group/title");
            } finally { w.Close(); } }
        });
        Case("favicon task keeps UI responsive and replaces fallback", delegate {
            using (IconServer server = new IconServer("headers-timeout")) { MainWindow w = Open("compact"); try {
                Stopwatch watch = Stopwatch.StartNew(); Call(w, "AddSite", server.Url, "Slow", "A"); Assert(watch.ElapsedMilliseconds < 1500, "Add waited for network");
                int ticks = 0; DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) }; timer.Tick += delegate { ticks++; }; timer.Start();
                watch.Restart(); while (watch.ElapsedMilliseconds < 600) { Drain(); Thread.Sleep(10); } timer.Stop(); Assert(ticks >= 10, "UI dispatcher blocked");
                Assert(Call(w, "GetIcon", Live(w).Items[0].Path) != null, "No fallback icon");
            } finally { w.Close(); } }
            using (IconServer server = new IconServer("png")) { MainWindow w = Open("compact"); try {
                Call(w, "AddSite", server.Url, "Icon", "A"); Stopwatch watch = Stopwatch.StartNew(); Dictionary<string, ImageSource> cache = (Dictionary<string, ImageSource>)Get(w, "_iconCache");
                while (!cache.ContainsKey(SiteLinks.IconPath(server.Url)) && watch.ElapsedMilliseconds < 5000) { Drain(); Thread.Sleep(10); }
                Assert(cache.ContainsKey(SiteLinks.IconPath(server.Url)) && Object.ReferenceEquals(Call(w, "GetIcon", Live(w).Items[0].Path), cache[SiteLinks.IconPath(server.Url)]), "Async favicon did not replace fallback");
                Picture(w, "favicon-tile");
            } finally { w.Close(); } }
        });
        Console.WriteLine("Site scenarios passed=" + passed + "; failed=" + failed); app.Shutdown(); if (failed != 0) throw new Exception("Site failures: " + failed);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $false
    $options.OutputAssembly = Join-Path $temp 'SiteTests.dll'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($ref in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) { [void]$options.ReferencedAssemblies.Add((Join-Path $framework $ref)) }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('SiteTests').GetMethod('Run').Invoke($null, @())
    "PASS isolated site tests: $temp"
} finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $previous }
