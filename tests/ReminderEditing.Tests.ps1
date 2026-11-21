# Run: powershell.exe -NoProfile -STA -File tests\ReminderEditing.Tests.ps1
# Test-only source substitutions isolate storage and replace UI boundaries, not save handlers.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1 (.NET Framework).' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use powershell.exe -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-EditingTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$previousRoot = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $testRoot
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs')) {
        $source = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $original = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if (($source.Split(@($original), [StringSplitOptions]::None)).Length -ne 2) { throw 'Cannot verify isolation.' }
            $source = $source.Replace($original, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
        }
        if ($file -eq 'Reminders.cs') {
            $pattern = 'MessageBox.Show\(this, "[^"\r\n]*\\n" \+ error.Message,\s*"AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($source, $pattern).Count -ne 2) { throw 'Cannot identify error dialogs.' }
            $source = [regex]::Replace($source, $pattern, 'ReminderEditingTests.SaveError();')
            $modalPattern = '            dialog\.Loaded \+= delegate \{ title\.Focus\(\); title\.CaretIndex = title\.Text\.Length; \};\s*dialog\.ShowDialog\(\);'
            if ([regex]::Matches($source, $modalPattern).Count -ne 1) { throw 'Cannot identify editor boundary.' }
            $source = [regex]::Replace($source, $modalPattern, '            ReminderEditingTests.DriveEditor(dialog);')
            $source = $source.Replace('            dialog.Show();', '            ReminderEditingTests.HideAlert(dialog);')
            $first = $source.IndexOf('        private bool ConfirmReminderAction(')
            $last = $source.IndexOf('        private static bool IsCompletedReminder(', $first)
            if ($first -lt 0 -or $last -le $first) { throw 'Cannot isolate styled confirmation.' }
            $source = $source.Substring(0, $first) +
                '        private bool ConfirmReminderAction(Window owner, string message, string buttonText) { return ReminderEditingTests.ConfirmDelete(); }' + "`n" +
                $source.Substring($last)
        }
        $source = $source.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $source = $source.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $source.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using AppShelf;

