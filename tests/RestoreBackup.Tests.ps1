# Run with Windows PowerShell 5.1: powershell.exe -NoProfile -STA -File tests\RestoreBackup.Tests.ps1
# Compile sources in memory with only the data-root expression redirected to a unique test directory.
# Never launch Program.Main or load the real profile. Layout tests use invisible windows,
# with network monitors and global-hotkey registration suppressed in the test harness only.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1 (.NET Framework).' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use powershell.exe -STA.' }

$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-RestoreTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$previousTestRoot = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $testRoot
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs','PluginWidgetView.cs')) {
        $source = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($file -eq 'AppShelf.cs') {
            $original = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if (($source.Split(@($original), [StringSplitOptions]::None)).Length -ne 2) {
                throw 'Data-root expression changed: refuse to run without verified isolation.'
            }
            $source = $source.Replace($original, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $anchor = 'bool? savedSettings = dialog.ShowDialog();'
            if ($source.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Settings isolation boundary missing.' }
            $source = $source.Replace($anchor, 'bool? savedSettings = RestoreBackupTests.Settings(dialog);')
            $anchor = 'if (picker.ShowDialog(dialog) != true) return;'
            if ($source.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Wallpaper picker boundary missing.' }
            $source = $source.Replace($anchor, 'if (!RestoreBackupTests.PickFile(picker)) return;')
        }
        if ($file -eq 'BackupManager.cs') {
            $anchor = '            dialog.ShowDialog();'
            if ($source.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Backup dialog boundary missing.' }
            $source = $source.Replace($anchor, '            RestoreBackupTests.Backup(dialog);')
            $source = $source.Replace('if (picker.ShowDialog(dialog) != true) return;',
                'if (!RestoreBackupTests.PickFile(picker)) return;')
            $source = $source.Replace('MessageBox.Show(', 'RestoreBackupTests.Notice(')
            $anchor = 'return choice.ShowDialog() == true ? (BackupComponents?)picker.Selected : null;'
            if ($source.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Backup composition dialog boundary missing.' }
            $source = $source.Replace($anchor, 'return RestoreBackupTests.ChooseComponents(choice, delegate { return picker.Selected; }, export);')
        }
        $source = $source.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $source = $source.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $source.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShelf;

public static class RestoreBackupTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Action<Window> settingsAction, backupAction;
    private static string pickedFile;
    private static int filePickers;
    private static bool declineConfirmation;
    private static int compositionDialogs;
    internal static BackupComponents? ChooseComponents(Window dialog, Func<BackupComponents> selected, bool export)
    {
        compositionDialogs++;
        string caption = export ? "\u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0438\u0442\u044c\u2026" :
            "\u0412\u043e\u0441\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u0432\u044b\u0431\u0440\u0430\u043d\u043d\u043e\u0435\u2026";
        Exception error = null;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
            try
            {
                Assert(Controls<CheckBox>(dialog).Count > 1, "Missing backup component controls");
                Click(dialog, caption);
            }
            catch (Exception ex) { error = ex; dialog.Close(); }
        }));
        bool? result = dialog.ShowDialog();
        if (error != null) throw error;
        return result == true ? (BackupComponents?)selected() : null;
    }
    private static readonly List<string> notices = new List<string>();
    public static bool PickFile(Microsoft.Win32.FileDialog picker)
    {
        filePickers++;
        Assert(!String.IsNullOrEmpty(pickedFile), "No isolated file selected");
        picker.FileName = pickedFile;
        return true;
    }
    public static MessageBoxResult Notice(Window owner, string text, string caption,
        MessageBoxButton buttons, MessageBoxImage image)
    {
        notices.Add(text);
        return buttons == MessageBoxButton.YesNo ? (declineConfirmation ? MessageBoxResult.No : MessageBoxResult.Yes) : MessageBoxResult.OK;
    }
    public static bool? Settings(Window dialog)
    {
        Exception error = null;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
        {
            try { settingsAction(dialog); }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        bool? result = dialog.ShowDialog();
        if (error != null) throw error;
        return result;
    }
    public static void Backup(Window dialog)
    {
        dialog.Show();
        try { backupAction(dialog); }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }
    private static List<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> result = new List<T>();
        if (root is T) result.Add((T)root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject) result.AddRange(Controls<T>((DependencyObject)child));
        return result;
    }
    private static void Click(Window dialog, string title)
    {
        Button button = Controls<Button>(dialog).Find(delegate(Button candidate)
        {
            return Convert.ToString(candidate.Content) == title;
        });
        Assert(button != null, "Button missing: " + title);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void ToggleBackupGroup(Window dialog, string key)
    {
        Button header = Controls<Button>(dialog).Find(delegate(Button button)
        {
            return Convert.ToString(button.Tag) == key;
        });
        Assert(header != null, "Backup group missing: " + key);
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.UpdateLayout();
    }
    private static void AssertBackupGroups(Window dialog)
    {
        string[] keys = { "backup-automatic", "backup-full", "backup-reminders" };
        Dictionary<string, Border> bodies = new Dictionary<string, Border>();
        foreach (string key in keys)
        {
            Border header = Controls<Border>(dialog).Find(delegate(Border item)
            {
                return Convert.ToString(item.Tag) == "SettingsAccordionHeader:" + key;
            });
            Assert(header != null, "Backup header missing: " + key);
            StackPanel row = header.Parent as StackPanel;
            Assert(row != null && row.Children.Count == 2, "Unexpected accordion row");
            bodies[key] = (Border)row.Children[1];
            Assert(bodies[key].Visibility == Visibility.Collapsed, "Initially expanded: " + key);
        }
        ToggleBackupGroup(dialog, keys[0]);
        Assert(bodies[keys[0]].Visibility == Visibility.Visible, "Automatic group did not expand");
        ComboBox cadence = Controls<ComboBox>(bodies[keys[0]])[0];
        int original = cadence.SelectedIndex;
        cadence.SelectedIndex = original == 0 ? 1 : 0;
        ToggleBackupGroup(dialog, keys[1]);
        Assert(bodies[keys[0]].Visibility == Visibility.Collapsed &&
            bodies[keys[1]].Visibility == Visibility.Visible, "Full backup did not replace automatic group");
        ToggleBackupGroup(dialog, keys[2]);
        Assert(bodies[keys[1]].Visibility == Visibility.Collapsed &&
            bodies[keys[2]].Visibility == Visibility.Visible, "Reminders group did not replace full group");
        ToggleBackupGroup(dialog, keys[0]);
        Assert(cadence.SelectedIndex != original, "Collapsing lost unsaved settings");
        cadence.SelectedIndex = original;
        ToggleBackupGroup(dialog, keys[0]);
        Assert(bodies[keys[0]].Visibility == Visibility.Collapsed &&
            bodies[keys[1]].Visibility == Visibility.Collapsed &&
            bodies[keys[2]].Visibility == Visibility.Collapsed, "Clicking open group did not collapse it");
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static object Get(MainWindow window, string name)
    {
        return typeof(MainWindow).GetField(name, Private).GetValue(window);
    }
    private static void Set(MainWindow window, string name, object value)
    {
        typeof(MainWindow).GetField(name, Private).SetValue(window, value);
    }
    private static object Call(MainWindow window, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static AppConfig Config(string name)
    {
        AppConfig config = new AppConfig();
        // Real profile is redirected; choose a sibling folder for cloud-backup tests.
        config.BackupDirectory = Path.Combine(Path.GetDirectoryName(ConfigStore.DirectoryPath), "CloudBackups");
        config.MinimizeToTray = false; // Restore tests must exercise a real Close.
        config.GroupingEnabled = true;
        config.Groups.Add("Group-" + name);
        config.Items.Add(new AppItem { Name = "Application-" + name,
            Path = Path.Combine(ConfigStore.DirectoryPath, "Shortcuts", name + ".url"), Group = "Group-" + name });
        config.WeatherCity = name;
        config.ShowNetwork = false;
        config.ShowWeather = false;
        config.WindowPlacementSaved = true;
        config.WindowLeft = name == "A" ? 40 : 120;
        config.WindowTop = 50;
        config.WindowWidth = name == "A" ? 850 : 950;
        config.WindowHeight = 580;
        return config;
    }
    private static ReminderDocument Reminders(string name)
    {
        ReminderDocument document = new ReminderDocument();
        document.Reminders.Add(new ReminderItem { Id = name, Title = "Reminder-" + name,
            DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(10)), Repeat = "once", Active = true });
        return document;
    }
    private static void Seed(string name)
    {
        Directory.CreateDirectory(ConfigStore.ShortcutsDirectory);
        File.WriteAllText(Path.Combine(ConfigStore.ShortcutsDirectory, name + ".url"),
            "[InternetShortcut]\r\nURL=https://example.com/" + name + "\r\n");
        ConfigStore.Save(Config(name));
        ReminderStore.Save(Reminders(name));
    }
    private static void AssertLoadedA()
    {
        string warning; bool safe;
        AppConfig config = ConfigStore.Load(out warning, out safe);
        Assert(safe && warning == null, "Restored settings cannot be loaded");
        Assert(config.Items.Count == 1 && config.Items[0].Name == "Application-A", "Applications differ from A");
        Assert(config.Groups.Count == 1 && config.Groups[0] == "Group-A", "Groups differ from A");
        Assert(config.WeatherCity == "A" && config.WindowLeft == 40 && config.WindowWidth == 850,
            "Settings/geometry differ from A");
        Assert(File.ReadAllText(config.Items[0].Path).Contains("example.com/A"), "Restored shortcut differs from A");
        Assert(ReminderStore.Load(out warning, out safe).Reminders[0].Title == "Reminder-A", "Reminders differ from A");
    }
    private static void PrepareGeometry(MainWindow window, bool trackGeometry = true)
    {
        EventHandler sourceHandler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        window.SourceInitialized -= sourceHandler;
        Set(window, "_weatherTimer", new DispatcherTimer()); // Loaded must not start background monitors.
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(delegate { }));
        Set(window, "_windowPlacementTrackingReady", trackGeometry);
        if (!trackGeometry) ((DispatcherTimer)Get(window, "_windowPlacementSaveTimer")).Stop();
        window.Width = 1000;
        window.Left = 170;
        window.Measure(new Size(1000, 600));
        window.Arrange(new Rect(170, 50, 1000, 600));
        window.UpdateLayout();
        Assert(ConfigStore.IsValidGeometry(window.Left, window.Top, window.ActualWidth, window.ActualHeight),
            "Test must have valid geometry so Closing would save stale settings without the fix");
    }
    private static void SettingsRestoreFlow()
    {
        Seed("B");
        string root = Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA");
        string source = Path.Combine(root, "Staged wallpaper.png");
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96,
            PixelFormats.Bgra32, null, new byte[16], 8)));
        using (FileStream image = File.Create(source)) encoder.Save(image);
        string zip = Path.Combine(root, "settings-export.zip");
        string staged = null, stagedBytes = null;
        MainWindow window = new MainWindow();
        try
        {
            PrepareGeometry(window, false);
            // This scenario exports the seeded B profile, not geometry from
            // the deliberately resized in-memory window. Prevent the debounce
            // timer from racing the modal dialog setup. The pending/queued timer
            // restore cases below still exercise real SaveWindowPlacement.
            DispatcherTimer exportPlacement = (DispatcherTimer)Get(window, "_windowPlacementSaveTimer");
            exportPlacement.Stop();
            settingsAction = delegate(Window settings)
            {
                pickedFile = source;
                Click(settings, "\u0414\u043e\u0431\u0430\u0432\u0438\u0442\u044c \u0444\u043e\u043d\u2026");
                List<string> presets = CustomBackgroundStore.ListPresets();
                Assert(presets.Count == 1, "Expected one staged wallpaper");
                staged = presets[0];
                string image = CustomBackgroundStore.FilePath(staged);
                stagedBytes = Convert.ToBase64String(File.ReadAllBytes(image));
                backupAction = delegate(Window backup)
                {
                    AssertBackupGroups(backup);
                    ToggleBackupGroup(backup, "backup-full");
                    string seedWarning; bool seedSafe;
                    AppConfig seed = ConfigStore.Load(out seedWarning, out seedSafe);
                    Assert(seedSafe && seedWarning == null && seed.WindowWidth == 950 &&
                        ((AppConfig)Get(window, "_config")).WindowWidth == 950 &&
                        !exportPlacement.IsEnabled, "Unexpected geometry save before export");
                    pickedFile = zip;
                    Click(backup, "\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u0440\u0435\u0437\u0435\u0440\u0432\u043d\u0443\u044e \u043a\u043e\u043f\u0438\u044e ZIP\u2026");
                    Assert(File.Exists(zip) && compositionDialogs == 1, "ZIP export or composition picker was blocked");
                    string configBefore = File.ReadAllText(ConfigStore.FilePath);
                    string remindersBefore = File.ReadAllText(ReminderStore.FilePath);
                    int pickersBefore = filePickers;
                    // Cancelling at the confirmation must keep the nested dialogs and
                    // staged files intact; the second confirmation schedules restore.
                    declineConfirmation = true;
                    try { Click(backup, "\u0412\u043e\u0441\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u0438\u0437 ZIP\u2026"); }
                    finally { declineConfirmation = false; }
                    Assert(filePickers == pickersBefore + 1 && backup.IsVisible && settings.IsVisible,
                        "Cancel closed dialogs or skipped file selection");
                    Assert(File.ReadAllText(ConfigStore.FilePath) == configBefore &&
                        File.ReadAllText(ReminderStore.FilePath) == remindersBefore &&
                        Convert.ToBase64String(File.ReadAllBytes(image)) == stagedBytes,
                        "Cancel modified data or staged wallpaper");
                    Click(backup, "\u0412\u043e\u0441\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u0438\u0437 ZIP\u2026");
                    Assert(compositionDialogs == 3 && filePickers == pickersBefore + 2 && !backup.IsVisible && !settings.IsVisible,
                        "Confirmed restore did not close nested dialogs");
                    Assert(window.IsVisible && (bool)Get(window, "_canSave") &&
                        File.ReadAllText(ConfigStore.FilePath) == configBefore &&
                        Convert.ToBase64String(File.ReadAllBytes(image)) == stagedBytes,
                        "Restore ran before settings cleanup");
                };
                Click(settings, "\u042d\u043a\u0441\u043f\u043e\u0440\u0442 / \u0438\u043c\u043f\u043e\u0440\u0442 \u0434\u0430\u043d\u043d\u044b\u0445\u2026");
            };
            Call(window, "ShowSettings");
            Assert(!window.IsVisible && (bool)Get(window, "_explicitExitRequested") &&
                !(bool)Get(window, "_canSave") && !(bool)Get(window, "_remindersCanSave"),
                "Settings restore did not fully exit or block stale saves");
            Assert(Convert.ToBase64String(File.ReadAllBytes(CustomBackgroundStore.FilePath(staged))) == stagedBytes,
                "Settings cleanup deleted restored wallpaper");
            string warning; bool safe;
            AppConfig restored = ConfigStore.Load(out warning, out safe);
            Assert(safe && warning == null && restored.WeatherCity == "B" && restored.WindowWidth == 950,
                "Closing overwrote restored config or geometry");
        }
        finally { window.Close(); settingsAction = backupAction = null; }
        MainWindow reopened = new MainWindow();
        try
        {
            Assert(Convert.ToBase64String(File.ReadAllBytes(CustomBackgroundStore.FilePath(staged))) == stagedBytes,
                "Recovered wallpaper was deleted after Close/reopen");
        }
        finally { reopened.Close(); }
        Console.WriteLine("PASS: settings ZIP restore cancellation, deferred cleanup, full exit and wallpaper preservation");
    }
    public static void Run()
    {
        string expected = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf");
        Assert(String.Equals(ConfigStore.DirectoryPath, expected, StringComparison.OrdinalIgnoreCase),
            "Isolation failed: refuse to access any data");
        Seed("A");
        string zip = Path.Combine(expected, "A.zip");
        BackupStore.ExportAll(zip, Config("A"), Reminders("A"));
        Seed("B");
        MainWindow originalSequence = new MainWindow();
        try
        {
            PrepareGeometry(originalSequence);
            BackupStore.RestoreAll(zip);
            originalSequence.Close();
            string warning; bool safe;
            Assert(ConfigStore.Load(out warning, out safe).WeatherCity == "B",
                "Original restore/Close sequence did not reproduce the overwrite");
            Console.WriteLine("PASS: original RestoreAll/Close sequence reproduces stale B overwriting A");
        }
        finally { originalSequence.Close(); }
        foreach (bool pending in new bool[] { false, true })
        {
            Seed("B");
            MainWindow window = new MainWindow();
            try
            {
                PrepareGeometry(window);
                DispatcherTimer timer = (DispatcherTimer)Get(window, "_windowPlacementSaveTimer");
                if (pending) timer.Start(); else timer.Stop();
                string safetyCopy = (string)Call(window, "RestoreBackup", zip);
                Assert(Directory.Exists(safetyCopy), "Safety copy missing");
                Assert(!timer.IsEnabled && !(bool)Get(window, "_canSave") &&
                    !(bool)Get(window, "_remindersCanSave"), "Saving was not blocked");
                Assert(((AppConfig)Get(window, "_config")).WeatherCity == "B", "Test must retain stale B in memory");
                string restored = File.ReadAllText(ConfigStore.FilePath);
                // Simulate an already queued geometry callback and Closing after successful restore.
                Call(window, "SaveWindowPlacement");
                Call(window, "OnWindowGeometryChanged", window, EventArgs.Empty);
                Call(window, "OnWindowClosing", window, new CancelEventArgs());
                Assert(!timer.IsEnabled, "Queued geometry event restarted saving");
                Assert(File.ReadAllText(ConfigStore.FilePath) == restored, "Stale callback overwrote A");
                window.Close();
                Assert(File.ReadAllText(ConfigStore.FilePath) == restored, "Close overwrote A");
                AssertLoadedA();
                MainWindow reopened = new MainWindow();
                try { Assert(((AppConfig)Get(reopened, "_config")).WeatherCity == "A", "Reopen did not load A"); }
                finally { reopened.Close(); }
                Console.WriteLine("PASS: restore A over B, callbacks, Close and reopen; pending timer=" + pending);
            }
            finally { window.Close(); }
        }
        // Missing and malformed archives must preserve data and each prior permission/timer state.
        string invalid = Path.Combine(expected, "invalid.zip");
        File.WriteAllText(invalid, "not a ZIP");
        foreach (string path in new string[] { invalid, Path.Combine(expected, "missing.zip") })
        foreach (bool enabled in new bool[] { false, true })
        {
            Seed("B");
            MainWindow window = new MainWindow();
            DispatcherTimer placement = (DispatcherTimer)Get(window, "_windowPlacementSaveTimer");
            DispatcherTimer reminders = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            Set(window, "_remindersTimer", reminders);
            Set(window, "_canSave", enabled);
            Set(window, "_remindersCanSave", enabled);
            if (enabled) { placement.Start(); reminders.Start(); } else placement.Stop();
            string before = File.ReadAllText(ConfigStore.FilePath);
            string remindersBefore = File.ReadAllText(ReminderStore.FilePath);
            try
            {
                bool failed = false;
                try { Call(window, "RestoreBackup", path); } catch { failed = true; }
                Assert(failed, "Invalid import unexpectedly succeeded");
                Assert((bool)Get(window, "_canSave") == enabled && (bool)Get(window, "_remindersCanSave") == enabled,
                    "Failure did not restore permissions");
                Assert(placement.IsEnabled == enabled && reminders.IsEnabled == enabled, "Failure changed timer state");
                Assert(File.ReadAllText(ConfigStore.FilePath) == before &&
                    File.ReadAllText(ReminderStore.FilePath) == remindersBefore, "Failed import changed data");
                if (enabled)
                {
                    ((AppConfig)Get(window, "_config")).WeatherCity = "B-after-error";
                    Assert((bool)Call(window, "SaveConfig"), "Saving remains blocked after failure");
                    Assert(File.ReadAllText(ConfigStore.FilePath).Contains("B-after-error"), "Normal saving did not resume");
                }
                Console.WriteLine("PASS: failed import preserves data and prior state; enabled=" + enabled);
            }
            finally { window.Close(); }
        }
        SettingsRestoreFlow();
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
    $result.CompiledAssembly.GetType('RestoreBackupTests').GetMethod('Run').Invoke($null, @())
    Write-Output "PASS: all restore tests. Isolated test data retained at $testRoot"
}
finally {
    $provider.Dispose()
    $env:APPSHELF_TEST_DATA = $previousTestRoot
}
