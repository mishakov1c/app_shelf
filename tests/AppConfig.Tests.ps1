# Run: powershell.exe -NoProfile -STA -File tests\AppConfig.Tests.ps1
# Compile a test-only assembly with redirected storage and controlled dialog boundaries.
param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use powershell.exe -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-ConfigTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $testRoot
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs')) {
        $source = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($Baseline -and $file -in @('Collections.cs','CollectionsWindow.cs')) { continue }
        if ($Baseline -and $file -eq 'Reminders.cs') {
            # Previous AppShelf versions did not depend on the new audio service.
            $source = $source.Replace('NotificationAudio.PlayReminder(_config);', '')
        }
        if ($Baseline -and $file -eq 'WindowsIntegration.cs') { continue }
        if ($Baseline -and $file -in @('AppShelf.cs','BackupManager.cs','WeatherProviders.cs')) {
            $git = New-Object Diagnostics.Process
            $git.StartInfo.FileName = 'git'
            $git.StartInfo.WorkingDirectory = $root
            $git.StartInfo.Arguments = "show 536effc2d95b354cc13c28f815317294c83e5372:$file"
            $git.StartInfo.UseShellExecute = $false
            $git.StartInfo.RedirectStandardOutput = $true
            $git.StartInfo.RedirectStandardError = $true
            $git.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
            try {
                [void]$git.Start()
                $source = $git.StandardOutput.ReadToEnd()
                [void]$git.StandardError.ReadToEnd()
                $git.WaitForExit()
                if ($git.ExitCode -ne 0) { throw 'Cannot read the comparison baseline.' }
            }
            finally { $git.Dispose() }
        }
        if ($file -eq 'AppShelf.cs') {
            $storage = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if (($source.Split(@($storage), [StringSplitOptions]::None)).Length -ne 2) { throw 'Storage isolation cannot be verified.' }
            $source = $source.Replace($storage, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $errorDialog = 'MessageBox.Show\(this, title \+ "\\n\\n" \+ error.Message \+\s*"[^"\r\n]*" \+ ConfigStore.LogPath, "AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($source, $errorDialog).Count -ne 1) { throw 'Cannot identify error dialog.' }
            $source = [regex]::Replace($source, $errorDialog, 'ConfigTests.Error(title);')
            $confirm = 'MessageBox.Show\(this, [^;]*?MessageBoxButton.YesNo,\s*MessageBoxImage.Question\)'
            if ([regex]::Matches($source, $confirm).Count -ne 2) { throw 'Cannot identify item/group confirmations.' }
            $source = [regex]::Replace($source, $confirm, 'ConfigTests.Confirm()')
            $input = 'return dialog.ShowDialog() == true ? edit.Text.Trim() : null;'
            if (($source.Split(@($input), [StringSplitOptions]::None)).Length -ne 2) { throw 'Cannot identify name input.' }
            $source = $source.Replace($input, 'return ConfigTests.Input;')
            $source = $source.Replace('            dialog.Show();', '            ConfigTests.Hide(dialog);')
            $source = $source.Replace('bool? savedSettings = dialog.ShowDialog();',
                'bool? savedSettings = ConfigTests.Settings(dialog);')
            # The pre-existing settings regression must never write to HKCU Run.
            # New startup behavior is verified separately against an isolated test key.
            $source = $source.Replace('WindowsStartup.Apply(enable, hidden);', '')
            $source = $source.Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
            $startup = 'MessageBox.Show\(this, message, "[^"\r\n]*", MessageBoxButton.OK, MessageBoxImage.Warning\);'
            if ([regex]::Matches($source, $startup).Count -ne 1) { throw 'Cannot identify startup warning.' }
            $source = [regex]::Replace($source, $startup, 'ConfigTests.StartupWarning();')
        }
        # Registry writes and audio are exercised separately through isolated boundaries.
        $source = $source.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $source = $source.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $source.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AppShelf;

public static class ConfigTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static string Input;
    private static int failures, errors;
    private static Action duringError;
    public static void Error(string title) { errors++; Action action = duringError; duringError = null; if (action != null) action(); }
    public static MessageBoxResult Confirm() { return MessageBoxResult.Yes; }
    public static void Hide(Window window) { }
    public static void StartupWarning() { }
    private static void ChangeSettingsControls(DependencyObject node)
    {
        TextBox text = node as TextBox;
        if (text != null) text.Text = "TestCity";
        CheckBox check = node as CheckBox;
        if (check != null) check.IsChecked = false; // Do not enable network calls or RDP in tests.
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject) ChangeSettingsControls((DependencyObject)child);
    }
    public static bool Settings(Window dialog)
    {
        ChangeSettingsControls(dialog);
        return true;
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Get(MainWindow window, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(window); }
    private static void Set(MainWindow window, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(window, value); }
    private static object Call(MainWindow window, string name, params object[] args)
    {
        foreach (MethodInfo method in typeof(MainWindow).GetMethods(Private))
            if (method.Name == name && method.GetParameters().Length == args.Length)
            {
                try { return method.Invoke(window, args); }
                catch (TargetInvocationException ex) { throw ex.InnerException; }
            }
        throw new Exception("Method not found: " + name);
    }
    private static AppConfig Live(MainWindow window) { return (AppConfig)Get(window, "_config"); }
    private static string Snapshot(AppConfig config)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(stream, config);
            return Convert.ToBase64String(stream.ToArray());
        }
    }
    private static AppConfig Load(out bool safe, out string warning) { return ConfigStore.Load(out warning, out safe); }
    private static string External(string name) { return Path.Combine(ConfigStore.DirectoryPath, "External", name); }
    private static void Seed()
    {
        Directory.CreateDirectory(ConfigStore.ShortcutsDirectory);
        Directory.CreateDirectory(External(""));
        foreach (string file in new string[] { "First.url", "New.url", "New.cmd" })
            File.WriteAllText(External(file), file.EndsWith(".url") ? "[InternetShortcut]\r\nURL=https://example.com/\r\n" : "@echo off\r\n");
        string owned = Path.Combine(ConfigStore.ShortcutsDirectory, "First.url");
        File.Copy(External("First.url"), owned, true);
        AppConfig config = new AppConfig { GroupingEnabled = true, ShowWeather = false, ShowNetwork = false };
        config.Groups.AddRange(new string[] { "A", "B" });
        config.Items.Add(new AppItem { Path = owned, SourcePath = External("First.url"), Name = "First", Group = "A" });
        config.Items.Add(new AppItem { Path = External("New.cmd"), Name = "Second", Group = "A" });
        config.Items.Add(new AppItem { Path = External("Third.cmd"), Name = "Third", Group = "B" });
        File.WriteAllText(External("Third.cmd"), "@echo off\r\n");
        ConfigStore.Save(config);
    }
    private static MainWindow Open()
    {
        MainWindow window = new MainWindow();
        EventHandler sourceHandler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        window.SourceInitialized -= sourceHandler;
        Set(window, "_networkTimer", new DispatcherTimer());
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(delegate { }));
        window.UpdateLayout();
        Call(window, "SaveWindowPlacement");
        ((DispatcherTimer)Get(window, "_windowPlacementSaveTimer")).Stop();
        return window;
    }
    private static FileStream LockFile() { return new FileStream(ConfigStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read); }
    private static void Click(MenuItem item) { item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static MenuItem Menu(ContextMenu menu, string header)
    {
        foreach (object entry in menu.Items) { MenuItem item = entry as MenuItem; if (item != null && Convert.ToString(item.Header) == header) return item; }
        throw new Exception("Menu missing: " + header);
    }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static string[] Shortcuts() { string[] files = Directory.GetFiles(ConfigStore.ShortcutsDirectory); Array.Sort(files); return files; }
    private static bool SameFiles(string[] a, string[] b) { return String.Join("|", a) == String.Join("|", b); }
    private static void Pomodoro(MainWindow window)
    {
        Call(window, "ShowPomodoroWindow");
        Window dialog = (Window)Get(window, "_pomodoroWindow");
        StackPanel panel = (StackPanel)dialog.Content;
        WrapPanel row = (WrapPanel)panel.Children[5];
        ((TextBox)((StackPanel)row.Children[0]).Children[1]).Text = "55";
        ((TextBox)((StackPanel)row.Children[1]).Children[1]).Text = "8";
        ((TextBox)((StackPanel)row.Children[2]).Children[1]).Text = "18";
        Click((Button)panel.Children[6]);
    }
    private static void Operation(string name, Action<MainWindow> change, Action<MainWindow> verify)
    {
        Seed();
        MainWindow window = Open();
        try
        {
            AppItem first = Live(window).Items[0];
            AppItem second = Live(window).Items[1];
            string before = Snapshot(Live(window));
            string diskBefore = File.ReadAllText(ConfigStore.FilePath);
            string[] filesBefore = Shortcuts();
            ((TextBlock)Get(window, "_status")).Text = "Before operation";
            int errorsBefore = errors;
            using (FileStream locked = LockFile())
            {
                // Run geometry saving while the error dialog would pump messages.
                duringError = delegate { Call(window, "SaveWindowPlacement"); };
                change(window);
                Assert(errors > errorsBefore, "Operation did not attempt/report the failed save");
                Assert(Snapshot(Live(window)) == before, "Failed save changed live config: " + name);
                Assert(File.ReadAllText(ConfigStore.FilePath) == diskBefore, "Failed save changed file: " + name);
                Assert(SameFiles(filesBefore, Shortcuts()), "Failed save leaked/lost shortcut copies: " + name);
                if (name == "Pomodoro") Assert(((TextBlock)Get(window, "_status")).Text == "Before operation", "False success status");
            }
            Call(window, "SaveWindowPlacement");
            bool safe; string warning;
            Assert(Snapshot(Load(out safe, out warning)) == before, "Geometry persisted rejected changes: " + name);
            change(window);
            verify(window);
            Assert(Object.ReferenceEquals(second, Live(window).Items.Find(delegate(AppItem item) { return item.Path == second.Path; })),
                "Existing item reference lost: " + name);
            if (Live(window).Items.Contains(first)) Assert(Object.ReferenceEquals(first,
                Live(window).Items.Find(delegate(AppItem item) { return item.Path == first.Path; })), "First item reference lost");
            Assert(Snapshot(Load(out safe, out warning)) == Snapshot(Live(window)) && safe && warning == null,
                "Disk/live mismatch after retry: " + name);
            Assert(File.Exists(External("First.url")) && File.Exists(External("New.cmd")), "Original files lost");
            window.Close();
            MainWindow reopened = Open();
            try { verify(reopened); }
            finally { reopened.Close(); }
        }
        finally { window.Close(); }
    }
    private static void BackupCases()
    {
        string primary = ConfigStore.FilePath, backup = primary + ".bak";
        Seed(); string valid = File.ReadAllText(primary);
        File.WriteAllText(backup, valid); File.WriteAllText(primary, "broken-primary");
        bool safe; string warning;
        AppConfig loaded = Load(out safe, out warning);
        Assert(!safe && warning != null && loaded.Items.Count == 3, "Corrupt primary did not load protected backup");
        Assert(File.ReadAllText(primary) == "broken-primary" && File.ReadAllText(backup) == valid, "Load modified recovery files");
        File.Move(primary, primary + ".damaged");
        loaded = Load(out safe, out warning);
        Assert(!safe && warning != null && loaded.Items.Count == 3, "Missing primary silently replaced usable backup with empty config");
        Assert(!File.Exists(primary) && File.ReadAllText(backup) == valid, "Missing-primary recovery changed backup");
        File.Copy(backup, primary);
        loaded = Load(out safe, out warning);
        Assert(safe && warning == null && loaded.Items.Count == 3, "Explicit copy recovery did not restore normal loading");
        File.WriteAllText(primary, "broken-primary"); File.WriteAllText(backup, "broken-backup");
        loaded = Load(out safe, out warning);
        Assert(!safe && warning != null && loaded.Items.Count == 0, "Both-corrupt files were not protected/reported");
        Assert(File.ReadAllText(primary) == "broken-primary" && File.ReadAllText(backup) == "broken-backup", "Corrupt files overwritten");
        File.Delete(primary);
        loaded = Load(out safe, out warning);
        Assert(!safe && warning != null, "Missing primary with bad backup was treated as a new profile");
        File.Delete(backup);
        loaded = Load(out safe, out warning);
        Assert(safe && warning == null && loaded.Items.Count == 0, "New empty profile rejected");
        File.WriteAllText(primary, valid); File.WriteAllText(backup, "broken-backup");
        loaded = Load(out safe, out warning);
        Assert(safe && warning == null && loaded.Items.Count == 3, "Valid primary blocked by bad backup");
    }
    private static void Migration()
    {
        Seed();
        bool safe; string warning;
        AppConfig config = Load(out safe, out warning);
        config.Items[0].Path = External("First.url");
        config.Items[0].SourcePath = null;
        ConfigStore.Save(config);
        string before = Snapshot(config);
        string[] filesBefore = Shortcuts();
        using (FileStream locked = LockFile())
        {
            MainWindow failed = new MainWindow();
            try
            {
                Assert(Snapshot(Live(failed)) == before, "Failed migration changed live config");
                Assert(SameFiles(filesBefore, Shortcuts()), "Failed migration left orphan shortcut copies");
            }
            finally { failed.Close(); }
        }
        MainWindow recovered = new MainWindow();
        try
        {
            Assert(ConfigStore.IsOwnedShortcut(Live(recovered).Items[0].Path) &&
                File.Exists(Live(recovered).Items[0].Path), "Migration retry failed");
            Assert(File.Exists(External("First.url")), "Migration removed original");
            Assert(Snapshot(Load(out safe, out warning)) == Snapshot(Live(recovered)), "Migration disk/live mismatch");
        }
        finally { recovered.Close(); }
    }
    private static void SharedShortcut()
    {
        Seed();
        bool safe; string warning;
        AppConfig config = Load(out safe, out warning);
        config.Items[1].Path = config.Items[0].Path;
        ConfigStore.Save(config);
        MainWindow window = Open();
        try
        {
            string path = Live(window).Items[0].Path;
            Call(window, "RemoveItem", Live(window).Items[0]);
            Assert(File.Exists(path), "Removing one tile deleted a shortcut still in use");
            Call(window, "RemoveItem", Live(window).Items[0]);
            Assert(File.Exists(path), "Previous .bak lost its private shortcut");
            Call(window, "SaveConfig"); // The next successful save rotates that reference out of .bak.
            Assert(!File.Exists(path) && File.Exists(External("First.url")), "Retired copy cleanup changed original");
        }
        finally { window.Close(); }
    }
    private static void Geometry()
    {
        Seed();
        MainWindow window = Open();
        try
        {
            string before = Snapshot(Live(window));
            window.Left += 40;
            using (FileStream locked = LockFile())
            {
                Call(window, "SaveWindowPlacement");
                Assert(Snapshot(Live(window)) == before, "Rejected geometry remained in live config");
            }
            Call(window, "SaveWindowPlacement");
            bool safe; string warning;
            Assert(Live(window).WindowLeft == window.Left && Snapshot(Load(out safe, out warning)) == Snapshot(Live(window)),
                "Geometry retry failed");
        }
        finally { window.Close(); }
    }
    private static void Case(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + " - " + ex.Message); }
    }
    public static void Run()
    {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Isolation failed");
        Case("add shortcut", delegate { Operation("add shortcut", delegate(MainWindow w) { Call(w, "AddPaths", new string[] { External("New.url"), External("New.url") }, "A"); },
            delegate(MainWindow w) { Assert(Live(w).Items.Count == 4 && File.Exists(Live(w).Items[3].Path), "Shortcut not added");
                Call(w, "AddPaths", new string[] { External("New.url") }, "A"); Assert(Live(w).Items.Count == 4, "Retry duplicated item"); }); });
        Case("remove", delegate { Operation("remove", delegate(MainWindow w) { Call(w, "RemoveItem", Live(w).Items[0]); },
            delegate(MainWindow w) { Assert(Live(w).Items.Count == 2 && File.Exists(External("First.url")), "Remove failed/lost original"); }); });
        Case("rename", delegate { Operation("rename", delegate(MainWindow w) { Input = "Renamed"; Call(w, "RenameItem", Live(w).Items[0]); },
            delegate(MainWindow w) { Assert(Live(w).Items[0].Name == "Renamed", "Rename failed"); }); });
        Case("move", delegate { Operation("move", delegate(MainWindow w) { Call(w, "MoveItem", Live(w).Items[0], 1); },
            delegate(MainWindow w) { Assert(Live(w).Items[0].Name == "Second", "Move failed"); }); });
        Case("item group", delegate { Operation("item group", delegate(MainWindow w) { Call(w, "SetItemGroup", Live(w).Items[0], "B"); },
            delegate(MainWindow w) { Assert(Live(w).Items[0].Group == "B", "Group move failed"); }); });
        Case("drop", delegate { Operation("drop", delegate(MainWindow w) { Call(w, "MoveDroppedItem", Live(w).Items[0], Live(w).Items[2], "B", true); },
            delegate(MainWindow w) { Assert(Live(w).Items[2].Name == "First" && Live(w).Items[2].Group == "B", "Drop failed"); }); });
        Case("admin flag", delegate { Operation("admin flag", delegate(MainWindow w) { ContextMenu menu = (ContextMenu)Call(w, "MakeContextMenu", Live(w).Items[1]);
                 MenuItem admin = (MenuItem)menu.Items[2]; admin.IsChecked = true; Click(admin);
                 if (!Live(w).Items[1].RunAsAdmin) Assert(!admin.IsChecked, "Admin checkbox not rolled back"); },
             delegate(MainWindow w) { Assert(Live(w).Items[1].RunAsAdmin, "Admin flag failed"); }); });
        Case("create group", delegate { Operation("create group", delegate(MainWindow w) { Input = "C"; Call(w, "CreateGroup"); },
            delegate(MainWindow w) { Assert(Live(w).Groups.Count == 3 && Live(w).Groups[2] == "C", "Group not created"); }); });
        Case("create group and move", delegate { Operation("create group and move", delegate(MainWindow w) {
                Input = "C"; ContextMenu menu = (ContextMenu)Call(w, "MakeContextMenu", Live(w).Items[0]);
                 MenuItem groups = Menu(menu, "Переместить в группу"); Click((MenuItem)groups.Items[groups.Items.Count - 1]); },
            delegate(MainWindow w) { Assert(Live(w).Groups.Count == 3 && Live(w).Items[0].Group == "C", "Combined group creation/move failed"); }); });
        Case("rename group", delegate { Operation("rename group", delegate(MainWindow w) { Input = "C"; Call(w, "RenameGroup", "A"); },
            delegate(MainWindow w) { Assert(Live(w).Groups[0] == "C" && Live(w).Items[0].Group == "C", "Group rename failed"); }); });
        Case("delete group", delegate { Operation("delete group", delegate(MainWindow w) { Call(w, "DeleteGroup", "A"); },
            delegate(MainWindow w) { Assert(Live(w).Groups.Count == 1 && Live(w).Items[0].Group == "", "Group deletion failed"); }); });
        Case("group order", delegate { Operation("group order", delegate(MainWindow w) { Call(w, "MoveGroup", "A", 1); },
            delegate(MainWindow w) { Assert(Live(w).Groups[0] == "B", "Group order failed"); }); });
        Case("group drop", delegate { Operation("group drop", delegate(MainWindow w) { Call(w, "SwapGroups", "A", "B"); },
            delegate(MainWindow w) { Assert(Live(w).Groups[0] == "B", "Group swap failed"); }); });
        Case("compact", delegate { Operation("compact", delegate(MainWindow w) {
                if (typeof(MainWindow).GetMethod("SetTileDisplayMode", Private) != null) Call(w, "SetTileDisplayMode", "compact");
                else Call(w, "SetCompact", true); },
            delegate(MainWindow w) { Assert(Live(w).Compact, "Compact flag failed"); }); });
        Case("grouping", delegate { Operation("grouping", delegate(MainWindow w) { Call(w, "SetGrouping", false); },
            delegate(MainWindow w) { Assert(!Live(w).GroupingEnabled, "Grouping flag failed"); }); });
        Case("pin", delegate { Operation("pin", delegate(MainWindow w) { MenuItem pin = (MenuItem)Get(w, "_pinMenuItem"); pin.IsChecked = true; Click(pin);
                Assert(w.Topmost == Live(w).AlwaysOnTop, "Window pinned before save"); },
            delegate(MainWindow w) { Assert(Live(w).AlwaysOnTop && w.Topmost, "Pin failed"); }); });
        Case("Pomodoro", delegate { Operation("Pomodoro", Pomodoro,
            delegate(MainWindow w) { Assert(Live(w).PomodoroWorkMinutes == 55 && Live(w).PomodoroShortMinutes == 8 &&
                Live(w).PomodoroLongMinutes == 18, "Intervals not saved"); }); });
        Case("Pomodoro error status", delegate {
            Seed(); MainWindow window = Open();
            try
            {
                TextBlock status = (TextBlock)Get(window, "_status");
                status.Text = "Not saved";
                using (FileStream locked = LockFile()) { Pomodoro(window); }
                Assert(status.Text == "Not saved", "Pomodoro reported success after a write error");
            }
            finally { window.Close(); }
        });
        Case("settings", delegate { Operation("settings", delegate(MainWindow w) { Call(w, "ShowSettings"); },
            delegate(MainWindow w) { Assert(Live(w).WeatherCity == "TestCity" && !Live(w).ShowPomodoro && !Live(w).MinimizeToTray,
                "Settings not committed"); }); });
        Case("startup shortcut migration", Migration);
        Case("shared shortcut", SharedShortcut);
        Case("geometry failure/retry", Geometry);
        Case("backup recovery", BackupCases);
        if (failures != 0) throw new Exception("Failed scenarios: " + failures);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $true
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll',
        'System.Drawing.dll','System.IO.Compression.dll','System.Xml.dll','System.Xaml.dll','WPF\WindowsBase.dll',
        'WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('ConfigTests').GetMethod('Run').Invoke($null, @())
    Write-Output "PASS: all config tests. Isolated data retained at $testRoot"
}
finally {
    $provider.Dispose()
    $env:APPSHELF_TEST_DATA = $previous
}