public static class ReminderEditingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Action<Window> editorAction;
    private static int failures;
    private static int saveErrors;
    public static void SaveError() { saveErrors++; }
    public static bool ConfirmDelete() { return true; }
    public static void HideAlert(Window window) { } // Inspect controls without showing notifications.
    public static void DriveEditor(Window window)
    {
        try { editorAction(window); }
        finally { window.Close(); }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Get(MainWindow window, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(window); }
    private static void Set(MainWindow window, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(window, value); }
    private static object Call(MainWindow window, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static ReminderDocument Live(MainWindow window) { return (ReminderDocument)Get(window, "_reminderDocument"); }
    private static string Snapshot(MainWindow window)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(ReminderDocument)).WriteObject(stream, Live(window));
            return Convert.ToBase64String(stream.ToArray());
        }
    }
    private static MainWindow Open(bool seed)
    {
        if (seed)
        {
            ReminderDocument document = new ReminderDocument();
            document.Reminders.Add(new ReminderItem { Id = "original", Title = "Original", Active = true,
                Repeat = "weekly", DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(3)) });
            document.Reminders.Add(new ReminderItem { Id = "other", Title = "Other", Active = true,
                Repeat = "daily", DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(4)) });
            ReminderStore.Save(document);
        }
        MainWindow window = new MainWindow();
        EventHandler sourceHandler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        window.SourceInitialized -= sourceHandler;
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        System.Windows.Forms.NotifyIcon tray = (System.Windows.Forms.NotifyIcon)Get(window, "_trayIcon");
        if (tray != null) { tray.Dispose(); Set(window, "_trayIcon", null); }
        return window; // Native owner handle only: no Loaded monitors or global hotkey.
    }
    private static FileStream LockFile()
    {
        return new FileStream(ReminderStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> result = new List<T>();
        if (root is T) result.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject) result.AddRange(Controls<T>((DependencyObject)child));
        return result;
    }
    private static Button Save(Window dialog) { return Controls<Button>(dialog)[0]; }
    private static Button Cancel(Window dialog) { return Controls<Button>(dialog)[1]; }
    private static void Fill(Window dialog)
    {
        Controls<TextBox>(dialog)[0].Text = "Edited \u041f\u0440\u0438\u0432\u0435\u0442";
        Controls<DatePicker>(dialog)[0].SelectedDate = DateTime.Today.AddDays(5);
        foreach (TextBox input in Controls<TextBox>(dialog))
        {
            if (input.Name == "ReminderHours") input.Text = "18";
            if (input.Name == "ReminderMinutes") input.Text = "30";
        }
        foreach (ComboBox combo in Controls<ComboBox>(dialog))
            foreach (object value in combo.Items)
            {
                ComboBoxItem option = value as ComboBoxItem;
                if (option != null && Convert.ToString(option.Tag) == "daily")
                {
                    combo.SelectedItem = option;
                    return;
                }
            }
        throw new Exception("Daily repeat option missing");
    }
    private static void Edit(MainWindow window, ReminderItem item, Action<Window> action)
    {
        editorAction = action;
        Call(window, "EditReminder", new object[] { item });
    }
    private static void AssertReload(MainWindow window)
    {
        string expected = Snapshot(window);
        MainWindow reopened = Open(false);
        try { Assert(Snapshot(reopened) == expected, "Reopen differs from live data"); }
        finally { reopened.Close(); }
    }
    private static void CreateOrEdit(bool editing, bool fail)
    {
        MainWindow window = Open(true);
        try
        {
            ReminderItem original = editing ? Live(window).Reminders[0] : null;
            string before = Snapshot(window);
            string diskBefore = File.ReadAllText(ReminderStore.FilePath);
            Edit(window, original, delegate(Window dialog)
            {
                bool closed = false;
                dialog.Closed += delegate { closed = true; };
                Fill(dialog);
                if (fail)
                {
                    using (FileStream locked = LockFile())
                    {
                        int errorsBefore = saveErrors;
                        Click(Save(dialog));
                        Click(Save(dialog));
                        Assert(saveErrors == errorsBefore + 2, "Expected two failed write attempts");
                        Assert(!closed, "Failed save closed editor");
                        Assert(Snapshot(window) == before, "Failed create/edit changed live data or added duplicates");
                        Assert(File.ReadAllText(ReminderStore.FilePath) == diskBefore, "Failed save changed file");
                    }
                }
                Click(Save(dialog));
                Assert(closed, "Successful save did not close editor");
                Click(Save(dialog)); // A queued duplicate click must not add another reminder.
            });
            Assert(Live(window).Reminders.Count == (editing ? 2 : 3), "Create/edit produced duplicates");
            ReminderItem edited = Live(window).Reminders[editing ? 0 : 2];
            Assert(edited.Title.StartsWith("Edited ") && edited.Repeat == "daily" && edited.Active, "Edited values not applied");
            if (editing) Assert(Object.ReferenceEquals(original, edited), "Open-editor reference was replaced");
            AssertReload(window);
        }
        finally { window.Close(); }
    }
    private static void CancelEdit(bool editing, bool fail)
    {
        MainWindow window = Open(true);
        try
        {
            string before = Snapshot(window);
            string diskBefore = File.ReadAllText(ReminderStore.FilePath);
            Edit(window, editing ? Live(window).Reminders[0] : null, delegate(Window dialog)
            {
                Fill(dialog);
                if (fail)
                {
                    using (FileStream locked = LockFile()) { Click(Save(dialog)); }
                }
                Click(Cancel(dialog));
            });
            Assert(Snapshot(window) == before, "Cancel retained failed edits in memory");
            Assert(File.ReadAllText(ReminderStore.FilePath) == diskBefore, "Cancel changed file");
            // A later successful write must not persist the cancelled editor values.
            Edit(window, null, delegate(Window dialog) { Fill(dialog); Click(Save(dialog)); });
            Assert(Live(window).Reminders[0].Title == "Original", "Later save persisted cancelled edit");
            AssertReload(window);
        }
        finally { window.Close(); }
    }
    private static void Snooze(bool fail)
    {
        MainWindow window = Open(true);
        try
        {
            ReminderDocument live = Live(window);
            live.Reminders.Add(new ReminderItem { Id = "occ-a", ParentId = "original", Title = "Due-A",
                Active = false, Repeat = "once", DueLocal = ReminderStore.ToValue(DateTime.Now) });
            live.Reminders.Add(new ReminderItem { Id = "occ-b", ParentId = "other", Title = "Due-B",
                Active = false, Repeat = "once", DueLocal = ReminderStore.ToValue(DateTime.Now) });
            ReminderStore.Save(live);
            List<string> names = (List<string>)Get(window, "_pendingReminderNames");
            List<string> ids = (List<string>)Get(window, "_pendingReminderIds");
            names.Add("Due-A"); names.Add("Due-B");
            ids.Add("occ-a"); ids.Add("occ-b");
            Call(window, "ShowDueReminderAlert");
            Window alert = (Window)Get(window, "_reminderAlertWindow");
            Button snooze = Controls<Button>(alert).Find(delegate(Button button) { return Convert.ToString(button.Content).StartsWith("10 ", StringComparison.Ordinal); });
            Assert(snooze != null, "Quick snooze button is missing");
            string before = Snapshot(window);
            string diskBefore = File.ReadAllText(ReminderStore.FilePath);
            if (fail)
            {
                using (FileStream locked = LockFile())
                {
                    int errorsBefore = saveErrors;
                    Click(snooze); Click(snooze);
                    Assert(saveErrors == errorsBefore + 2, "Expected two failed snooze writes");
                    Assert(Snapshot(window) == before, "Failed snooze changed existing reminder");
                    Assert(File.ReadAllText(ReminderStore.FilePath) == diskBefore, "Failed snooze changed disk");
                    Assert(Object.ReferenceEquals(Get(window, "_reminderAlertWindow"), alert), "Failed snooze closed alert");
                }
            }
            Click(snooze);
            Click(snooze);
            Assert(Live(window).Reminders.Count == 4, "Snooze created a duplicate row");
            foreach (ReminderItem occurrence in Live(window).Reminders.GetRange(2, 2))
            {
                DateTime due;
                Assert(occurrence.Active && occurrence.Repeat == "once" &&
                    ReminderStore.TryParse(occurrence.DueLocal, out due) &&
                    Math.Abs((due - DateTime.Now.AddMinutes(10)).TotalSeconds) < 5,
                    "Snooze failed to update the same occurrence");
            }
            Assert(Live(window).Reminders[0].Repeat == "weekly" &&
                Live(window).Reminders[1].Repeat == "daily", "Snooze changed the recurring schedule");
            AssertReload(window);
        }
        finally { window.Close(); }
    }
    private static void SnoozeSameOccurrenceTwice()
    {
        MainWindow window = Open(true);
        try
        {
            ReminderDocument live = Live(window);
            ReminderItem occurrence = new ReminderItem { Id = "today", ParentId = "other",
                Title = "Training", Repeat = "once", Active = false,
                DueLocal = ReminderStore.ToValue(DateTime.Now) };
            live.Reminders.Add(occurrence);
            ReminderStore.Save(live);
            string originalSchedule = live.Reminders[1].DueLocal;
            List<string> ids = new List<string> { occurrence.Id };
            Assert((bool)Call(window, "SnoozeReminderOccurrences", ids, 10), "First snooze was rejected");
            DateTime firstDue;
            Assert(ReminderStore.TryParse(occurrence.DueLocal, out firstDue) &&
                Math.Abs((firstDue - DateTime.Now.AddMinutes(10)).TotalSeconds) < 5, "First snooze time is wrong");
            occurrence.Active = false;
            ReminderStore.Save(live);
            Assert((bool)Call(window, "SnoozeReminderOccurrences", ids, 15), "Second snooze was rejected");
            DateTime secondDue;
            Assert(ReminderStore.TryParse(occurrence.DueLocal, out secondDue) &&
                Math.Abs((secondDue - DateTime.Now.AddMinutes(15)).TotalSeconds) < 5 &&
                occurrence.Active && occurrence.Id == "today" &&
                occurrence.ParentId == "other" && live.Reminders.Count == 3,
                "Second snooze duplicated or replaced the occurrence");
            Assert(live.Reminders[1].DueLocal == originalSchedule && live.Reminders[1].Repeat == "daily",
                "Snooze changed the parent recurring schedule");
            AssertReload(window);
        }
        finally { window.Close(); }
    }

    private static void BulkCleanup()
    {
        MainWindow window = Open(true);
        try
        {
            ReminderDocument live = Live(window);
            live.Reminders.Add(new ReminderItem { Id = "done-a", Title = "Old-A", Repeat = "once",
                Active = false, DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(-8)) });
            live.Reminders.Add(new ReminderItem { Id = "done-b", Title = "Old-B", Repeat = "once",
                Active = false, DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(-2)) });
            live.Reminders.Add(new ReminderItem { Id = "active-once", Title = "Future", Repeat = "once",
                Active = true, DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(2)) });
            ReminderStore.Save(live);
            string before = Snapshot(window);
            HashSet<string> ids = new HashSet<string> { "done-a", "done-b" };
            using (FileStream locked = LockFile())
            {
                int errorsBefore = saveErrors;
                Assert(!(bool)Call(window, "RemoveReminderIds", ids), "Locked deletion must fail");
                Assert(saveErrors == errorsBefore + 1 && Snapshot(window) == before,
                    "Failed bulk deletion changed memory or omitted error");
            }
            Assert((bool)Call(window, "RemoveReminderIds", ids), "Bulk deletion failed");
            Assert(live.Reminders.Count == 3 &&
                live.Reminders.Exists(delegate(ReminderItem item) { return item.Id == "original"; }) &&
                live.Reminders.Exists(delegate(ReminderItem item) { return item.Id == "other"; }) &&
                live.Reminders.Exists(delegate(ReminderItem item) { return item.Id == "active-once"; }),
                "Bulk cleanup deleted an active reminder or left completed records");
            AssertReload(window);
        }
        finally { window.Close(); }
    }

    private static void DeleteFailure()
    {
        MainWindow window = Open(true);
        try
        {
            Set(window, "_remindersList", new StackPanel());
            Call(window, "UpdateReminderList");
            StackPanel list = (StackPanel)Get(window, "_remindersList");
            Button remove = (Button)((StackPanel)((DockPanel)((Border)list.Children[0]).Child).Children[0]).Children[1];
            string before = Snapshot(window);
            string diskBefore = File.ReadAllText(ReminderStore.FilePath);
            using (FileStream locked = LockFile())
            {
                int errorsBefore = saveErrors;
                Click(remove);
                Assert(saveErrors == errorsBefore + 1, "Expected a failed deletion write");
                Assert(Snapshot(window) == before, "Failed deletion changed live collection");
                Assert(File.ReadAllText(ReminderStore.FilePath) == diskBefore, "Failed deletion changed file");
            }
            Click(remove);
            Assert(Live(window).Reminders.Count == 1, "Successful deletion failed");
            AssertReload(window);
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
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"),
            "Data isolation failed");
        Case("create", delegate { CreateOrEdit(false, false); });
        Case("create failure/retry", delegate { CreateOrEdit(false, true); });
        Case("edit", delegate { CreateOrEdit(true, false); });
        Case("edit failure/retry", delegate { CreateOrEdit(true, true); });
        Case("create/cancel/later save", delegate { CancelEdit(false, false); });
        Case("edit/cancel/later save", delegate { CancelEdit(true, false); });
        Case("failed create/cancel/later save", delegate { CancelEdit(false, true); });
        Case("failed edit/cancel/later save", delegate { CancelEdit(true, true); });
        Case("snooze", delegate { Snooze(false); });
        Case("snooze failure/retry", delegate { Snooze(true); });
        Case("repeat snooze keeps a single occurrence and parent schedule", SnoozeSameOccurrenceTwice);
        Case("bulk cleanup failure/retry preserves active reminders", BulkCleanup);
        Case("delete failure/retry", DeleteFailure);
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
    $result.CompiledAssembly.GetType('ReminderEditingTests').GetMethod('Run').Invoke($null, @())
    Write-Output "PASS: all reminder editing tests. Isolated data retained at $testRoot"
}
finally {
    $provider.Dispose()
    $env:APPSHELF_TEST_DATA = $previousRoot
}
