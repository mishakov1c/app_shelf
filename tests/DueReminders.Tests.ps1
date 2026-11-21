# Windows PowerShell 5.1 only. Compile with an isolated data root; never run Program.Main.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1 (.NET Framework).' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use powershell.exe -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-DueTests-' + [Guid]::NewGuid().ToString('N'))
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
            if (($source.Split(@($original), [StringSplitOptions]::None)).Length -ne 2) { throw 'Cannot verify data isolation.' }
            $source = $source.Replace($original, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
        }
        if ($file -eq 'Reminders.cs') {
            # Replace only save-error dialogs with a counter/reentrancy probe in the test assembly.
            $dialogPattern = 'MessageBox.Show\(this, "[^"\r\n]*\\n" \+ error.Message,\s*"AppShelf", MessageBoxButton.OK, MessageBoxImage.Error\);'
            if ([regex]::Matches($source, $dialogPattern).Count -ne 2) { throw 'Cannot identify save-error dialogs.' }
            $source = [regex]::Replace($source, $dialogPattern, 'DueReminderTests.RecordSaveError();')
        }
        $source = $source.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $source = $source.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $source.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using AppShelf;

public static class DueReminderTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int errorDialogs;
    private static Action duringError;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static object Get(MainWindow window, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(window); }
    private static void Set(MainWindow window, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(window, value); }
    private static void Check(MainWindow window)
    {
        try { typeof(MainWindow).GetMethod("CheckDueReminders", Private).Invoke(window, null); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static List<string> Pending(MainWindow window) { return (List<string>)Get(window, "_pendingReminderNames"); }
    private static List<string> PendingIds(MainWindow window) { return (List<string>)Get(window, "_pendingReminderIds"); }
    private static void Acknowledge(MainWindow window, IList<string> ids)
    {
        try {
            typeof(MainWindow).GetMethod("AcknowledgeReminderAlert", Private).Invoke(window, new object[] { ids });
        } catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Start(MainWindow window)
    {
        try { typeof(MainWindow).GetMethod("StartReminderTimer", Private).Invoke(window, null); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    public static void RecordSaveError()
    {
        errorDialogs++;
        Action callback = duringError;
        duringError = null;
        if (callback != null) callback();
    }
    private static ReminderDocument Seed(params string[] modes)
    {
        ReminderDocument document = new ReminderDocument();
        foreach (string mode in modes)
            document.Reminders.Add(new ReminderItem { Id = mode, Title = "Test-" + mode, Active = true,
                Repeat = mode, DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(-22).AddMinutes(-1)) });
        ReminderStore.Save(document);
        return document;
    }
    private static MainWindow Open()
    {
        MainWindow window = new MainWindow();
        EventHandler sourceHandler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        window.SourceInitialized -= sourceHandler;
        Set(window, "_networkTimer", new DispatcherTimer()); // Prevent Loaded from starting monitors.
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(delegate { }));
        // Keep alerts queued behind an existing window, so counts can be asserted without visible notifications.
        Set(window, "_reminderAlertWindow", new Window());
        return window;
    }
    private static ReminderDocument Load()
    {
        string warning; bool safe;
        ReminderDocument document = ReminderStore.Load(out warning, out safe);
        Assert(safe && warning == null, "Test reminders cannot be loaded");
        return document;
    }
    private static void AssertDelivered(ReminderDocument document, string[] oldDates)
    {
        for (int i = 0; i < oldDates.Length; i++)
        {
            ReminderItem item = document.Reminders[i];
            if (item.Repeat == "once") Assert(!item.Active && item.DueLocal == oldDates[i], "Once transition is incorrect");
            else
            {
                DateTime next, old;
                Assert(ReminderStore.TryParse(item.DueLocal, out next) && next > DateTime.Now, "Repeat was not moved to future");
                Assert(ReminderStore.TryParse(oldDates[i], out old), "Invalid original date");
                int period = item.Repeat == "daily" ? 1 : 7;
                Assert(item.Active && (next - old).TotalDays % period == 0 && next.TimeOfDay == old.TimeOfDay,
                    "Repeat cadence changed");
            }
        }
        int recurring = 0;
        for (int i = 0; i < oldDates.Length; i++)
            if (document.Reminders[i].Repeat != "once") recurring++;
        Assert(document.Reminders.Count == oldDates.Length + recurring, "Expected one row per fired recurring occurrence");
        HashSet<string> parents = new HashSet<string>();
        for (int i = oldDates.Length; i < document.Reminders.Count; i++)
        {
            ReminderItem history = document.Reminders[i];
            bool recognized = false;
            for (int j = 0; j < oldDates.Length; j++)
                if (document.Reminders[j].Id == history.ParentId && history.DueLocal == oldDates[j]) recognized = true;
            Assert(!history.Active && history.Repeat == "once" && recognized && parents.Add(history.ParentId),
                "Unexpected historical reminder or duplicate");
        }
    }
    private static void Scenario(bool fail, params string[] modes)
    {
        ReminderDocument initial = Seed(modes);
        string[] dates = initial.Reminders.ConvertAll(delegate(ReminderItem item) { return item.DueLocal; }).ToArray();
        MainWindow window = Open();
        try
        {
            ReminderDocument live = (ReminderDocument)Get(window, "_reminderDocument");
            ReminderItem firstReference = live.Reminders[0];
            string before = File.ReadAllText(ReminderStore.FilePath);
            if (fail)
            {
                // Allow reads, but deny File.Replace through the absence of FileShare.Delete.
                using (FileStream locked = new FileStream(ReminderStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    duringError = delegate { Check(window); }; // A modal dialog can pump another timer tick.
                    Check(window);
                    Assert(Pending(window).Count == 0, "Notification queued despite failed save");
                    Assert(File.ReadAllText(ReminderStore.FilePath) == before, "Failed save changed disk data");
                    for (int i = 0; i < live.Reminders.Count; i++)
                        Assert(live.Reminders[i].Active && live.Reminders[i].DueLocal == dates[i],
                            "Failed save consumed overdue reminder in memory: " + modes[i]);
                    int errorsBefore = errorDialogs;
                    Check(window);
                    Check(window);
                    Assert(errorDialogs == errorsBefore, "Repeated timer checks repeatedly showed save errors");
                    Assert(Pending(window).Count == 0, "Failed retries queued notifications");
                }
            }
            Check(window);
            Assert(Pending(window).Count == modes.Length, "Expected exactly one queued notification per due reminder");
            Assert(Object.ReferenceEquals(firstReference, ((ReminderDocument)Get(window, "_reminderDocument")).Reminders[0]),
                "Existing reminder references were replaced");
            AssertDelivered((ReminderDocument)Get(window, "_reminderDocument"), dates);
            AssertDelivered(Load(), dates);
            Check(window);
            Check(window);
            Assert(Pending(window).Count == modes.Length, "Successful retry duplicated notifications");
            // Test's placeholder alert never opens; simulate user acknowledgement
            // of the queued batch instead of merely throwing its in-memory names away.
            Acknowledge(window, new List<string>(PendingIds(window)));
            Pending(window).Clear();
            PendingIds(window).Clear();
            Check(window);
            Assert(Pending(window).Count == 0, "Notification requeued after presentation");
        }
        finally { window.Close(); }
        MainWindow restarted = Open();
        try { Check(restarted); Assert(Pending(restarted).Count == 0, "Restart redelivered consumed reminders"); }
        finally { restarted.Close(); }
        Console.WriteLine("PASS: " + String.Join(",", modes) + "; save failure/recovery=" + fail);
    }
    private static void RestartBeforeRecovery()
    {
        Seed("once", "daily", "weekly");
        MainWindow window = Open();
        try
        {
            using (FileStream locked = new FileStream(ReminderStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(window);
                Assert(Pending(window).Count == 0, "Failed save queued notifications before restart");
                window.Close();
                window = Open();
                Check(window);
                ReminderDocument live = (ReminderDocument)Get(window, "_reminderDocument");
                foreach (ReminderItem item in live.Reminders)
                {
                    DateTime due;
                    Assert(item.Active && ReminderStore.TryParse(item.DueLocal, out due) && due < DateTime.Now,
                        "Restart lost an undelivered reminder");
                }
                Assert(Pending(window).Count == 0, "Still-locked file queued notifications after restart");
            }
            Check(window);
            Assert(Pending(window).Count == 3, "Restart/recovery did not deliver all three reminders");
            Check(window);
            Assert(Pending(window).Count == 3, "Restart/recovery duplicated notifications");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS: restart before write recovery preserves all overdue reminders");
    }
    private static void HiddenStartupAfterDueShowsAlert()
    {
        // Regression: the PC/AppShelf starts 40 minutes after today's due time.
        // Unlike older persistence tests, this checks the actual WPF alert window.
        ReminderDocument seeded = new ReminderDocument();
        DateTime overdue = DateTime.Now.AddMinutes(-40);
        seeded.Reminders.Add(new ReminderItem { Id = "late-start-daily",
            Title = "Тренировка", Repeat = "daily", Active = true,
            DueLocal = ReminderStore.ToValue(overdue) });
        ReminderStore.Save(seeded);
        // Use the real Loaded -> StartMonitors -> StartReminderTimer path.
        // Disable optional external probes in the isolated test configuration.
        ConfigStore.Save(new AppConfig { ShowNetwork = false, ShowWeather = false,
            ShowPomodoro = false, RdpKeepAwake = false, MinimizeToTray = true });
        MainWindow window = new MainWindow(true);
        try
        {
            window.Show();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            Assert(!window.IsVisible || window.WindowState == WindowState.Minimized,
                "Startup-in-tray mode did not remain hidden");
            Assert(Get(window, "_remindersTimer") != null,
                "Startup did not initialize reminder timer automatically");
            Window alert = (Window)Get(window, "_reminderAlertWindow");
            Assert(alert != null && alert.IsVisible && alert.Topmost && alert.ShowInTaskbar,
                "Late-start daily reminder did not show a visible topmost alert");
            Assert(Pending(window).Count == 0, "Delivered alert remains queued");
            ReminderDocument saved = Load();
            Assert(saved.Reminders.Count == 2 &&
                saved.Reminders[0].Active && saved.Reminders[1].ParentId == "late-start-daily" &&
                !saved.Reminders[1].Active, "Late-start schedule or occurrence history incorrect");
            Check(window);
            Assert(Load().Reminders.Count == 2, "Startup alert was delivered twice");
            Assert(Load().Reminders[1].PendingAlert,
                "New history occurrence was acknowledged before user dismissed the alert");
            alert.Close();
            Assert(!Load().Reminders[1].PendingAlert,
                "Dismissing the alert did not durably acknowledge the occurrence");
            Console.WriteLine("PASS: hidden startup 40 minutes late shows actual WPF notification");
        }
        finally { window.Close(); }
    }
    private static void ReopenUnacknowledgedAlertAfterShutdown()
    {
        ReminderDocument seeded = new ReminderDocument();
        seeded.Reminders.Add(new ReminderItem
        {
            Id = "restart-daily", Title = "Тренировка", Active = true, Repeat = "daily",
            DueLocal = ReminderStore.ToValue(DateTime.Now.AddMinutes(-40))
        });
        ReminderStore.Save(seeded);
        MainWindow first = new MainWindow(true);
        Set(first, "_networkTimer", new DispatcherTimer());
        first.Show();
        first.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(delegate { }));
        Start(first);
        Window firstAlert = (Window)Get(first, "_reminderAlertWindow");
        Assert(firstAlert != null && firstAlert.IsVisible, "Original alert did not open");
        Assert(Load().Reminders[1].PendingAlert, "Pending notification was not stored");
        first.Close(); // Simulate AppShelf stopping while the user was away.
        ReminderDocument afterShutdown = Load();
        Assert(afterShutdown.Reminders.Count == 2 &&
            afterShutdown.Reminders[1].PendingAlert && afterShutdown.Reminders[0].Active,
            "Shutdown silently acknowledged a notification that the user never saw");

        MainWindow second = new MainWindow(true);
        Set(second, "_networkTimer", new DispatcherTimer());
        try
        {
            second.Show();
            second.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(delegate { }));
            Start(second);
            Window recovered = (Window)Get(second, "_reminderAlertWindow");
            Assert(recovered != null && recovered.IsVisible && recovered.Topmost,
                "Unacknowledged notification was not replayed on next startup");
            Assert(Load().Reminders.Count == 2,
                "Recovery created a second history occurrence");
            recovered.Close();
            Assert(!Load().Reminders[1].PendingAlert,
                "Recovered alert was not acknowledged when dismissed");
        }
        finally { second.Close(); }

        MainWindow third = new MainWindow(true);
        Set(third, "_networkTimer", new DispatcherTimer());
        try
        {
            third.Show();
            third.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(delegate { }));
            Start(third);
            Assert(Get(third, "_reminderAlertWindow") == null &&
                Load().Reminders.Count == 2,
                "Acknowledged alert unexpectedly returned after restart");
        }
        finally { third.Close(); }
        Console.WriteLine("PASS: shutdown without acknowledgement replays exactly one alert");
    }

    public static void Run()
    {
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"),
            "Data isolation failed");
        foreach (string mode in new string[] { "once", "daily", "weekly" })
        {
            Scenario(false, mode);
            Scenario(true, mode);
        }
        Scenario(true, "once", "daily", "weekly");
        RestartBeforeRecovery();
        HiddenStartupAfterDueShowsAlert();
        ReopenUnacknowledgedAlertAfterShutdown();
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
    $result.CompiledAssembly.GetType('DueReminderTests').GetMethod('Run').Invoke($null, @())
    Write-Output "PASS: all due reminder tests. Isolated data retained at $testRoot"
}
finally {
    $provider.Dispose()
    $env:APPSHELF_TEST_DATA = $previousRoot
}
