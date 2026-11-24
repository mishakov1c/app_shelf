# Run with Windows PowerShell 5.1 -NoProfile -STA. -Baseline reproduces defects without checkout.
param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-BackupTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $testRoot
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $samplePackage = ''
    if (-not $Baseline) {
        $samplePackage = & (Join-Path $root 'samples\NotesPlugin\Build.ps1') -OutputDirectory (Join-Path $testRoot 'SamplePackage')
        if (-not (Test-Path -LiteralPath $samplePackage -PathType Leaf)) { throw 'Missing sample package.' }
    }
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs')) {
        if ($Baseline -and $file -in @('PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs')) { continue }
        if ($Baseline -and $file -in @('AppUpdate.cs','AppUpdateWindow.cs')) { continue }
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
            $git.StartInfo.Arguments = "show 557fa13c00104c145b4ffcb8a95edb1b5a3db3c5:$file"
            $git.StartInfo.UseShellExecute = $false
            $git.StartInfo.RedirectStandardOutput = $true
            $git.StartInfo.RedirectStandardError = $true
            $git.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
            try {
                [void]$git.Start()
                $source = $git.StandardOutput.ReadToEnd()
                [void]$git.StandardError.ReadToEnd()
                $git.WaitForExit()
                if ($git.ExitCode -ne 0) { throw 'Cannot read baseline.' }
            }
            finally { $git.Dispose() }
        }
        if ($file -eq 'AppShelf.cs') {
            $storage = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if (($source.Split(@($storage), [StringSplitOptions]::None)).Length -ne 2) { throw 'Cannot verify data isolation.' }
            $source = $source.Replace($storage, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $confirm = 'MessageBox.Show\(this, [^;]*?MessageBoxButton.YesNo,\s*MessageBoxImage.Question\)'
            if ([regex]::Matches($source, $confirm).Count -ne 2) { throw 'Cannot identify confirmations.' }
            $source = [regex]::Replace($source, $confirm, 'BackupTests.Confirm()')
        }
        if ($file -eq 'BackupManager.cs') {
            # Inject failures at filesystem boundaries only; keep backup/rollback algorithms unchanged.
            $source = $source.Replace('File.Move(temp, filename);', 'BackupTests.Move(temp, filename);')
            $source = $source.Replace('File.Replace(temp, filename, null);', 'BackupTests.Replace(temp, filename);')
            $source = $source.Replace('File.Copy(', 'BackupTests.Copy(')
            $source = $source.Replace('ReminderStore.Save(reminders);', 'BackupTests.SaveImported(reminders);')
        }
        if (-not $Baseline -and $file -eq 'FluentUi.cs') {
            $anchor = 'input.CopyTo(output);'
            if ($source.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'Image copy isolation anchor missing.' }
            $source = $source.Replace($anchor, 'BackupTests.CopyImage(input, output);')
        }
        $source = $source.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $source = $source.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $source.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Threading;
using AppShelf;

public static class BackupTests
{
    private const long MiB = 1024 * 1024;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static bool failCommit, failImport;
    private static int changesDuringExport, archiveCommits;
    private static bool lockCopiedExe;
    private static FileStream copiedExeLock;
    private static bool failImageCopy;
    public static void CopyImage(Stream input, Stream output)
    {
        if (failImageCopy)
        {
            failImageCopy = false;
            output.WriteByte(137);
            output.Flush();
            throw new IOException("Injected disk full after partial image write");
        }
        input.CopyTo(output);
    }
    private static readonly HashSet<string> rollbackFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> rollbackAttempts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static int failures, passes;
    private static string Root { get { return ConfigStore.DirectoryPath; } }
    public static MessageBoxResult Confirm() { return MessageBoxResult.Yes; }
    public static void Move(string temp, string filename)
    {
        if (failCommit) throw new IOException("export-commit-failure");
        File.Move(temp, filename);
        if (Path.GetFileName(filename) == "AppShelf-data.zip")
        {
            archiveCommits++;
            if (changesDuringExport > 0)
            {
                changesDuringExport--;
                File.WriteAllText(Shortcut(0), "concurrent change " + archiveCommits);
            }
        }
    }
    public static void Replace(string temp, string filename)
    {
        if (failCommit) throw new IOException("export-commit-failure");
        File.Replace(temp, filename, null);
    }
    public static void Copy(string source, string target) { Copy(source, target, false); }
    public static void Copy(string source, string target, bool overwrite)
    {
        if (source.StartsWith(Path.Combine(Root, "Backups") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            rollbackAttempts.Add(target);
            if (rollbackFailures.Contains(target)) throw new IOException("rollback-failure:" + Path.GetFileName(target));
        }
        File.Copy(source, target, overwrite);
        if (lockCopiedExe && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            copiedExeLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    public static void SaveImported(ReminderDocument document)
    {
        if (failImport) throw new IOException("import-original-failure");
        ReminderStore.Save(document);
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static Exception Throws(Action action)
    {
        try { action(); } catch (Exception ex) { return ex; }
        throw new Exception("Expected failure did not occur");
    }
    private static string Shortcut(int index) { return Path.Combine(ConfigStore.ShortcutsDirectory, "S" + index + ".url"); }
    private static AppConfig Config(string title, int count)
    {
        AppConfig config = new AppConfig { WeatherCity = title, ShowWeather = false, ShowNetwork = false, MinimizeToTray = false };
        config.Groups.Add(title);
        for (int i = 0; i < count; i++)
        {
            config.Items.Add(new AppItem { Name = title + i, Path = Shortcut(i), Group = title });
            if (!File.Exists(Shortcut(i))) File.WriteAllText(Shortcut(i), title + i);
        }
        return config;
    }
    private static ReminderDocument Reminders(string title)
    {
        ReminderDocument document = new ReminderDocument();
        document.Reminders.Add(new ReminderItem { Id = title, Title = title, DueLocal = ReminderStore.ToValue(DateTime.Now.AddDays(10)),
            Repeat = "once", Active = true });
        return document;
    }
    private static void Seed(string title)
    {
        Directory.CreateDirectory(ConfigStore.ShortcutsDirectory);
        AppConfig config = Config(title, 2);
        for (int i = 0; i < 2; i++) File.WriteAllText(Shortcut(i), title + i);
        ConfigStore.Save(config);
        ReminderStore.Save(Reminders(title));
    }
    private static void Reset()
    {
        failCommit = failImport = false;
        changesDuringExport = archiveCommits = 0;
        rollbackFailures.Clear(); rollbackAttempts.Clear();
        Assert(Root == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Isolation failed");
        // Delete only this test run's verified data directory, never a real profile.
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        string cloud = Path.Combine(Path.GetDirectoryName(Root), "CloudBackups");
        if (Directory.Exists(cloud)) Directory.Delete(cloud, true);
        Directory.CreateDirectory(ConfigStore.ShortcutsDirectory);
        Seed("B");
    }
    private static AppConfig Loaded()
    {
        string warning; bool safe;
        AppConfig config = ConfigStore.Load(out warning, out safe);
        Assert(safe && warning == null, "Restored config could not be loaded");
        return config;
    }
    private static string ExportA()
    {
        Seed("A");
        string file = Path.Combine(Root, "export.zip");
        BackupStore.ExportAll(file, Config("A", 2), Reminders("A"));
        Seed("C");
        AppConfig older = Config("C", 2);
        string backupOnly = Path.Combine(ConfigStore.ShortcutsDirectory, "C-only.url");
        File.WriteAllText(backupOnly, "keep-for-old-bak");
        older.Items.Add(new AppItem { Name = "Backup only", Path = backupOnly });
        ConfigStore.Save(older);
        Seed("B");
        return file;
    }
    private static void NoExportTemps() { Assert(Directory.GetFiles(Root, "*.tmp").Length == 0, "Export left temporary files"); }
    private static void ExportCase(bool json, bool existing, bool failure)
    {
        string file = Path.Combine(Root, json ? "saved.json" : "saved.zip");
        Action export = delegate { if (json) BackupStore.ExportReminders(file, Reminders("A"));
            else BackupStore.ExportAll(file, Config("A", 2), Reminders("A")); };
        if (existing) { export(); }
        byte[] previous = existing ? File.ReadAllBytes(file) : null;
        failCommit = failure;
        if (failure)
        {
            Throws(export);
            Assert(existing ? File.Exists(file) && Convert.ToBase64String(File.ReadAllBytes(file)) == Convert.ToBase64String(previous)
                : !File.Exists(file), "Failed export lost/changed the previous backup");
        }
        else
        {
            export(); export();
            if (json) Assert(BackupStore.ImportReminders(file).Reminders[0].Title == "A", "JSON roundtrip failed");
            else { BackupStore.RestoreAll(file); Assert(Loaded().WeatherCity == "A", "ZIP roundtrip failed"); }
        }
        failCommit = false;
        NoExportTemps();
    }
    private static void Rollback(bool breakConfig, bool breakShortcut)
    {
        string zip = ExportA();
        string oldApps = File.ReadAllText(ConfigStore.FilePath), oldReminders = File.ReadAllText(ReminderStore.FilePath);
        string oldBackup = File.ReadAllText(ConfigStore.FilePath + ".bak");
        string oldReminderBackup = File.ReadAllText(ReminderStore.FilePath + ".bak");
        failImport = true;
        if (breakConfig) rollbackFailures.Add(ConfigStore.FilePath);
        if (breakShortcut) rollbackFailures.Add(Shortcut(0));
        Exception error = Throws(delegate { BackupStore.RestoreAll(zip); });
        Assert(rollbackAttempts.Contains(ReminderStore.FilePath) && rollbackAttempts.Contains(Shortcut(1)), "Rollback stopped after an earlier failure");
        Assert(File.ReadAllText(ReminderStore.FilePath) == oldReminders && File.ReadAllText(Shortcut(1)) == "B1", "Independent rollback did not restore remaining components");
        Assert((error.InnerException ?? error).Message == "import-original-failure", "Original import exception lost");
        PropertyInfo property = error.GetType().GetProperty("RollbackErrors");
        if (property != null)
            Assert(((ICollection)property.GetValue(error, null)).Count == (breakConfig ? 1 : 0) + (breakShortcut ? 1 : 0),
                "Rollback error count differs");
        else Assert(!breakConfig && !breakShortcut, "Rollback errors not reported separately");
        if (!breakConfig) Assert(File.ReadAllText(ConfigStore.FilePath) == oldApps, "Settings rollback failed");
        Assert(File.ReadAllText(ConfigStore.FilePath + ".bak") == oldBackup, "Previous .bak was not rolled back");
        Assert(File.ReadAllText(ReminderStore.FilePath + ".bak") == oldReminderBackup, "Reminder .bak was not rolled back");
        Assert(File.Exists(Path.Combine(ConfigStore.ShortcutsDirectory, "C-only.url")), "Import pruning deleted an old-backup shortcut before rollback");
        string[] safety = Directory.GetDirectories(Path.Combine(Root, "Backups"));
        Assert(safety.Length == 1 && File.Exists(Path.Combine(safety[0], "apps.json")) &&
            File.Exists(Path.Combine(safety[0], "Shortcuts", "S0.url")), "Safety copy was removed");
        failImport = false; rollbackFailures.Clear();
        BackupStore.RestoreAll(zip);
        Assert(Loaded().WeatherCity == "A" && File.ReadAllText(Shortcut(0)) == "A0", "Retry after rollback failed");
    }
    private static object Get(MainWindow window, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(window); }
    private static void Set(MainWindow window, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(window, value); }
    private static object Call(MainWindow window, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static MainWindow Open()
    {
        MainWindow window = new MainWindow();
        EventHandler source = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        window.SourceInitialized -= source;
        Set(window, "_networkTimer", new DispatcherTimer());
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(delegate { }));
        return window;
    }
    private static void IncompleteRollbackBlocksSaving()
    {
        string zip = ExportA();
        MainWindow window = Open();
        try
        {
            failImport = true; rollbackFailures.Add(ConfigStore.FilePath);
            Throws(delegate { Call(window, "RestoreBackup", zip); });
            Assert(!(bool)Get(window, "_canSave") && !(bool)Get(window, "_remindersCanSave"), "Partial rollback re-enabled stale saves");
            string partial = File.ReadAllText(ConfigStore.FilePath);
            Call(window, "SaveWindowPlacement");
            Assert(File.ReadAllText(ConfigStore.FilePath) == partial, "Geometry overwrote partial restore");
            failImport = false; rollbackFailures.Clear();
            Call(window, "RestoreBackup", zip);
            window.Close();
            Assert(Loaded().WeatherCity == "A", "Retry/Close overwrote restored config");
        }
        finally { window.Close(); }
    }
    private static void CompleteRollbackResumesSaving()
    {
        string zip = ExportA();
        MainWindow window = Open();
        try
        {
            DispatcherTimer placement = (DispatcherTimer)Get(window, "_windowPlacementSaveTimer");
            DispatcherTimer reminders = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            Set(window, "_remindersTimer", reminders);
            placement.Start(); reminders.Start();
            failImport = true;
            Throws(delegate { Call(window, "RestoreBackup", zip); });
            Assert((bool)Get(window, "_canSave") && (bool)Get(window, "_remindersCanSave") &&
                placement.IsEnabled && reminders.IsEnabled, "Complete rollback did not restore prior save/timer permissions");
            failImport = false;
            Assert((bool)Call(window, "SaveConfig") && Loaded().WeatherCity == "B", "Normal saving did not resume after full rollback");
        }
        finally { window.Close(); }
    }
    private static void BackupShortcut()
    {
        MainWindow window = Open();
        string path = Shortcut(0);
        try
        {
            AppItem item = ((AppConfig)Get(window, "_config")).Items[0];
            Call(window, "RemoveItem", item);
            Assert(File.Exists(path), "Removed tile's .bak shortcut was deleted");
            string backup = File.ReadAllText(ConfigStore.FilePath + ".bak");
            File.WriteAllText(ConfigStore.FilePath, "broken");
            string warning; bool safe;
            AppConfig recovered = ConfigStore.Load(out warning, out safe);
            Assert(!safe && recovered.Items.Exists(delegate(AppItem app) { return app.Path == path; }) && File.Exists(path), "Backup recovery lost its shortcut");
            File.WriteAllText(ConfigStore.FilePath, backup);
            ConfigStore.Save(recovered);
            Assert(File.Exists(path), "Saving recovered config deleted its shortcut");
            Call(window, "SaveConfig"); // Live state still has the removed tile absent.
            Call(window, "SaveConfig"); // Rotate it out of both primary and .bak.
            Assert(!File.Exists(path), "Retired known shortcut accumulated after backup rotation");
            string unknown = Path.Combine(ConfigStore.ShortcutsDirectory, "user-untracked.url");
            File.WriteAllText(unknown, "keep");
            Call(window, "SaveConfig");
            Assert(File.Exists(unknown), "Cleanup deleted an unknown file");
        }
        finally { window.Close(); }
    }
    private static void Resize(string path, long length) { using (FileStream file = File.Create(path)) file.SetLength(length); }
    private static void OriginalShortcutPreserved()
    {
        // Even an original manually placed inside Shortcuts must not become a deletion candidate via SourcePath.
        string original = Path.Combine(ConfigStore.ShortcutsDirectory, "user-original.url");
        File.WriteAllText(original, "user-original");
        string owned = ConfigStore.CopyShortcut(original);
        AppConfig config = Loaded();
        config.Items.Add(new AppItem { Name = "Copied", Path = owned, SourcePath = original });
        ConfigStore.Save(config);
        MainWindow window = Open();
        try
        {
            AppConfig live = (AppConfig)Get(window, "_config");
            Call(window, "RemoveItem", live.Items.Find(delegate(AppItem item) { return item.Path == owned; }));
            Call(window, "SaveConfig");
            Assert(!File.Exists(owned) && File.ReadAllText(original) == "user-original", "Cleanup deleted a user original");
        }
        finally { window.Close(); }
    }
    private static void CountLimit(bool over)
    {
        int count = over ? 1997 : 1996; // ZIP now includes Collections/collections.json.
        AppConfig config = Config("Large", count);
        string file = Path.Combine(Root, "limit.zip");
        BackupStore.ExportAll(file, Config("Old", 2), Reminders("Old"));
        string before = Convert.ToBase64String(File.ReadAllBytes(file));
        if (over)
        {
            Throws(delegate { BackupStore.ExportAll(file, config, Reminders("Large")); });
            Assert(Convert.ToBase64String(File.ReadAllBytes(file)) == before, "Count rejection replaced old backup");
        }
        else
        {
            BackupStore.ExportAll(file, config, Reminders("Large"));
            using (ZipArchive archive = ZipFile.OpenRead(file)) Assert(archive.Entries.Count == 2000, "Wrong boundary count");
            BackupStore.RestoreAll(file);
            Assert(Loaded().Items.Count == count, "Boundary archive cannot be restored");
        }
        NoExportTemps();
    }
    private static void SizeLimit(bool totalLimit, bool over)
    {
        AppConfig config = Config("Size", totalLimit ? 4 : 1);
        string file = Path.Combine(Root, "size.zip");
        BackupStore.ExportAll(file, config, Reminders("Size"));
        long overhead = 0;
        using (ZipArchive archive = ZipFile.OpenRead(file))
            foreach (ZipArchiveEntry entry in archive.Entries) if (!entry.FullName.StartsWith("Shortcuts/")) overhead += entry.Length;
        if (totalLimit)
        {
            for (int i = 0; i < 3; i++) Resize(Shortcut(i), 10 * MiB);
            Resize(Shortcut(3), 10 * MiB - overhead + (over ? 1 : 0));
        }
        else Resize(Shortcut(0), 10 * MiB + (over ? 1 : 0));
        string before = Convert.ToBase64String(File.ReadAllBytes(file));
        if (over)
        {
            Throws(delegate { BackupStore.ExportAll(file, config, Reminders("Size")); });
            Assert(Convert.ToBase64String(File.ReadAllBytes(file)) == before, "Size rejection replaced old backup");
        }
        else
        {
            BackupStore.ExportAll(file, config, Reminders("Size"));
            using (ZipArchive archive = ZipFile.OpenRead(file))
            {
                long total = 0; foreach (ZipArchiveEntry entry in archive.Entries) total += entry.Length;
                Assert(totalLimit ? total == 40 * MiB : archive.GetEntry("Shortcuts/S0.url").Length == 10 * MiB, "Wrong boundary size");
            }
            BackupStore.RestoreAll(file);
            Assert(Loaded().Items.Count == config.Items.Count, "Size-boundary export cannot be imported");
        }
        NoExportTemps();
    }
    private static void InvalidExport(bool badReminder)
    {
        string file = Path.Combine(Root, "valid.zip");
        AppConfig config = Config("A", 2); ReminderDocument reminders = Reminders("A");
        BackupStore.ExportAll(file, config, reminders);
        string before = Convert.ToBase64String(File.ReadAllBytes(file));
        if (badReminder) reminders.Reminders[0].Repeat = "invalid"; else config.Items.Add(null);
        Throws(delegate { BackupStore.ExportAll(file, config, reminders); });
        Assert(Convert.ToBase64String(File.ReadAllBytes(file)) == before, "Incompatible export replaced old backup");
        NoExportTemps();
    }
    private static void NativeLock(bool json)
    {
        string file = Path.Combine(Root, json ? "locked.json" : "locked.zip");
        Action export = delegate { if (json) BackupStore.ExportReminders(file, Reminders("A"));
            else BackupStore.ExportAll(file, Config("A", 2), Reminders("A")); };
        export();
        string before = Convert.ToBase64String(File.ReadAllBytes(file));
        using (FileStream locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Throws(export);
            Assert(Convert.ToBase64String(File.ReadAllBytes(file)) == before, "Native sharing violation damaged previous export");
        }
        export();
        NoExportTemps();
    }
    private static void InvalidJsonExport()
    {
        string file = Path.Combine(Root, "valid.json");
        BackupStore.ExportReminders(file, Reminders("A"));
        string before = File.ReadAllText(file);
        ReminderDocument invalid = Reminders("A"); invalid.Reminders[0].Repeat = "invalid";
        Throws(delegate { BackupStore.ExportReminders(file, invalid); });
        Assert(File.ReadAllText(file) == before, "Invalid JSON export replaced good data");
        NoExportTemps();
    }
    private static void OversizedJsonEntry()
    {
        string file = Path.Combine(Root, "large-document.zip");
        AppConfig config = Config("A", 2);
        BackupStore.ExportAll(file, config, Reminders("A"));
        string before = Convert.ToBase64String(File.ReadAllBytes(file));
        config.Items[0].Name = new string('x', (int)(10 * MiB));
        Throws(delegate { BackupStore.ExportAll(file, config, Reminders("A")); });
        Assert(Convert.ToBase64String(File.ReadAllBytes(file)) == before, "Oversized apps.json replaced previous backup");
        NoExportTemps();
    }
    private static void UnsafeImport(string extraName)
    {
        string source = ExportA(), target = Path.Combine(Root, "unsafe.zip");
        using (ZipArchive original = ZipFile.OpenRead(source))
        using (ZipArchive modified = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry entry in original.Entries)
            {
                ZipArchiveEntry copy = modified.CreateEntry(entry.FullName);
                using (Stream input = entry.Open()) using (Stream output = copy.Open()) input.CopyTo(output);
            }
            ZipArchiveEntry extra = modified.CreateEntry(extraName);
            using (StreamWriter writer = new StreamWriter(extra.Open())) writer.Write("invalid");
        }
        string before = File.ReadAllText(ConfigStore.FilePath);
        Throws(delegate { BackupStore.RestoreAll(target); });
        Assert(File.ReadAllText(ConfigStore.FilePath) == before && !Directory.Exists(Path.Combine(Root, "Backups")),
            "Unsafe import touched live state");
    }
    private static string ImageFile(string extension, int width, int height)
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"),
            Guid.NewGuid().ToString("N") + extension);
        int stride = (width + 7) / 8;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.BlackWhite,
            BitmapPalettes.BlackAndWhite, new byte[stride * height], stride);
        BitmapEncoder encoder = extension == ".png" ? (BitmapEncoder)new PngBitmapEncoder() : new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream output = File.Create(path)) encoder.Save(output);
        return path;
    }
    private static void ImageImport()
    {
        foreach (string extension in new string[] { ".png", ".jpg", ".jpeg" })
        {
            string source = ImageFile(extension, 16, 12);
            string first = CustomBackgroundStore.Import(source);
            string second = CustomBackgroundStore.Import(source);
            Assert(first != second && CustomBackgroundStore.DisplayName(first) == Path.GetFileName(source),
                "Repeated import changed names or overwrote an image");
            Assert(Convert.ToBase64String(File.ReadAllBytes(source)) ==
                Convert.ToBase64String(File.ReadAllBytes(CustomBackgroundStore.FilePath(first))), "Image bytes changed");
            int before = Directory.GetFiles(CustomBackgroundStore.DirectoryPath).Length;
            failImageCopy = true;
            Throws(delegate { CustomBackgroundStore.Import(source); });
            Assert(Directory.GetFiles(CustomBackgroundStore.DirectoryPath).Length == before,
                "Interrupted import left a partial or temporary file");
            using (FileStream locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
                Throws(delegate { CustomBackgroundStore.Import(source); });
            Assert(Directory.GetFiles(CustomBackgroundStore.DirectoryPath).Length == before, "Access failure left a file");
            CustomBackgroundStore.Import(source); // A failed attempt must not prevent the next import.
        }
        int count = Directory.GetFiles(CustomBackgroundStore.DirectoryPath).Length;
        string broken = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "broken.png");
        File.WriteAllText(broken, "not an image");
        Throws(delegate { CustomBackgroundStore.Import(broken); });
        Assert(Directory.GetFiles(CustomBackgroundStore.DirectoryPath).Length == count, "Rejected image left a file");
        string oversized = ImageFile(".png", 8193, 1);
        string originalBytes = Convert.ToBase64String(File.ReadAllBytes(oversized));
        string resized = CustomBackgroundStore.Import(oversized);
        using (FileStream input = File.OpenRead(CustomBackgroundStore.FilePath(resized)))
        {
            System.Windows.Media.Imaging.BitmapDecoder decoder =
                System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert(decoder.Frames[0].PixelWidth <= 3840 && decoder.Frames[0].PixelHeight <= 2160,
                "Large imported image was not reduced");
        }
        Assert(Convert.ToBase64String(File.ReadAllBytes(oversized)) == originalBytes,
            "Resizing modified the original image");
        CustomBackgroundStore.Delete(resized);
        string unsafeSize = ImageFile(".png", 16385, 1);
        Throws(delegate { CustomBackgroundStore.Import(unsafeSize); });
        Assert(Directory.GetFiles(CustomBackgroundStore.DirectoryPath).Length == count, "Oversized image left a file");
    }
    private static string ImageArchive(string image, string extension)
    {
        string original = ExportA();
        string target = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
        string name = Guid.NewGuid().ToString("N") + "_Wallpaper" + extension;
        using (ZipArchive archive = ZipFile.OpenRead(original))
        using (ZipArchive output = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ZipArchiveEntry copy = output.CreateEntry(entry.FullName);
                using (Stream input = entry.Open()) using (Stream content = copy.Open())
                {
                    if (entry.FullName == "apps.json")
                    {
                        AppConfig config = (AppConfig)new DataContractJsonSerializer(typeof(AppConfig)).ReadObject(input);
                        config.MainBackgroundPreset = "custom:" + name;
                        new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(content, config);
                    }
                    else input.CopyTo(content);
                }
            }
            ZipArchiveEntry wallpaper = output.CreateEntry("Backgrounds/" + name);
            using (Stream content = wallpaper.Open())
                if (image != null) using (FileStream input = File.OpenRead(image)) input.CopyTo(content);
        }
        return target;
    }
    private static void ImageRestore(string kind)
    {
        string extension = kind == "jpg" || kind == "corrupt-jpg" ? ".jpg" : kind == "jpeg" ? ".jpeg" : ".png";
        bool valid = kind == "valid" || kind == "jpg" || kind == "jpeg";
        string image = kind == "empty" ? null : ImageFile(extension, 16, 12);
        if (kind == "text") File.WriteAllText(image, "not a PNG");
        if (kind == "corrupt-jpg") File.WriteAllText(image, "not a JPEG");
        if (kind == "dimensions") image = ImageFile(".png", 8193, 1);
        if (kind == "pixels") image = ImageFile(".png", 6001, 6000);
        if (kind == "truncated")
        {
            long length = new FileInfo(image).Length;
            using (FileStream file = File.OpenWrite(image)) file.SetLength(length / 2);
        }
        if (kind == "bytes")
            using (FileStream file = File.OpenWrite(image)) file.SetLength(CustomBackgroundStore.MaxFileBytes + 1);
        string zip = ImageArchive(image, kind == "mismatch" ? ".jpg" : extension);
        string configBefore = File.ReadAllText(ConfigStore.FilePath);
        string remindersBefore = File.ReadAllText(ReminderStore.FilePath);
        if (!valid && kind != "copy-failure" && kind != "save-failure" && kind != "conflict")
        {
            Throws(delegate { BackupStore.RestoreAll(zip); });
            Assert(!Directory.Exists(Path.Combine(Root, "Backups")), "Invalid image reached live restore phase");
        }
        else
        {
            string name;
            using (ZipArchive archive = ZipFile.OpenRead(zip))
                name = archive.Entries[archive.Entries.Count - 1].Name;
            if (kind == "conflict")
            {
                Directory.CreateDirectory(CustomBackgroundStore.DirectoryPath);
                File.WriteAllText(Path.Combine(CustomBackgroundStore.DirectoryPath, name), "keep-existing");
            }
            failImageCopy = kind == "copy-failure";
            failImport = kind == "save-failure";
            if (valid)
            {
                BackupStore.RestoreAll(zip);
                Assert(Loaded().MainBackgroundPreset == "custom:" + name, "Selected wallpaper not restored");
                string path = CustomBackgroundStore.FilePath(Loaded().MainBackgroundPreset);
                Assert(File.Exists(path), "Restored wallpaper missing");
                string exported = Path.Combine(Root, "wallpaper-roundtrip.zip");
                BackupStore.ExportAll(exported, Loaded(), Reminders("A"));
                BackupStore.RestoreAll(exported);
                return;
            }
            try { Throws(delegate { BackupStore.RestoreAll(zip); }); }
            finally { failImageCopy = failImport = false; }
            if (kind == "conflict")
                Assert(File.ReadAllText(Path.Combine(CustomBackgroundStore.DirectoryPath, name)) == "keep-existing",
                    "Existing image was overwritten");
            else Assert(Directory.GetFiles(CustomBackgroundStore.DirectoryPath).Length == 0,
                "Failed restore left a partial, temporary or published image");
        }
        Assert(File.ReadAllText(ConfigStore.FilePath) == configBefore &&
            File.ReadAllText(ReminderStore.FilePath) == remindersBefore &&
            File.ReadAllText(Shortcut(0)) == "B0", "Failed image restore changed existing data");
    }
    private static void CoverRestore(bool corrupt, bool copyFailure)
    {
        string image = ImageFile(".png", 16, 12);
        string cover = CollectionStore.ImportCoverFile(image);
        CollectionDocument collections = new CollectionDocument();
        CollectionDefinition group = new CollectionDefinition { Id = "group", Name = "Group" };
        group.Entries.Add(new CollectionEntry { Id = "entry", Title = "Entry", CoverFile = cover });
        collections.Collections.Add(group);
        CollectionStore.Save(collections);
        string original = Path.Combine(Root, "cover.zip");
        BackupStore.ExportAll(original, Loaded(), Reminders("B"));
        string zip = original;
        if (corrupt)
        {
            zip = Path.Combine(Root, "broken-cover.zip");
            using (ZipArchive input = ZipFile.OpenRead(original))
            using (ZipArchive output = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (ZipArchiveEntry entry in input.Entries)
                {
                    ZipArchiveEntry copy = output.CreateEntry(entry.FullName);
                    using (Stream source = entry.Open()) using (Stream target = copy.Open())
                        if (!entry.FullName.StartsWith("Collections/Images/", StringComparison.Ordinal)) source.CopyTo(target);
                }
        }
        File.Delete(CollectionStore.CoverPath(cover));
        string before = File.ReadAllText(CollectionStore.FilePath);
        failImageCopy = copyFailure;
        try
        {
            if (corrupt || copyFailure) Throws(delegate { BackupStore.RestoreAll(zip); });
            else BackupStore.RestoreAll(zip);
        }
        finally { failImageCopy = false; }
        Assert(File.ReadAllText(CollectionStore.FilePath) == before, "Cover restore changed collection records");
        Assert(Directory.GetFiles(CollectionStore.ImagesPath).Length == (corrupt || copyFailure ? 0 : 1),
            "Cover restore lost valid data or left a partial file");
        Assert(File.ReadAllText(Shortcut(0)) == "B0", "Cover restore damaged shortcuts");
    }
    private static string ExternalBackupRoot()
    {
        // Sibling of the isolated AppShelf data root, never a real user directory.
        return Path.Combine(Path.GetDirectoryName(Root), "CloudBackups");
    }

    private static void InvalidBackupDirectory()
    {
        string backup = ExternalBackupRoot();
        Assert(BackupAutomation.ValidateRoot(backup) == Path.GetFullPath(backup),
            "External backup root rejected");
        Throws(delegate { BackupAutomation.ValidateRoot(Root); });
        Throws(delegate { BackupAutomation.ValidateRoot(Path.Combine(Root, "Backups")); });
        Throws(delegate { BackupAutomation.ValidateRoot(Path.GetDirectoryName(Root)); });
        Throws(delegate { BackupAutomation.ValidateRoot(Path.GetPathRoot(Root)); });
        Throws(delegate { BackupAutomation.ValidateRoot("relative-path"); });
    }

    private static void AutomaticBackupRetention()
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        config.AutomaticBackupsEnabled = true;
        config.AutomaticBackupDays = 1;
        config.AutomaticBackupKeep = 3;
        ConfigStore.Save(config);
        string daily = Path.Combine(config.BackupDirectory, "Automatic");
        Assert(BackupAutomation.CreateAutomatic(config), "First daily archive was not created");
        Assert(Directory.GetFiles(daily, "*.zip").Length == 1, "Expected one daily archive");
        string zip = Directory.GetFiles(daily, "*.zip")[0];
        using (ZipArchive archive = ZipFile.OpenRead(zip))
            Assert(archive.GetEntry("apps.json") != null &&
                archive.GetEntry("reminders.json") != null, "Portable ZIP entries missing");
        Assert(!BackupAutomation.CreateAutomatic(config),
            "Second same-day run created duplicate");
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(ConfigStore.FilePath, DateTime.UtcNow.AddMinutes(-1));
        Assert(!BackupAutomation.CreateAutomatic(config),
            "Identical old data unexpectedly created a second ZIP");
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllText(Shortcut(0), "changed backup version " + i);
            Assert(BackupAutomation.CreateAutomatic(config),
                "Changed source was not backed up");
            zip = Directory.GetFiles(daily, "*.zip")
                .OrderByDescending(delegate(string name) { return File.GetLastWriteTimeUtc(name); }).First();
            File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddDays(-2));
        }
        Assert(Directory.GetFiles(daily, "*.zip").Length == 3, "Daily retention did not limit to three");
    }

    private static void PreUpdateBackupAndRestoreLocation()
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        ConfigStore.Save(config);
        string executable = Path.Combine(Path.GetDirectoryName(Root), "Original-AppShelf.exe");
        File.WriteAllText(executable, "original executable bytes 2.22.0");
        string result = BackupAutomation.CreateBeforeUpdate(config,
            executable, "2.22.0", "2.23.0");
        Assert(Directory.Exists(result) &&
            Directory.GetFiles(result, "*.zip").Length == 1 &&
            Directory.GetFiles(result, "*.exe").Length == 1,
            "BeforeUpdate did not contain both ZIP and old EXE");
        Assert(File.ReadAllText(Directory.GetFiles(result, "*.exe")[0]) ==
            File.ReadAllText(executable), "Old EXE bytes did not match");
        Assert(!File.Exists(executable + ".previous"), "Update backup left adjacent .previous");
        string portable = Directory.GetFiles(result, "*.zip")[0];
        string safety = BackupStore.RestoreAll(portable, config.BackupDirectory);
        Assert(safety.StartsWith(Path.Combine(config.BackupDirectory, "BeforeRestore"),
            StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(safety, "apps.json")),
            "Pre-restore safety copy was not moved to the selected folder");
    }

    private static void CloudReaderDoesNotBlockBackupPublication()
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        ConfigStore.Save(config);
        string executable = Path.Combine(Path.GetDirectoryName(Root), "Cloud-test.exe");
        File.WriteAllText(executable, "old EXE locked by cloud reader after copy");
        lockCopiedExe = true;
        try
        {
            string result = BackupAutomation.CreateBeforeUpdate(config,
                executable, "2.23.0-beta.1", "2.23.0-beta.2");
            Assert(copiedExeLock != null, "Cloud reader simulation was not active");
            Assert(File.Exists(Path.Combine(result, ".complete")) &&
                File.Exists(Path.Combine(result, "AppShelf-data.zip")) &&
                File.ReadAllText(Path.Combine(result, "AppShelf-2.23.0-beta.1.exe")) ==
                    File.ReadAllText(executable),
                "Locked copied EXE prevented complete backup publication");
        }
        finally
        {
            lockCopiedExe = false;
            if (copiedExeLock != null) { copiedExeLock.Dispose(); copiedExeLock = null; }
        }
    }

    private static void IncompleteUpdateSetsAreNotRetained()
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        ConfigStore.Save(config);
        string executable = Path.Combine(Path.GetDirectoryName(Root), "Cloud-test.exe");
        File.WriteAllText(executable, "test EXE");
        string result = BackupAutomation.CreateBeforeUpdate(config,
            executable, "2.23.0-beta.1", "2.23.0-beta.2");
        string parent = Path.GetDirectoryName(result);
        string incomplete = Path.Combine(parent,
            "20260101-010101-v2.23.0-beta.1-to-2.23.0-beta.2-aaaaaa-set");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "AppShelf-2.23.0-beta.1.exe"), "partial");
        Directory.SetCreationTimeUtc(incomplete, DateTime.UtcNow.AddDays(-2));
        BackupAutomation.CreateBeforeUpdate(config, executable, "2.23.0-beta.2", "2.23.0-beta.3");
        Assert(!Directory.Exists(incomplete) && Directory.Exists(result),
            "Stale incomplete set not cleaned or prior completed set lost");
    }

    private static void RetryChangedData(bool persistent)
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        ConfigStore.Save(config);
        string executable = Path.Combine(Path.GetDirectoryName(Root), "Retry-test.exe");
        File.WriteAllText(executable, "test executable");
        changesDuringExport = persistent ? 3 : 1;
        if (persistent)
        {
            Exception error = Throws(delegate {
                BackupAutomation.CreateBeforeUpdate(config, executable, "2.23.0-beta.6", "2.23.0-beta.7");
            });
            Assert(error is IOException && archiveCommits == 3 &&
                Directory.GetDirectories(Path.Combine(config.BackupDirectory, "BeforeUpdate")).Length == 0,
                "Persistent changes did not stop after three clean attempts");
        }
        else
        {
            string result = BackupAutomation.CreateBeforeUpdate(config, executable,
                "2.23.0-beta.6", "2.23.0-beta.7");
            Assert(archiveCommits == 2 && File.Exists(Path.Combine(result, ".complete")) &&
                Directory.GetDirectories(Path.GetDirectoryName(result)).Length == 1,
                "Transient data change did not recover on second attempt");
        }
    }

    private static void FailedPreUpdateKeepsOldCopies()
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        ConfigStore.Save(config);
        string executable = Path.Combine(Path.GetDirectoryName(Root), "AppShelf-old.exe");
        File.WriteAllText(executable, "old executable to keep");
        string keep = BackupAutomation.CreateBeforeUpdate(config,
            executable, "2.22.0", "2.23.0");
        failCommit = true;
        try
        {
            Throws(delegate {
                BackupAutomation.CreateBeforeUpdate(config,
                    executable, "2.22.0", "2.23.1");
            });
        }
        finally { failCommit = false; }
        string parent = Path.Combine(config.BackupDirectory, "BeforeUpdate");
        Assert(Directory.Exists(keep) && Directory.GetDirectories(parent).Length == 1,
            "Failed pre-update backup deleted a good copy or left staging data");
    }

    private static void FailedAutomaticPreservesPrevious()
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        config.AutomaticBackupsEnabled = true;
        ConfigStore.Save(config);
        string directory = Path.Combine(config.BackupDirectory, "Automatic");
        Assert(BackupAutomation.CreateAutomatic(config), "Initial archive missing");
        string original = Directory.GetFiles(directory, "*.zip")[0];
        long length = new FileInfo(original).Length;
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(Shortcut(0), "force a different backup");
        failCommit = true;
        try { Throws(delegate { BackupAutomation.CreateAutomatic(config); }); }
        finally { failCommit = false; }
        Assert(File.Exists(original) && new FileInfo(original).Length == length &&
            Directory.GetFiles(directory, "*.zip").Length == 1 &&
            Directory.GetFiles(directory, "*.tmp").Length == 0,
            "Failed automatic export damaged a prior ZIP or left temporary files");
    }

    private static void MigrateOldAdjacentExecutable()
    {
        AppConfig config = Loaded();
        config.BackupDirectory = ExternalBackupRoot();
        ConfigStore.Save(config);
        string currentExe = Path.Combine(Path.GetDirectoryName(Root), "AppShelf.exe");
        string previous = currentExe + ".previous";
        File.WriteAllText(previous, "old 2.21.1 executable");
        string destination = BackupAutomation.MigrateLegacyPreviousExe(config, currentExe);
        Assert(destination != null && Directory.Exists(destination) &&
            !File.Exists(previous) &&
            File.ReadAllText(Path.Combine(destination, "AppShelf-previous.exe")) ==
                "old 2.21.1 executable", "Legacy EXE was not preserved or adjacent file remains");
        Assert(BackupAutomation.MigrateLegacyPreviousExe(config, currentExe) == null,
            "Second migration duplicated a previous executable");
    }

    private static void PluginBackupRoundtrip()
    {
        string oldArchive = Path.Combine(Root, "old.zip");
        BackupStore.ExportAll(oldArchive, Loaded(), Reminders("B"));
        string package = Path.Combine(Root, "test.asplugin");
        using (FileStream input = File.Create(package))
        using (ZipArchive zip = new ZipArchive(input, ZipArchiveMode.Create))
        {
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                writer.Write("{\"schemaVersion\":1,\"apiVersion\":1,\"id\":\"test.backup\",\"name\":\"Backup test\",\"version\":\"1.0.0\",\"entryPoint\":\"bin/Test.exe\",\"capabilities\":[\"window\"],\"backupFiles\":[\"settings.json\"]}");
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("bin/Test.exe").Open()))
                writer.Write("never executed");
        }
        PluginPackageStore store = new PluginPackageStore(Root);
        store.Install(package, delegate { return true; });
        store.SetEnabled("test.backup", true);
        string data = store.GetDataDirectory("test.backup");
        string settings = Path.Combine(data, "settings.json");
        File.WriteAllText(settings, "saved settings");
        File.WriteAllText(Path.Combine(data, "history.txt"), "private history");
        string before = BackupAutomation.DataSignature();
        File.WriteAllText(settings, "changed but not opted in");
        Assert(before == BackupAutomation.DataSignature(), "Unselected data changed signature");
        File.WriteAllText(settings, "saved settings");
        store.SetBackupDataEnabled("test.backup", true);
        Assert(before != BackupAutomation.DataSignature(), "Consent not included in signature");
        string opted = BackupAutomation.DataSignature();
        File.WriteAllText(Path.Combine(data, "history.txt"), "changed private history");
        Assert(opted == BackupAutomation.DataSignature(), "Undeclared data changed signature");
        string zipFile = Path.Combine(Root, "plugins.zip");
        string exe = Path.Combine(Root, "renamed.exe");
        File.WriteAllBytes(exe, new byte[] { 77, 90, 1, 2, 3, 4 });
        string code = Path.Combine(Root, "Plugins", "Installed", "test.backup", "bin", "dependency.dll");
        File.WriteAllText(code, "dependency v1");
        string firstCodeSignature = BackupAutomation.DataSignature();
        File.WriteAllText(code, "dependency v2");
        Assert(firstCodeSignature != BackupAutomation.DataSignature(), "Plugin dependency omitted from fingerprint");
        BackupStore.ExportAll(zipFile, Loaded(), Reminders("B"), exe);
        using (ZipArchive zip = ZipFile.OpenRead(zipFile))
        {
            Assert(zip.GetEntry("Program/AppShelf.exe") != null, "Renamed core missing");
            Assert(zip.GetEntry("Plugins/Packages/test.backup.asplugin") != null, "Plugin package missing");
        }
        string extracted = BackupStore.ExtractPrograms(zipFile, Root, true, true);
        Assert(File.ReadAllBytes(Path.Combine(extracted, "Program", "AppShelf.exe")).SequenceEqual(File.ReadAllBytes(exe)), "Wrong core bytes");
        string savedPackage = Path.Combine(extracted, "Plugins", "test.backup.asplugin");
        using (ZipArchive zip = ZipFile.OpenRead(savedPackage))
        {
            Assert(zip.GetEntry("bin/dependency.dll") != null && zip.GetEntry("bin/Test.exe") != null, "Plugin dependencies missing");
            Assert(zip.GetEntry("enabled.flag") == null && zip.GetEntry("approved.flag") == null, "Trust markers leaked");
        }
        PluginPackageStore fresh = new PluginPackageStore(Path.Combine(Root, "fresh-profile"));
        bool asked = false;
        Assert(fresh.Install(savedPackage, delegate { asked = true; return false; }) == null && asked, "Restored package bypassed trust");
        fresh.Install(savedPackage, delegate { return true; });
        Assert(!fresh.List()[0].Enabled, "Restored package started automatically");
        string onlyCore = BackupStore.ExtractPrograms(zipFile, Root, true, false);
        Assert(File.Exists(Path.Combine(onlyCore, "Program", "AppShelf.exe")) && !Directory.Exists(Path.Combine(onlyCore, "Plugins")), "Component selection ignored");
        string onlyPlugins = BackupStore.ExtractPrograms(zipFile, Root, false, true);
        Assert(!Directory.Exists(Path.Combine(onlyPlugins, "Program")) && File.Exists(Path.Combine(onlyPlugins, "Plugins", "test.backup.asplugin")), "Package selection ignored");
        AppConfig excluded = Loaded();
        excluded.BackupExecutable = false; excluded.BackupPluginPackages = false;
        string excludedZip = Path.Combine(Root, "excluded.zip");
        BackupStore.ExportAll(excludedZip, excluded, Reminders("B"), exe);
        using (ZipArchive zip = ZipFile.OpenRead(excludedZip))
            Assert(zip.GetEntry("Program/AppShelf.exe") == null && zip.GetEntry("Plugins/Packages/test.backup.asplugin") == null &&
                zip.GetEntry(PluginPackageStore.BackupArchiveEntry) != null, "Binary opt-out ignored");
        using (ZipArchive zip = ZipFile.OpenRead(zipFile))
        {
            ZipArchiveEntry entry = zip.GetEntry(PluginPackageStore.BackupArchiveEntry);
            Assert(entry != null, "Plugin backup missing");
            using (Stream content = entry.Open())
            using (StreamReader reader = new StreamReader(content))
            {
                string json = reader.ReadToEnd();
                Assert(json.Contains("settings.json") && !json.Contains("history.txt") &&
                    !json.Contains("Test.exe") && !json.Contains("approved.flag"), "Private data or executable leaked");
            }
        }
        File.WriteAllText(settings, "live settings");
        Assert(opted != BackupAutomation.DataSignature(), "Selected data not included in signature");
        BackupStore.RestoreAll(zipFile);
        Assert(File.ReadAllText(settings) == "live settings" && store.List()[0].Enabled,
            "Full restore modified the live plugin or enabled state");
        Assert(store.ReadRecovered().Count == 1 && store.ReadRecovered()[0].Files.Count == 1,
            "Full restore did not stage plugin data");
        byte[] recovery = File.ReadAllBytes(store.RecoveredPath());
        BackupStore.RestoreAll(oldArchive);
        Assert(Convert.ToBase64String(File.ReadAllBytes(store.RecoveredPath())) == Convert.ToBase64String(recovery),
            "Old archive overwrote pending plugin recovery");
        string secondSafety = BackupStore.RestoreAll(zipFile);
        Assert(File.Exists(Path.Combine(secondSafety, PluginPackageStore.RecoveredRelativePath)),
            "Previous staged recovery not saved before restore");
        string invalid = Path.Combine(Root, "invalid-plugins.zip");
        using (ZipArchive original = ZipFile.OpenRead(zipFile))
        using (ZipArchive output = ZipFile.Open(invalid, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry entry in original.Entries)
            {
                ZipArchiveEntry copy = output.CreateEntry(entry.FullName);
                using (Stream target = copy.Open())
                {
                    if (entry.FullName == PluginPackageStore.BackupArchiveEntry)
                    {
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":999,\"plugins\":[]}");
                        target.Write(bytes, 0, bytes.Length);
                    }
                    else using (Stream source = entry.Open()) source.CopyTo(target);
                }
            }
        }
        Throws(delegate { BackupStore.RestoreAll(invalid); });
        Assert(File.ReadAllText(settings) == "live settings" &&
            Convert.ToBase64String(File.ReadAllBytes(store.RecoveredPath())) == Convert.ToBase64String(recovery),
            "Invalid archive touched live or recovered data");
    }

    private static void SamplePluginRoundtrip(string package)
    {
        const string id = "example.notes", version = "1.0.0";
        PluginPackageStore store = new PluginPackageStore(Root);
        bool confirmed = false;
        store.Install(package, delegate(PluginManifest m) {
            confirmed = m.Id == id && m.Version == version;
            return confirmed;
        });
        Assert(confirmed && !store.List()[0].Enabled, "Sample install did not require trust or was enabled automatically");
        string data = store.GetDataDirectory(id);
        string notes = Path.Combine(data, "notes.txt"), settings = Path.Combine(data, "settings.txt");
        // Seed the documented on-disk format. Actual editing and notice delivery are manual UI acceptance.
        File.WriteAllText(notes, "Saved sample note");
        File.WriteAllText(settings, "on");
        File.WriteAllText(Path.Combine(data, "private.txt"), "not declared");
        Assert(!store.BackupDataEnabled(id), "Sample data backup enabled without consent");
        store.SetBackupDataEnabled(id, true);
        using (PluginRuntime runtime = new PluginRuntime(store, 10000))
        {
            runtime.SetEnabledAsync(id, true).GetAwaiter().GetResult();
            Assert(runtime.State(id).Status == "running", "Sample did not start");
            PluginCommand[] commands = runtime.GetCommandsAsync(id, version).GetAwaiter().GetResult();
            Assert(commands.Length == 1 && commands[0].Id == "open", "Sample command not discovered");
            runtime.InvokeAsync(id, version, "open").GetAwaiter().GetResult();
            runtime.OpenSettingsAsync(id, version).GetAwaiter().GetResult();
            Assert(runtime.CheckHealthAsync().GetAwaiter().GetResult().Count == 0, "Unexpected sample notice");
            Assert(runtime.State(id).Status == "running", "Sample did not answer ping/poll");
            string archive = Path.Combine(Root, "sample.zip");
            AppConfig config = Loaded();
            config.BackupPluginPackages = true; config.BackupExecutable = false;
            BackupStore.ExportAll(archive, config, Reminders("B"));
            runtime.RemoveAsync(id).GetAwaiter().GetResult();
            Assert(store.List().Count == 0 && File.ReadAllText(notes) == "Saved sample note", "Removal lost data or left package");
            File.WriteAllText(notes, "Modified after backup");
            File.WriteAllText(settings, "off");
            BackupStore.RestoreAll(archive);
            Assert(store.List().Count == 0 && File.ReadAllText(notes) == "Modified after backup", "ZIP restore installed code or changed live data");
            string extracted = BackupStore.ExtractPrograms(archive, Root, false, true);
            string restored = Path.Combine(extracted, "Plugins", id + ".asplugin");
            bool asked = false;
            Assert(store.Install(restored, delegate { asked = true; return false; }) == null && asked, "Recovered sample skipped trust");
            store.Install(restored, delegate { return true; });
            Assert(!store.List()[0].Enabled && !store.BackupDataEnabled(id), "Recovered sample inherited approval state or backup consent");
            PluginBackupItem recovered = store.ReadRecovered().Single(x => x.Id == id);
            Assert(recovered.DataIncluded && recovered.Files.Count == 2, "Sample declared files missing");
            runtime.RestoreDataAsync(id, version, ExternalBackupRoot()).GetAwaiter().GetResult();
            Assert(!store.List()[0].Enabled && runtime.State(id).Status == "idle", "Restored sample started automatically");
            Assert(File.ReadAllText(notes) == "Saved sample note" && File.ReadAllText(settings) == "on", "Sample data did not roundtrip");
            Assert(File.ReadAllText(Path.Combine(data, "private.txt")) == "not declared", "Undeclared data changed");
            runtime.SetEnabledAsync(id, true).GetAwaiter().GetResult();
            runtime.InvokeAsync(id, version, "open").GetAwaiter().GetResult();
            Assert(runtime.State(id).Status == "running", "Recovered sample failed to launch");
            runtime.SetEnabledAsync(id, false).GetAwaiter().GetResult();
            Assert(runtime.State(id).Status == "idle", "Sample did not stop");
        }
    }

    private static void Case(string name, Action action)
    {
        try { Reset(); action(); passes++; Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + " - " + ex.Message); }
    }
    public static void Run(string samplePackage)
    {
        foreach (bool json in new bool[] { false, true }) foreach (bool existing in new bool[] { false, true })
        foreach (bool fail in new bool[] { false, true })
        {
            bool j = json, e = existing, f = fail;
            Case((json ? "JSON" : "ZIP") + " existing=" + existing + " fail=" + fail, delegate { ExportCase(j, e, f); });
        }
        Case("plugin ZIP inventory, privacy, signature and staged restore", PluginBackupRoundtrip);
        if (!String.IsNullOrEmpty(samplePackage)) Case("real sample plugin install, IPC and ZIP recovery", delegate { SamplePluginRoundtrip(samplePackage); });
        Case("chosen backup directory isolation", InvalidBackupDirectory);
        Case("automatic backup schedule, changed data, retention", AutomaticBackupRetention);
        Case("failed automatic copy preserves existing ZIP", FailedAutomaticPreservesPrevious);
        Case("paired pre-update ZIP, EXE and external restore safety", PreUpdateBackupAndRestoreLocation);
        Case("failed pre-update keeps prior archives", FailedPreUpdateKeepsOldCopies);
        Case("transient data change retries backup", delegate { RetryChangedData(false); });
        Case("persistent data changes stop after three attempts", delegate { RetryChangedData(true); });
        Case("cloud reader locks copied EXE without blocking publication", CloudReaderDoesNotBlockBackupPublication);
        Case("stale incomplete update set cleanup", IncompleteUpdateSetsAreNotRetained);
        Case("legacy .previous moves to selected backup directory", MigrateOldAdjacentExecutable);
        Case("full rollback and retry", delegate { Rollback(false, false); });
        Case("config rollback failure does not stop others", delegate { Rollback(true, false); });
        Case("shortcut rollback failure does not stop others", delegate { Rollback(false, true); });
        Case("multiple rollback failures", delegate { Rollback(true, true); });
        Case("partial rollback blocks stale saves", IncompleteRollbackBlocksSaving);
        Case("complete rollback resumes saving", CompleteRollbackResumesSaving);
        Case(".bak shortcut retention and cleanup", BackupShortcut);
        Case("user shortcut original preserved", OriginalShortcutPreserved);
        Case("2000 entries", delegate { CountLimit(false); });
        Case("2001 entries rejected", delegate { CountLimit(true); });
        Case("10 MiB entry", delegate { SizeLimit(false, false); });
        Case("10 MiB + 1 rejected", delegate { SizeLimit(false, true); });
        Case("40 MiB total", delegate { SizeLimit(true, false); });
        Case("40 MiB + 1 rejected", delegate { SizeLimit(true, true); });
        Case("invalid reminder export rejected", delegate { InvalidExport(true); });
        Case("invalid application export rejected", delegate { InvalidExport(false); });
        Case("native ZIP destination lock", delegate { NativeLock(false); });
        Case("native JSON destination lock", delegate { NativeLock(true); });
        Case("invalid JSON export rejected", InvalidJsonExport);
        Case("oversized apps.json entry rejected", OversizedJsonEntry);
        Case("path traversal rejected", delegate { UnsafeImport("../escape.url"); });
        Case("duplicate entry rejected", delegate { UnsafeImport("apps.json"); });
        Case("unknown entry rejected", delegate { UnsafeImport("unexpected.txt"); });
        Case("image import: valid, repeated, partial write, access failure and invalid image", ImageImport);
        foreach (string kind in new string[] { "empty", "text", "corrupt-jpg", "mismatch", "dimensions", "pixels",
            "truncated", "bytes", "valid", "jpg", "jpeg", "copy-failure", "save-failure", "conflict" })
        {
            string scenario = kind;
            Case("wallpaper ZIP " + scenario, delegate { ImageRestore(scenario); });
        }
        Case("cover PNG and shortcuts roundtrip", delegate { CoverRestore(false, false); });
        Case("corrupt cover rejected before restore", delegate { CoverRestore(true, false); });
        Case("partial cover copy is removed", delegate { CoverRestore(false, true); });
        Console.WriteLine("Backup scenarios passed=" + passes + "; failed=" + failures);
        if (failures != 0) throw new Exception("Failed scenarios: " + failures);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $true
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll',
        'System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xml.dll','System.Xaml.dll',
        'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $result.CompiledAssembly.GetType('BackupTests').GetMethod('Run').Invoke($null, [object[]]@([string]$samplePackage))
    Write-Output "PASS: backup reliability tests. Isolated data at $testRoot"
}
finally {
    $provider.Dispose()
    $env:APPSHELF_TEST_DATA = $previous
}
