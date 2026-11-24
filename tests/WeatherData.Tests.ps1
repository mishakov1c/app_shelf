# Windows PowerShell 5.1 -STA. Every JSON/request is controlled; no network is permitted.
param([switch]$Baseline, [switch]$WithoutIcu)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
if ($Baseline -and $WithoutIcu) { throw 'Missing ICU checks require the current implementation.' }
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path $env:LOCALAPPDATA ('Temp\opencode\AppShelf-WeatherData-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$previous = $env:APPSHELF_TEST_DATA
$env:APPSHELF_TEST_DATA = $temp
try {
    $sources = @()
    foreach ($file in @('AppShelf.cs','AppShelfExtras.cs','FluentUi.cs','Reminders.cs','WeatherProviders.cs','BackupManager.cs','SiteLinks.cs','WindowsIntegration.cs','Collections.cs','CollectionsWindow.cs','AppUpdate.cs','AppUpdateWindow.cs','PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs')) {
        if ($Baseline -and $file -in @('PluginPackages.cs','PluginManagerWindow.cs','PluginProtocol.cs','PluginRuntime.cs')) { continue }
    if ($Baseline -and $file -in @('AppUpdate.cs','AppUpdateWindow.cs')) { continue }
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        if ($Baseline -and $file -in @('Collections.cs','CollectionsWindow.cs')) { continue }
        if ($Baseline -and $file -in @('AppShelf.cs','WeatherProviders.cs')) {
            $git = New-Object Diagnostics.Process
            $git.StartInfo.FileName = 'git'
            $git.StartInfo.WorkingDirectory = $root
            $git.StartInfo.Arguments = "show 023201a6a715b6a0d1290c72782d06a57490b9d0:$file"
            $git.StartInfo.UseShellExecute = $false
            $git.StartInfo.RedirectStandardOutput = $true
            $git.StartInfo.RedirectStandardError = $true
            $git.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
            try {
                [void]$git.Start()
                $text = $git.StandardOutput.ReadToEnd()
                [void]$git.StandardError.ReadToEnd()
                $git.WaitForExit()
                if ($git.ExitCode -ne 0) { throw 'Cannot read baseline.' }
            } finally { $git.Dispose() }
        }
        if ($file -eq 'AppShelf.cs') {
            $storage = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppShelf"'
            if ($text.Split(@($storage), [StringSplitOptions]::None).Length -ne 2) { throw 'Data isolation anchor missing.' }
            $text = $text.Replace($storage, 'Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"')
            $start = $text.IndexOf('        private static async Task<T> ReadJson<T>')
            # Preserve the HTTP-cache parser inserted between ReadJson and ReadJsonWorker.
            # Older baseline sources do not have it; keep their original boundary.
            $end = $text.IndexOf('        private static T ParseMetCachedJson<T>', $start)
            if ($end -lt 0) { $end = $text.IndexOf('        private static T ReadJsonWorker<T>', $start) }
            if ($start -lt 0 -or $end -le $start) { throw 'JSON interception anchor missing.' }
            $jsonMethod = @'
        private static Task<T> ReadJson<T>(string url, string stage, bool allowTlsRetry = true)
        { return WeatherTests.Json<T>(url); }

'@
            if ($Baseline) { $jsonMethod = $jsonMethod.Replace('bool allowTlsRetry = true)', 'bool allowTlsRetry = true, string apiKey = null)') }
            $text = $text.Substring(0, $start) + $jsonMethod + $text.Substring($end)
            $text = $text.Replace('await WeatherClient.GetForecast(city, provider)', 'await WeatherTests.Request(city, provider)')
            $text = $text.Replace('bool? savedSettings = dialog.ShowDialog();',
                'bool? savedSettings = WeatherTests.Settings(dialog);')
        }
        if ($file -eq 'WeatherProviders.cs') {
            $text = $text.Replace('DateTimeOffset.UtcNow', 'WeatherTests.Now')
            if ($WithoutIcu) {
                $anchor = '[DllImport("icu.dll",'
                if ($text.Split(@($anchor), [StringSplitOptions]::None).Length -ne 2) { throw 'ICU import anchor missing.' }
                # Force a real loader failure in this test assembly, without touching Windows DLLs.
                $text = $text.Replace($anchor, '[DllImport("AppShelf-Test-Missing-Icu.dll",')
            }
        }
        if ($file -eq 'BackupManager.cs') {
            $text = $text.Replace('ReminderStore.Save(reminders);', 'WeatherTests.SaveImported(reminders);')
            $text = $text.Replace('File.Copy(', 'WeatherTests.Copy(')
        }
        $text = $text.Replace('WindowsStartup.Apply(enable, hidden);', '').Replace('WindowsStartup.WriteRaw(previousRunValue);', '')
        $text = $text.Replace('System.Media.SystemSounds.Asterisk.Play();', '').Replace('System.Media.SystemSounds.Exclamation.Play();', '')
        $sources += $text.Replace('SiteLinks.FetchFavicon(site)', 'false') # No live websites in legacy tests.
    }
    $sources += @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AppShelf;

public static class WeatherRestartProbe
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { WeatherTests.RestartProbe(args[0]); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

public static class WeatherTests
{
    public static DateTimeOffset Now = DateTimeOffset.Parse("2026-03-08T03:00:00Z", CultureInfo.InvariantCulture);
    private static string met, open, geo;
    private static readonly List<TaskCompletionSource<WeatherSnapshot>> requests = new List<TaskCompletionSource<WeatherSnapshot>>();
    private static readonly List<string> requestKeys = new List<string>();
    private static int passed, failed;
    private static string settingsChoice;
    private static bool failImport, failRollback;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void SaveImported(ReminderDocument reminders)
    {
        if (failImport) throw new IOException("injected-legacy-import-failure");
        ReminderStore.Save(reminders);
    }
    public static void Copy(string source, string target) { Copy(source, target, false); }
    public static void Copy(string source, string target, bool overwrite)
    {
        if (failRollback && target == ConfigStore.FilePath && source.StartsWith(Path.Combine(ConfigStore.DirectoryPath, "Backups") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("injected-legacy-rollback-failure");
        File.Copy(source, target, overwrite);
    }
    public static Task<T> Json<T>(string url)
    {
        string json;
        if (url.StartsWith("https://geocoding-api.open-meteo.com/")) json = geo;
        else if (url.StartsWith("https://api.met.no/")) json = met;
        else if (url.StartsWith("https://api.open-meteo.com/")) json = open;
        else throw new Exception("Unexpected URL: " + url);
        using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            return Task.FromResult((T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream));
    }
    public static Task<WeatherSnapshot> Request(string city, string provider)
    {
        TaskCompletionSource<WeatherSnapshot> pending = new TaskCompletionSource<WeatherSnapshot>();
        requests.Add(pending); requestKeys.Add(city + "/" + provider);
        return pending.Task;
    }
    private static Exception UnexpectedWeatherError()
    {
        try { throw new InvalidDataException("malformed-json-diagnostic-marker"); }
        catch (InvalidDataException error) { return error; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static IEnumerable<DependencyObject> Controls(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            DependencyObject item = child as DependencyObject;
            if (item != null) foreach (DependencyObject nested in Controls(item)) yield return nested;
        }
    }
    public static bool Settings(Window dialog)
    {
        ComboBox provider = null;
        foreach (DependencyObject item in Controls(dialog))
        {
            Assert(!(item is PasswordBox), "Removed API-key control is still present");
            if (item is ComboBox && ((ComboBox)item).Items.Count == 2) provider = (ComboBox)item;
            Button button = item as Button;
            if (button != null) Assert(!Convert.ToString(button.Content).Contains("Яндекс"), "Removed site button is still present");
        }
        Assert(provider != null && provider.Items.Count == 2, "Expected exactly two free providers");
        HashSet<string> keys = new HashSet<string>();
        foreach (ComboBoxItem item in provider.Items)
        {
            string key = (string)item.Tag; keys.Add(key);
            if (key == settingsChoice) provider.SelectedItem = item;
        }
        Assert(keys.SetEquals(new string[] { "metno", "openmeteo" }), "Unexpected provider choice");
        return settingsChoice != null;
    }
    private static void Missing(object value, string name) { Assert(value == null, name + " substituted a numeric value: " + value); }
    private static void Number(object value, double expected, string name) { Assert(value != null && Convert.ToDouble(value, CultureInfo.InvariantCulture) == expected, name + " expected " + expected + ", got " + value); }
    private static void Invalid(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected an explicit incomplete-data error");
    }
    private static WeatherSnapshot Forecast(string provider) { return WeatherClient.GetForecast("Test city", provider).GetAwaiter().GetResult(); }
    private static string Step(string time, string values)
    {
        return "{\"time\":\"" + time + "\",\"data\":{\"instant\":{\"details\":{" + values + "}},\"next_1_hours\":{\"summary\":{\"symbol_code\":\"clearsky_day\"}}}}";
    }
    private static string Met(params string[] steps) { return "{\"properties\":{\"timeseries\":[" + String.Join(",", steps) + "]}}"; }
    private static void Seed()
    {
        met = Met(Step("2026-03-08T06:00:00Z", "\"air_temperature\":0,\"apparent_air_temperature\":-2"));
        open = "{\"current\":{\"temperature_2m\":0,\"apparent_temperature\":-2,\"weather_code\":0},\"daily\":{\"time\":[\"2026-03-08\"],\"temperature_2m_min\":[-5],\"temperature_2m_max\":[4],\"apparent_temperature_min\":[-6],\"apparent_temperature_max\":[0],\"weather_code\":[0]}}";
        geo = "{\"results\":[{\"name\":\"Test city\",\"latitude\":40.7,\"longitude\":-74,\"timezone\":\"America/New_York\"}]}";
        requests.Clear(); requestKeys.Clear();
        settingsChoice = null;
        failImport = failRollback = false;
        if (File.Exists(WeatherDiskCache.FilePath)) File.Delete(WeatherDiskCache.FilePath);
    }
    private static object Get(MainWindow window, string name) { return typeof(MainWindow).GetField(name, Private).GetValue(window); }
    private static void Set(MainWindow window, string name, object value)
    {
        FieldInfo field = typeof(MainWindow).GetField(name, Private);
        if (field != null) field.SetValue(window, value);
    }
    private static object Call(MainWindow window, string name, params object[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, Private).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Drain() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { })); }
    private static void RightClickWeather(MainWindow window, FrameworkElement indicator, string mode)
    {
        Assert(indicator.ContextMenu != null, mode + ": weather menu is missing");
        Border panel = (Border)Get(window, "_monitorBorder");
        ContextMenu panelMenu = panel.ContextMenu;
        Assert(panelMenu != null, mode + ": original lower-panel settings menu missing");
        MouseButtonEventArgs click = new MouseButtonEventArgs(
            Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right);
        click.RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent;
        indicator.RaiseEvent(click);
        Assert(click.Handled, mode + ": right click escaped to the lower-panel container");
        Assert(indicator.ContextMenu.IsOpen, mode + ": weather menu did not open on right click");
        Assert(!panelMenu.IsOpen, mode + ": lower-panel settings menu opened instead of weather");
        foreach (string source in new[] { "Open-Meteo", "MET Norway" }) {
            MenuItem choice = null;
            foreach (object item in indicator.ContextMenu.Items) {
                MenuItem candidate = item as MenuItem;
                if (candidate != null && Convert.ToString(candidate.Header) == source) choice = candidate;
            }
            Assert(choice != null, mode + ": provider choice missing");
            choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Drain();
            string key = source == "MET Norway" ? "metno" : "openmeteo";
            string warning; bool safe;
            Assert(((AppConfig)Get(window, "_config")).WeatherProvider == key &&
                ConfigStore.Load(out warning, out safe).WeatherProvider == key && safe,
                mode + ": source selection did not reach live and saved settings");
            Assert(!panelMenu.IsOpen, mode + ": source selection opened the lower-panel menu");
        }
        indicator.ContextMenu.IsOpen = false;
        Drain();
    }
    private static MainWindow Window(bool enableWeatherTimer = false)
    {
        ConfigStore.Save(new AppConfig { ShowWeather = false, ShowNetwork = false, MinimizeToTray = false });
        MainWindow window = new MainWindow();
        window.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        Set(window, "_networkTimer", new DispatcherTimer());
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); Drain();
        // The regular test harness suppresses StartMonitors; warm-start tests need
        // a real weather timer to verify the remaining provider cooldown.
        if (enableWeatherTimer)
            Set(window, "_weatherTimer", new DispatcherTimer { Interval = TimeSpan.FromHours(3) });
        Change(window, "A", "metno");
        return window;
    }
    private static void Change(MainWindow window, string city, string provider)
    {
        AppConfig pending = ((AppConfig)Get(window, "_config")).Copy();
        pending.ShowWeather = true; pending.WeatherCity = city; pending.WeatherProvider = provider;
        Assert((bool)Call(window, "SaveConfigChanges", pending, false), "Cannot save isolated settings");
        Call(window, "RefreshWeather");
    }
    public static void RestartProbe(string provider)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"),
            "Restart data isolation failed");
        string warning; bool safe;
        AppConfig config = ConfigStore.Load(out warning, out safe);
        Assert(safe && warning == null && config.WeatherProvider == provider && config.WeatherCity == "A",
            "Saved provider or city did not survive process restart");
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        FluentUi.Install(app);
        MainWindow window = new MainWindow();
        window.SourceInitialized -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
            typeof(MainWindow).GetMethod("OnWindowSourceInitialized", Private));
        Set(window, "_networkTimer", new DispatcherTimer());
        window.ShowInTaskbar = false; window.Show(); Drain();
        try {
            Call(window, "RefreshWeather");
            Assert(requests.Count == 1, "Offline startup did not attempt the eligible refresh");
            Assert(Get(window, "_lastWeather") != null && (bool)Get(window, "_lastWeatherIsCached"),
                "Cache did not survive process restart");
            requests[0].SetException(new IOException("isolated-offline-restart")); Drain();
            WeatherSnapshot forecast = (WeatherSnapshot)Get(window, "_lastWeather");
            Assert(forecast != null && forecast.Source == (provider == "metno" ? "MET Norway" : "Open-Meteo") &&
                forecast.Days.Count == 7 && forecast.Days[6].Max == 6,
                "Offline restart lost the selected provider's seven-day forecast");
            Assert(((TextBlock)Get(window, "_weather")).Text.Contains("сохранено"),
                "Offline restarted UI did not label the saved forecast");
            if (provider == "metno") {
                Call(window, "RefreshWeather");
                Assert(requests.Count == 1, "Offline MET startup bypassed the request cooldown");
            }
        } finally { window.Close(); app.Shutdown(); }
    }
    private static WeatherSnapshot Snapshot(string city, string source)
    {
        return new WeatherSnapshot { City = city, Source = source, Temperature = 0, ApparentTemperature = -1, UpdatedAt = DateTime.Now };
    }
    private static void Switch(bool city, bool error)
    {
        MainWindow window = Window();
        try
        {
            Assert(requests.Count == 1, "Initial request missing");
            Change(window, city ? "B" : "A", city ? "metno" : "openmeteo");
            if (error) requests[0].SetException(new IOException("old-request-error"));
            else requests[0].SetResult(Snapshot("A", "MET Norway"));
            Drain();
            Assert(Get(window, "_lastWeatherError") == null, "Old error contaminated new settings");
            Assert(Get(window, "_lastWeather") == null, "Old result contaminated new settings");
            Assert(requests.Count == 2, "New settings were not fetched after the old request");
            requests[1].SetResult(Snapshot(city ? "B" : "A", city ? "MET Norway" : "Open-Meteo"));
            Drain();
            Assert(((WeatherSnapshot)Get(window, "_lastWeather")).City == (city ? "B" : "A"), "New result not applied");
            Assert(requests.Count == 2 && !(bool)Get(window, "_weatherPending"), "Completion started an endless refresh");
        }
        finally { window.Close(); }
    }
    private static void Case(string name, Action action)
    {
        try { Seed(); action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static TimeZoneInfo Zone(string id)
    {
        try { return (TimeZoneInfo)typeof(WeatherClient).GetMethod("ResolveWeatherTimeZone", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { id }); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Offset(TimeZoneInfo zone, string utc, int hours)
    {
        DateTimeOffset instant = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);
        Assert(TimeZoneInfo.ConvertTime(instant, zone).Offset == TimeSpan.FromHours(hours), zone.Id + " wrong offset at " + utc);
    }
    private static void ZoneChecks()
    {
        Assert(Environment.Version.Major == 4, "These checks must exercise .NET Framework, not modern .NET");
        foreach (string id in new string[] { "Asia/Novokuznetsk", "Europe/Berlin", "America/New_York" })
        {
            string name = id;
            Case("Framework native ICU mapping " + name, delegate {
                bool needsMapping = false;
                try { TimeZoneInfo.FindSystemTimeZoneById(name); } catch (TimeZoneNotFoundException) { needsMapping = true; }
                Assert(needsMapping, "IANA ID resolved directly; ICU path was not verified");
                TimeZoneInfo zone = Zone(name);
                string expected = name == "Asia/Novokuznetsk" ? "North Asia Standard Time" : name == "Europe/Berlin" ? "W. Europe Standard Time" : "Eastern Standard Time";
                Assert(zone.Id == expected, "Unexpected Windows ID: " + zone.Id);
                int winter = name == "Asia/Novokuznetsk" ? 7 : name == "Europe/Berlin" ? 1 : -5;
                int summer = name == "Asia/Novokuznetsk" ? 7 : name == "Europe/Berlin" ? 2 : -4;
                Offset(zone, "2026-01-15T12:00:00Z", winter); Offset(zone, "2026-07-15T12:00:00Z", summer);
                Console.WriteLine("MAPPING " + name + " -> " + zone.Id);
            });
        }
        Case("Berlin spring DST transition", delegate { TimeZoneInfo z = Zone("Europe/Berlin"); Offset(z,"2026-03-29T00:59:00Z",1); Offset(z,"2026-03-29T01:00:00Z",2); });
        Case("Berlin autumn DST transition", delegate { TimeZoneInfo z = Zone("Europe/Berlin"); Offset(z,"2026-10-25T00:59:00Z",2); Offset(z,"2026-10-25T01:00:00Z",1); });
        Case("New York spring DST transition", delegate { TimeZoneInfo z = Zone("America/New_York"); Offset(z,"2026-03-08T06:59:00Z",-5); Offset(z,"2026-03-08T07:00:00Z",-4); });
        Case("New York autumn DST transition", delegate { TimeZoneInfo z = Zone("America/New_York"); Offset(z,"2026-11-01T05:59:00Z",-4); Offset(z,"2026-11-01T06:00:00Z",-5); });
    }
    public static void RunWithoutIcu()
    {
        Assert(Environment.Version.Major == 4, "Missing ICU checks must exercise .NET Framework");
        foreach (string id in new string[] { "Asia/Novokuznetsk", "Europe/Berlin", "America/New_York" })
        {
            string name = id;
            Case("missing ICU fails closed for " + name, delegate {
                try { Zone(name); }
                catch (InvalidDataException ex) { Assert(ex.InnerException is DllNotFoundException, "Not a real DLL loader failure"); return; }
                throw new Exception("Missing ICU silently substituted a timezone");
            });
        }
        Case("missing ICU still permits built-in Windows zone", delegate { Offset(Zone("North Asia Standard Time"), "2026-03-08T00:00:00Z", 7); });
        Console.WriteLine("Missing ICU scenarios passed=" + passed + "; failed=" + failed);
        if (failed != 0) throw new Exception("Missing ICU checks failed");
    }
    private static void LegacyRollback(bool incomplete)
    {
        AppConfig live = new AppConfig { WeatherProvider = "yandex", WeatherCity = "Before import", ShowNetwork = false };
        ConfigStore.Save(live); ConfigStore.Save(live);
        ReminderStore.Save(new ReminderDocument()); ReminderStore.Save(new ReminderDocument());
        string[] names = { "apps.json", "apps.json.bak", "reminders.json", "reminders.json.bak" };
        Dictionary<string,string> before = new Dictionary<string,string>();
        foreach (string name in names) before[name] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(ConfigStore.DirectoryPath,name)));
        string key = Path.Combine(ConfigStore.DirectoryPath, "yandex-weather-key.dpapi");
        File.WriteAllBytes(key, new byte[] { 9,8,7,6 }); DateTime stamp = File.GetLastWriteTimeUtc(key);
        string zip = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "rollback-" + incomplete + ".zip");
        BackupStore.ExportAll(zip, new AppConfig { WeatherProvider = "yandex", WeatherCity = "After import", ShowNetwork = false }, new ReminderDocument());
        BackupRestoreException failure = null;
        failImport = true; failRollback = incomplete;
        try { BackupStore.RestoreAll(zip); }
        catch (BackupRestoreException ex) { failure = ex; }
        finally { failImport = failRollback = false; }
        Assert(failure != null && failure.InnerException.Message == "injected-legacy-import-failure", "Original import failure lost");
        Assert(failure.RollbackErrors.Count == (incomplete ? 1 : 0), "Rollback failures differ");
        foreach (string name in names)
        {
            Assert(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(failure.SafetyCopyDirectory,name))) == before[name], "Safety copy changed " + name);
            if (!incomplete || name != "apps.json") Assert(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(ConfigStore.DirectoryPath,name))) == before[name], "Rollback changed " + name);
        }
        string warning; bool safe;
        AppConfig loaded = ConfigStore.Load(out warning, out safe);
        Assert(safe && loaded.WeatherProvider == "metno" && loaded.WeatherCity == (incomplete ? "After import" : "Before import"), "Legacy rollback config migration differs");
        BackupStore.RestoreAll(zip);
        loaded = ConfigStore.Load(out warning, out safe);
        Assert(safe && loaded.WeatherProvider == "metno" && loaded.WeatherCity == "After import", "Retry after legacy rollback failed");
        Assert(Convert.ToBase64String(File.ReadAllBytes(key)) == Convert.ToBase64String(new byte[] { 9,8,7,6 }) && File.GetLastWriteTimeUtc(key) == stamp, "Rollback/retry changed the retired key");
    }
    public static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Assert(ConfigStore.DirectoryPath == Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "AppShelf"), "Data isolation failed");
        ZoneChecks();
        Case("MET absent feels-like current and daily", delegate {
            met = Met(Step("2026-03-08T06:00:00Z", "\"air_temperature\":5"));
            WeatherSnapshot s = Forecast("metno"); Missing(s.ApparentTemperature, "current feels-like"); Missing(s.Days[0].ApparentMin, "daily feels-like");
        });
        Case("MET present feels-like remains valid", delegate { WeatherSnapshot s = Forecast("metno"); Number(s.ApparentTemperature, -2, "feels-like"); });
        foreach (string provider in new string[] { "metno", "openmeteo" })
        {
            string p = provider;
            Case(p + " real zero and absent precipitation", delegate {
                WeatherSnapshot s = Forecast(p); Number(s.Temperature, 0, "zero temperature"); Missing(s.Days[0].PrecipitationProbability, "precipitation");
            });
            Case(p + " missing mandatory current temperature", delegate {
                met = met.Replace("\"air_temperature\":0,", ""); open = open.Replace("\"temperature_2m\":0,", "");
                Invalid(delegate { Forecast(p); });
            });
            Case(p + " negative temperature", delegate {
                met = met.Replace("\"air_temperature\":0", "\"air_temperature\":-12"); open = open.Replace("\"temperature_2m\":0", "\"temperature_2m\":-12");
                Number(Forecast(p).Temperature, -12, "negative temperature");
            });
            Case(p + " empty response", delegate { met = open = "{}"; Invalid(delegate { Forecast(p); }); });
            Case(p + " real zero precipitation", delegate {
                met = met.Replace("\"symbol_code\":\"clearsky_day\"}", "\"symbol_code\":\"clearsky_day\"},\"details\":{\"probability_of_precipitation\":0}");
                open = open.Replace("\"daily\":{", "\"daily\":{\"precipitation_probability_max\":[0],");
                Number(Forecast(p).Days[0].PrecipitationProbability, 0, "zero precipitation");
            });
        }
        Case("Open-Meteo missing optional feels-like", delegate { open = open.Replace("\"apparent_temperature\":-2,", ""); Missing(Forecast("openmeteo").ApparentTemperature, "feels-like"); });
        Case("Open-Meteo absent daily feels-like arrays", delegate {
            open = open.Replace(",\"apparent_temperature_min\":[-6]", "").Replace(",\"apparent_temperature_max\":[0]", "");
            Missing(Forecast("openmeteo").Days[0].ApparentMin, "daily feels-like");
        });
        Case("Open-Meteo null daily extrema", delegate { open = open.Replace("[-5]", "[null]").Replace("[4]", "[null]"); WeatherDay d = Forecast("openmeteo").Days[0]; Missing(d.Min, "daily min"); Missing(d.Max, "daily max"); });
        Case("Open-Meteo missing condition is not clear sky", delegate { open = open.Replace(",\"weather_code\":0", ""); Assert(Forecast("openmeteo").Condition != "ясно", "Missing code became clear sky"); });
        Case("MET city timezone and spring DST midnight", delegate {
            met = Met(Step("2026-03-08T04:30:00Z", "\"air_temperature\":-10"), Step("2026-03-08T05:30:00Z", "\"air_temperature\":0"), Step("2026-03-09T03:30:00Z", "\"air_temperature\":9"), Step("2026-03-09T04:30:00Z", "\"air_temperature\":2"));
            WeatherSnapshot s = Forecast("metno"); Assert(s.Days.Count == 3 && s.Days[0].Date == new DateTime(2026,3,7), "Wrong city calendar dates"); Number(s.Days[1].Max, 9, "DST-aware max"); Number(s.Days[2].Max, 2, "next day max");
        });
        Case("MET missing city timezone fails safely", delegate { geo = geo.Replace(",\"timezone\":\"America/New_York\"", ""); Invalid(delegate { Forecast("metno"); }); });
        Case("MET unknown city timezone fails safely", delegate { geo = geo.Replace("America/New_York", "Invalid/NoSuchZone"); Invalid(delegate { Forecast("metno"); }); });
        Case("built-in Kemerovo keeps its UTC+7 calendar", delegate {
            WeatherSnapshot s = WeatherClient.GetForecast("Кемерово", "metno").GetAwaiter().GetResult();
            Assert(s.City == "Кемерово" && s.Days[0].Date == new DateTime(2026,3,8), "Kemerovo date changed"); Number(s.Temperature, 0, "Kemerovo zero");
        });
        Case("Open-Meteo does not require geocoder timezone for server-local dates", delegate {
            geo = geo.Replace(",\"timezone\":\"America/New_York\"", ""); Assert(Forecast("openmeteo").Days[0].Date == new DateTime(2026,3,8), "Open-Meteo local date changed");
        });
        Case("MET autumn DST midnight", delegate {
            Now = DateTimeOffset.Parse("2026-11-01T03:00:00Z", CultureInfo.InvariantCulture);
            met = Met(Step("2026-11-01T03:30:00Z", "\"air_temperature\":-2"), Step("2026-11-01T04:30:00Z", "\"air_temperature\":0"), Step("2026-11-02T04:30:00Z", "\"air_temperature\":8"), Step("2026-11-02T05:30:00Z", "\"air_temperature\":1"));
            try { WeatherSnapshot s = Forecast("metno"); Assert(s.Days.Count == 3 && s.Days[0].Date == new DateTime(2026,10,31), "Wrong autumn dates"); Number(s.Days[1].Max, 8, "25-hour day maximum"); }
            finally { Now = DateTimeOffset.Parse("2026-03-08T03:00:00Z", CultureInfo.InvariantCulture); }
        });
        Case("MET partial daily feels-like is not a complete range", delegate {
            met = Met(Step("2026-03-08T06:00:00Z", "\"air_temperature\":0,\"apparent_air_temperature\":-2"), Step("2026-03-08T07:00:00Z", "\"air_temperature\":5"));
            Missing(Forecast("metno").Days[0].ApparentMax, "partial feels-like range");
        });
        Case("MET later missing daily temperature does not become zero", delegate {
            met = Met(Step("2026-03-08T06:00:00Z", "\"air_temperature\":4"), Step("2026-03-08T07:00:00Z", "\"apparent_air_temperature\":-2"));
            WeatherDay d = Forecast("metno").Days[0]; Missing(d.Min, "incomplete daily minimum"); Missing(d.Max, "incomplete daily maximum");
        });
        Case("MET missing first instant cannot be replaced by a later observation", delegate {
            met = "{\"properties\":{\"timeseries\":[{\"time\":\"2026-03-08T06:00:00Z\",\"data\":{\"instant\":{}}}," + Step("2026-03-08T07:00:00Z", "\"air_temperature\":5") + "]}}";
            Invalid(delegate { Forecast("metno"); });
        });
        Case("MET invalid time cannot silently disappear from the daily range", delegate {
            met = Met(Step("not-a-time", "\"air_temperature\":-20"), Step("2026-03-08T07:00:00Z", "\"air_temperature\":5"));
            Invalid(delegate { Forecast("metno"); });
        });
        Case("Open-Meteo short arrays preserve missing daily values", delegate {
            open = open.Replace("[\"2026-03-08\"]", "[\"2026-03-08\",\"2026-03-09\"]");
            WeatherSnapshot s = Forecast("openmeteo"); Number(s.Days[0].Min, -5, "first day min"); Missing(s.Days[1].Min, "missing second day min"); Missing(s.Days[1].PrecipitationProbability, "second day precipitation");
        });
        Case("temperature formatting distinguishes unavailable, zero and negative", delegate {
            MethodInfo format = typeof(WeatherSnapshot).GetMethod("TemperatureText");
            Assert((string)format.Invoke(null, new object[] { null }) == "н/д", "Unavailable value formatted as zero");
            Assert(WeatherSnapshot.TemperatureText(0).StartsWith("0 ") && WeatherSnapshot.TemperatureText(-12).StartsWith("-12 "), "Real temperatures changed");
        });
        Case("forecast does not label a future first date as today", delegate {
            WeatherSnapshot s = Snapshot("A", "test"); s.Days.Add(new WeatherDay { Date = DateTime.Today.AddDays(1), Min = 0, Max = 3 });
            WeatherForecastWindow w = new WeatherForecastWindow(delegate {}, delegate {});
            try {
                w.UpdateForecast(s, "A", null, false);
                StackPanel details = (StackPanel)typeof(WeatherForecastWindow).GetField("_details", Private).GetValue(w);
                Grid row = (Grid)((Border)details.Children[1]).Child;
                Assert(((TextBlock)row.Children[0]).Text != "Сегодня", "A future date is shown as today");
            } finally { w.Close(); }
        });
        Case("missing geocoding coordinates are not zero", delegate { geo = geo.Replace("\"latitude\":40.7,", ""); Invalid(delegate { Forecast("openmeteo"); }); });
        Case("disk cache restores current and daily forecast without internet", delegate {
            WeatherSnapshot original = Snapshot("A", "MET Norway");
            original.Days.Add(new WeatherDay { Date = DateTime.Today, Min = -6, Max = 2, ApparentMin = -9, PrecipitationProbability = 40 });
            DateTime savedAt = DateTime.UtcNow.AddMinutes(-30);
            WeatherDiskCache.Save("A", "metno", original, savedAt);
            WeatherSnapshot restored; DateTime restoredAt;
            Assert(WeatherDiskCache.TryLoad("a", "metno", DateTime.UtcNow, out restored, out restoredAt), "Recent matching cache was not restored");
            Assert(restoredAt == savedAt && restored.Source == "MET Norway" && restored.Temperature == 0,
                "Metadata or current weather was changed");
            Assert(restored.Days.Count == 1 && restored.Days[0].Min == -6 && restored.Days[0].PrecipitationProbability == 40,
                "Cached weekly data was lost");
            Assert(!File.Exists(WeatherDiskCache.FilePath + ".tmp"), "Atomic save left temporary file");
        });
        Case("cache keys on selected city even when geocoding translates its name", delegate {
            WeatherSnapshot translated = Snapshot("Кемерово", "MET Norway");
            WeatherDiskCache.Save("Kemerovo", "metno", translated, DateTime.UtcNow);
            WeatherSnapshot result; DateTime saved;
            Assert(WeatherDiskCache.TryLoad("Kemerovo", "metno", DateTime.UtcNow, out result, out saved),
                "Translated city title invalidated a matching cache key");
            Assert(result.City == "Кемерово", "City display name was changed");
            Assert(!WeatherDiskCache.TryLoad("Новокузнецк", "metno", DateTime.UtcNow, out result, out saved),
                "Translated city cache leaked to other settings");
        });
        Case("disk cache rejects mismatched city, source and 24-hour expiry", delegate {
            WeatherSnapshot original = Snapshot("A", "MET Norway");
            DateTime now = DateTime.UtcNow;
            WeatherDiskCache.Save("A", "metno", original, now.AddHours(-23));
            WeatherSnapshot found; DateTime stored;
            Assert(!WeatherDiskCache.TryLoad("B", "metno", now, out found, out stored), "Other city leaked");
            Assert(!WeatherDiskCache.TryLoad("A", "openmeteo", now, out found, out stored), "Other provider leaked");
            Assert(WeatherDiskCache.TryLoad("A", "metno", now, out found, out stored), "23-hour cache should be valid");
            Assert(!WeatherDiskCache.TryLoad("A", "metno", now.AddHours(1), out found, out stored), "24-hour cache must expire");
            WeatherDiskCache.Save("A", "metno", original, now.AddMinutes(2));
            Assert(!WeatherDiskCache.TryLoad("A", "metno", now, out found, out stored), "Future-dated cache was accepted");
        });
        Case("weather cache retains different providers and cities independently", delegate {
            DateTime now = DateTime.UtcNow;
            WeatherDiskCache.Save("A", "metno", Snapshot("City A MET", "MET Norway"), now.AddMinutes(-40));
            WeatherDiskCache.Save("A", "openmeteo", Snapshot("City A Open", "Open-Meteo"), now.AddMinutes(-30));
            WeatherDiskCache.Save("B", "metno", Snapshot("City B MET", "MET Norway"), now.AddMinutes(-20));
            WeatherSnapshot found; DateTime at;
            Assert(WeatherDiskCache.TryLoad("A", "metno", now, out found, out at) &&
                found.Source == "MET Norway" && found.City == "City A MET", "MET cache lost when switching source");
            Assert(WeatherDiskCache.TryLoad("A", "openmeteo", now, out found, out at) &&
                found.Source == "Open-Meteo" && found.City == "City A Open", "Open-Meteo cache lost after city change");
            Assert(WeatherDiskCache.TryLoad("B", "metno", now, out found, out at) &&
                found.Source == "MET Norway" && found.City == "City B MET", "Other city's cache was overwritten");
            WeatherDiskCache.Save("A", "metno", Snapshot("Fresh A", "MET Norway"), now.AddMinutes(-10));
            Assert(WeatherDiskCache.TryLoad("A", "metno", now, out found, out at) &&
                found.City == "Fresh A", "Old cache was not replaced");
            Assert(WeatherDiskCache.TryLoad("A", "openmeteo", now, out found, out at) &&
                found.City == "City A Open", "Updating MET erased Open-Meteo cache");
        });
        Case("weather cache prunes expired records before preserving other sources", delegate {
            DateTime now = DateTime.UtcNow;
            WeatherDiskCache.Save("A", "metno", Snapshot("Expired", "MET Norway"), now.AddHours(-25));
            WeatherDiskCache.Save("A", "openmeteo", Snapshot("Fresh", "Open-Meteo"), now);
            WeatherSnapshot found; DateTime at;
            Assert(!WeatherDiskCache.TryLoad("A", "metno", now, out found, out at),
                "Expired record was revived by saving another provider");
            Assert(WeatherDiskCache.TryLoad("A", "openmeteo", now, out found, out at),
                "Fresh cache was lost while purging expired records");
        });
        Case("corrupt weather cache never blocks loading", delegate {
            File.WriteAllText(WeatherDiskCache.FilePath, "{ broken", Encoding.UTF8);
            WeatherSnapshot found; DateTime stored;
            Assert(!WeatherDiskCache.TryLoad("A", "metno", DateTime.UtcNow, out found, out stored),
                "Corrupt JSON was accepted");
            File.WriteAllBytes(WeatherDiskCache.FilePath, new byte[524289]);
            Assert(!WeatherDiskCache.TryLoad("A", "metno", DateTime.UtcNow, out found, out stored),
                "Oversized file was accepted");
        });
        Case("warm startup displays disk cache and honors MET request cooldown", delegate {
            WeatherSnapshot original = Snapshot("A", "MET Norway");
            WeatherDiskCache.Save("A", "metno", original, DateTime.UtcNow.AddMinutes(-10));
            MainWindow window = Window(true);
            try {
                Assert(requests.Count == 0, "Warm restart contacted MET too early");
                TimeSpan untilRefresh = ((DispatcherTimer)Get(window, "_weatherTimer")).Interval;
                Assert(untilRefresh > TimeSpan.FromHours(2) && untilRefresh < TimeSpan.FromHours(3),
                    "Warm restart delayed the next MET poll by a full new three hours");
                Assert((bool)Get(window, "_lastWeatherIsCached"), "Warm restart did not mark forecast as saved");
                Assert(((TextBlock)Get(window, "_weather")).Text.Contains("сохранено"),
                    "Warm restart did not label saved weather");
                Assert(((WeatherSnapshot)Get(window, "_lastWeather")).City == "A", "Warm restart used the wrong city");
            } finally { window.Close(); }
        });
        foreach (string source in new[] { "metno", "openmeteo" }) { string selected = source;
            Case("offline cache and selected provider survive a fresh process " + selected, delegate {
                MainWindow window = Window();
                try {
                    Call(window, "SelectWeatherProvider", selected);
                } finally { window.Close(); }
                WeatherSnapshot forecast = Snapshot("A", selected == "metno" ? "MET Norway" : "Open-Meteo");
                for (int i = 0; i < 7; i++) forecast.Days.Add(new WeatherDay {
                    Date = DateTime.Today.AddDays(i), Min = -i, Max = i
                });
                WeatherDiskCache.Save("A", selected, forecast, DateTime.UtcNow.AddHours(-4));
                using (Process child = new Process()) {
                    child.StartInfo = new ProcessStartInfo(typeof(WeatherTests).Assembly.Location, selected) {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    child.Start();
                    Task<string> output = child.StandardOutput.ReadToEndAsync();
                    Task<string> error = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(30000)) { child.Kill(); child.WaitForExit(); throw new Exception("Restart probe timed out"); }
                    Assert(child.ExitCode == 0, "Fresh-process offline probe failed: " + output.Result + error.Result);
                }
            });
        }
        Case("failed refresh preserves successful forecast as saved data", delegate {
            MainWindow window = Window();
            try {
                requests[0].SetResult(Snapshot("A", "MET Norway")); Drain();
                WeatherSnapshot onDisk; DateTime when;
                Assert(WeatherDiskCache.TryLoad("A", "metno", DateTime.UtcNow, out onDisk, out when),
                    "Successful request did not persist forecast");
                ((Dictionary<string,DateTime>)Get(window, "_metWeatherRequests"))["A"] = DateTime.UtcNow.AddHours(-4);
                Call(window, "RefreshWeather");
                Assert(requests.Count == 2, "Cannot exercise refresh failure");
                requests[1].SetException(new IOException("offline")); Drain();
                Assert(Get(window, "_lastWeather") != null && (bool)Get(window, "_lastWeatherIsCached"),
                    "Offline refresh discarded last good result");
                Assert(((TextBlock)Get(window, "_weather")).Text.Contains("сохранено"),
                    "Offline forecast was not labeled as saved");
            } finally { window.Close(); }
        });
        Case("transfer failures do not write full logs, malformed data remains diagnostic", delegate {
            MainWindow window = Window();
            try {
                if (File.Exists(ConfigStore.LogPath)) File.Delete(ConfigStore.LogPath);
                requests[0].SetException(new InvalidOperationException("response",
                    new System.Net.WebException("incomplete HTTP body", System.Net.WebExceptionStatus.ConnectionClosed)));
                Drain();
                Assert(!File.Exists(ConfigStore.LogPath), "Expected transfer failure wrote a full log");
                Assert(Get(window, "_lastWeatherError") != null, "Expected transfer error was hidden from UI");
                ((Dictionary<string,DateTime>)Get(window, "_metWeatherRequests"))["A"] = DateTime.UtcNow.AddHours(-4);
                Call(window, "RefreshWeather");
                requests[1].SetException(UnexpectedWeatherError()); Drain();
                string log = File.ReadAllText(ConfigStore.LogPath);
                Assert(log.Contains("malformed-json-diagnostic-marker") && log.Contains("InvalidDataException") &&
                    log.Contains("metno") && log.Contains("UnexpectedWeatherError"),
                    "Unexpected data error lost detailed diagnostics/stack");
            } finally { window.Close(); }
        });
        Case("TLS failure is logged briefly only once for the same city and source", delegate {
            MainWindow window = Window();
            try {
                if (File.Exists(ConfigStore.LogPath)) File.Delete(ConfigStore.LogPath);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (attempt > 0)
                    {
                        ((Dictionary<string,DateTime>)Get(window, "_metWeatherRequests"))["A"] = DateTime.UtcNow.AddHours(-4);
                        Call(window, "RefreshWeather");
                    }
                    requests[attempt].SetException(new System.Net.WebException("TLS-marker",
                        System.Net.WebExceptionStatus.TrustFailure)); Drain();
                }
                string log = File.ReadAllText(ConfigStore.LogPath);
                Assert(log.IndexOf("InvalidOperationException", StringComparison.Ordinal) ==
                    log.LastIndexOf("InvalidOperationException", StringComparison.Ordinal) &&
                    !log.Contains("WebException"), "TLS log repeated or included the full inner exception");
            } finally { window.Close(); }
        });
        Case("expired disk cache does not appear after startup", delegate {
            WeatherDiskCache.Save("A", "metno", Snapshot("A", "MET Norway"), DateTime.UtcNow.AddHours(-25));
            MainWindow window = Window();
            try {
                Assert(requests.Count == 1 && Get(window, "_lastWeather") == null,
                    "Expired cache was used or normal refresh was skipped");
            } finally { window.Close(); }
        });
        Case("visible cached forecast expires at 24 hours without a new request", delegate {
            MainWindow window = Window();
            try {
                Call(window, "SetWeatherSnapshot", Snapshot("A", "MET Norway"),
                    DateTime.UtcNow.AddHours(-24).AddMilliseconds(300), true);
                Stopwatch wait = Stopwatch.StartNew();
                while (Get(window, "_lastWeather") != null && wait.ElapsedMilliseconds < 5000) {
                    Thread.Sleep(25); Drain();
                }
                Assert(Get(window, "_lastWeather") == null && requests.Count == 1,
                    "Expired visible forecast remained on screen or expiry triggered a network request");
            } finally { window.Close(); }
        });
        Case("provider change during successful request", delegate { Switch(false, false); });
        Case("provider change during failed request", delegate { Switch(false, true); });
        Case("city change during successful request", delegate { Switch(true, false); });
        Case("city change during failed request", delegate { Switch(true, true); });
        Case("MET successful refresh interval", delegate {
            MainWindow w = Window(); try { requests[0].SetResult(Snapshot("A", "MET Norway")); Drain(); Call(w, "RefreshWeather"); Assert(requests.Count == 1, "MET fetched again within 3 h"); } finally { w.Close(); }
        });
        Case("failed request is not retried forever", delegate {
            MainWindow w = Window(); try { requests[0].SetException(new IOException("test-error")); Drain(); Assert(requests.Count == 1 && !(bool)Get(w, "_weatherPending"), "Infinite retry after failure"); } finally { w.Close(); }
        });
        Case("provider change immediately removes old display", delegate {
            MainWindow w = Window(); try {
                Set(w, "_lastWeather", Snapshot("A", "MET Norway")); Set(w, "_lastWeatherCity", "A"); Set(w, "_lastWeatherProvider", "metno");
                Change(w, "A", "openmeteo"); Assert(Get(w, "_lastWeather") == null, "Old provider is still displayed as current");
            } finally { w.Close(); }
        });
        Case("MET failed attempt still respects refresh interval", delegate {
            MainWindow w = Window(); try { requests[0].SetException(new IOException("test-error")); Drain(); Call(w, "RefreshWeather"); Assert(requests.Count == 1, "MET repeated within 3 h after an error"); } finally { w.Close(); }
        });
        Case("A-B-A settings invalidate the first result without repeating MET", delegate {
            MainWindow w = Window(); try {
                Change(w, "A", "openmeteo"); Change(w, "A", "metno"); requests[0].SetResult(Snapshot("A", "MET Norway")); Drain();
                Assert(Get(w, "_lastWeather") == null && requests.Count == 1 && !(bool)Get(w, "_weatherPending"), "Obsolete A result was applied or MET was fetched twice");
            } finally { w.Close(); }
        });
        Case("disabling weather ignores the pending error", delegate {
            MainWindow w = Window(); try {
                AppConfig pending = ((AppConfig)Get(w, "_config")).Copy(); pending.ShowWeather = false;
                Assert((bool)Call(w, "SaveConfigChanges", pending, false), "Cannot disable weather"); Call(w, "RefreshWeather");
                requests[0].SetException(new IOException("old-disabled-error")); Drain();
                Assert(Get(w, "_lastWeatherError") == null && requests.Count == 1 && !(bool)Get(w, "_weatherPending"), "Disabled weather applied an old error or retried");
            } finally { w.Close(); }
        });
        Case("MET refresh is allowed after the interval", delegate {
            MainWindow w = Window(); try {
                WeatherSnapshot s = Snapshot("A", "MET Norway"); requests[0].SetResult(s); Drain();
                s.UpdatedAt = DateTime.Now.AddHours(-4);
                FieldInfo timestamps = typeof(MainWindow).GetField("_metWeatherRequests", Private);
                if (timestamps != null) ((Dictionary<string,DateTime>)timestamps.GetValue(w))["A"] = DateTime.UtcNow.AddHours(-4);
                Call(w, "RefreshWeather"); Assert(requests.Count == 2, "MET refresh never became eligible");
                requests[1].SetResult(Snapshot("A", "MET Norway")); Drain();
            } finally { w.Close(); }
        });
        Case("legacy Yandex configuration migrates without losing settings or touching key", delegate {
            AppConfig original = new AppConfig { WeatherProvider = "yandex", WeatherCity = "Test city", ShowWeather = true,
                ShowNetwork = false, Compact = true, PomodoroWorkMinutes = 42 };
            original.Groups.Add("Kept group"); original.Items.Add(new AppItem { Name = "Kept app", Path = "https://example.invalid", Group = "Kept group" });
            ConfigStore.Save(original);
            byte[] before = File.ReadAllBytes(ConfigStore.FilePath);
            string key = Path.Combine(ConfigStore.DirectoryPath, "yandex-weather-key.dpapi");
            byte[] sentinel = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(key, sentinel); DateTime stamp = File.GetLastWriteTimeUtc(key);
            using (FileStream locked = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                string warning; bool safe;
                AppConfig loaded = ConfigStore.Load(out warning, out safe);
                Assert(safe && warning == null && loaded.WeatherProvider == "metno", "Old provider was not normalized");
                Assert(loaded.WeatherCity == original.WeatherCity && loaded.Compact && !loaded.ShowNetwork &&
                    loaded.PomodoroWorkMinutes == 42 && loaded.Items[0].Name == "Kept app" && loaded.Groups[0] == "Kept group" &&
                    loaded.Version == original.Version, "Migration lost unrelated settings");
                Assert(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(ConfigStore.FilePath)), "Load rewrote the original JSON");
                Assert(Forecast(loaded.WeatherProvider).Source == "MET Norway", "Migration routed to removed API");
                ConfigStore.Save(loaded);
            }
            Assert(Convert.ToBase64String(File.ReadAllBytes(key)) == Convert.ToBase64String(sentinel) && File.GetLastWriteTimeUtc(key) == stamp, "Old key was changed or deleted");
        });
        Case("raw removed provider routes only to MET Norway", delegate { Assert(Forecast("yandex").Source == "MET Norway", "Removed API is still reachable"); });
        Case("legacy backup configuration also migrates safely", delegate {
            ConfigStore.Save(new AppConfig { WeatherProvider = "yandex", WeatherCity = "Test city", ShowNetwork = false });
            File.Copy(ConfigStore.FilePath, ConfigStore.FilePath + ".bak", true); File.WriteAllText(ConfigStore.FilePath, "broken");
            string warning; bool safe;
            AppConfig loaded = ConfigStore.Load(out warning, out safe);
            Assert(!safe && warning != null && loaded.WeatherProvider == "metno" && loaded.WeatherCity == "Test city", "Legacy .bak migration failed");
        });
        Case("legacy ZIP restores without exporting or altering the retired key", delegate {
            AppConfig config = new AppConfig { WeatherProvider = "yandex", WeatherCity = "Test city", ShowNetwork = false };
            ConfigStore.Save(config);
            string key = Path.Combine(ConfigStore.DirectoryPath, "yandex-weather-key.dpapi");
            File.WriteAllBytes(key, new byte[] { 7, 6, 5, 4 });
            byte[] before = File.ReadAllBytes(key); DateTime stamp = File.GetLastWriteTimeUtc(key);
            string zip = Path.Combine(Environment.GetEnvironmentVariable("APPSHELF_TEST_DATA"), "legacy.zip");
            BackupStore.ExportAll(zip, config, new ReminderDocument());
            using (ZipArchive archive = ZipFile.OpenRead(zip)) Assert(archive.GetEntry("yandex-weather-key.dpapi") == null, "Old key was exported");
            BackupStore.RestoreAll(zip);
            string warning; bool safe;
            AppConfig loaded = ConfigStore.Load(out warning, out safe);
            Assert(safe && loaded.WeatherProvider == "metno" && loaded.WeatherCity == "Test city", "Legacy ZIP provider was not normalized on load");
            Assert(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(key)) && File.GetLastWriteTimeUtc(key) == stamp, "Restore changed the old key");
        });
        Case("removed models and secret class are absent", delegate {
            Assembly assembly = typeof(WeatherClient).Assembly;
            Assert(assembly.GetType("AppShelf.WeatherSecrets") == null && assembly.GetType("AppShelf.YandexResponse") == null &&
                typeof(WeatherClient).GetMethod("GetYandexForecast") == null, "Removed integration is still compiled");
        });
        Case("weather settings and menu contain no paid provider or key UI", delegate {
            MainWindow w = Window(); try {
                Call(w, "ShowSettings");
                Border chip = (Border)Get(w, "_weatherChip");
                foreach (object item in chip.ContextMenu.Items) {
                    MenuItem menu = item as MenuItem;
                    if (menu != null) Assert(!Convert.ToString(menu.Header).Contains("Яндекс"), "Removed website menu item is present");
                }
            } finally { w.Close(); }
        });
        Case("context menu switches and persists weather provider", delegate {
            MainWindow window = Window();
            try {
                Border chip = (Border)Get(window, "_weatherChip");
                ContextMenu menu = chip.ContextMenu;
                MenuItem met = null, openMeteo = null;
                foreach (object part in menu.Items)
                {
                    MenuItem item = part as MenuItem;
                    if (item == null) continue;
                    if (Convert.ToString(item.Header) == "MET Norway") met = item;
                    if (Convert.ToString(item.Header) == "Open-Meteo") openMeteo = item;
                }
                Assert(met != null && openMeteo != null && met.IsCheckable && openMeteo.IsCheckable,
                    "Provider menu entries are missing or not checkable");
                // Activate the actual UI handler, not the underlying helper method.
                openMeteo.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(((AppConfig)Get(window, "_config")).WeatherProvider == "openmeteo",
                    "Context menu did not switch provider");
                string warning; bool safe;
                Assert(ConfigStore.Load(out warning, out safe).WeatherProvider == "openmeteo",
                    "Context menu did not persist the chosen provider");
                requests[0].SetResult(Snapshot("A", "MET Norway")); Drain();
                Assert(requests.Count == 2 && requestKeys[1] == "A/openmeteo" &&
                    Get(window, "_lastWeather") == null, "Old provider request leaked through menu selection");
                requests[1].SetResult(Snapshot("A", "Open-Meteo")); Drain();
                Assert(((WeatherSnapshot)Get(window, "_lastWeather")).Source == "Open-Meteo",
                    "New provider forecast not applied");
                openMeteo.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(requests.Count == 2, "Choosing active provider unexpectedly reloaded forecast");
                met.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(((AppConfig)Get(window, "_config")).WeatherProvider == "metno",
                    "Switching back to MET failed");
            }
            finally { window.Close(); }
        });
        Case("weather source menu is available in all three bottom-panel modes", delegate {
            MainWindow window = Window();
            try {
                Func<ContextMenu,bool> hasChoices = delegate(ContextMenu menu) {
                    if (menu == null) return false;
                    bool met = false, open = false;
                    foreach (object part in menu.Items) {
                        MenuItem item = part as MenuItem;
                        if (item == null) continue;
                        met |= Convert.ToString(item.Header) == "MET Norway" && item.IsCheckable;
                        open |= Convert.ToString(item.Header) == "Open-Meteo" && item.IsCheckable;
                    }
                    return met && open;
                };
                Border detailedWeather = (Border)Get(window, "_weatherChip");
                Assert(hasChoices(detailedWeather.ContextMenu),
                    "Detailed weather chip has no provider choices");
                RightClickWeather(window, detailedWeather, "detailed");
                AppConfig compact = ((AppConfig)Get(window, "_config")).Copy();
                compact.BottomPanelMode = "compact";
                Assert((bool)Call(window, "SaveConfigChanges", compact, false), "Cannot save compact mode");
                Call(window, "ApplyBottomPanelMode");
                WrapPanel favorites = (WrapPanel)Get(window, "_bottomCompactItems");
                bool compactHasWeather = false;
                foreach (UIElement element in favorites.Children) {
                    Button button = element as Button;
                    if (button != null && hasChoices(button.ContextMenu)) {
                        compactHasWeather = true;
                        RightClickWeather(window, button, "compact");
                    }
                }
                Assert(compactHasWeather, "Compact favorite weather button has no provider menu");
                AppConfig categories = ((AppConfig)Get(window, "_config")).Copy();
                categories.BottomPanelMode = "categories";
                Assert((bool)Call(window, "SaveConfigChanges", categories, false), "Cannot save category mode");
                Call(window, "ApplyBottomPanelMode");
                Panel tabs = (Panel)Get(window, "_bottomCategoryTabs");
                Button weatherTab = (Button)tabs.Children[1];
                Assert(hasChoices(weatherTab.ContextMenu),
                    "Weather category tab has no provider menu");
                RightClickWeather(window, weatherTab, "categories");
                Set(window, "_bottomOpenCategory", "weather");
                Call(window, "RefreshBottomCategoryDetails");
                WrapPanel details = (WrapPanel)Get(window, "_bottomCategoryDetails");
                Assert(details.Children.Count == 1 &&
                    hasChoices(((Button)details.Children[0]).ContextMenu),
                    "Expanded weather category button has no provider menu");
                RightClickWeather(window, (Button)details.Children[0], "expanded categories");
            }
            finally { window.Close(); }
        });
        Case("provider menu preserves existing weather commands", delegate {
            MainWindow window = Window();
            try {
                ContextMenu menu = ((Border)Get(window, "_weatherChip")).ContextMenu;
                List<string> headers = new List<string>();
                foreach (object part in menu.Items)
                {
                    MenuItem item = part as MenuItem;
                    if (item != null) headers.Add(Convert.ToString(item.Header));
                }
                Assert(headers.Contains("Прогноз на неделю") &&
                    headers.Contains("Обновить погоду") && headers.Contains("Изменить город") &&
                    headers.Contains("MET Norway") && headers.Contains("Open-Meteo"),
                    "Provider toggle removed an existing context menu action");
            }
            finally { window.Close(); }
        });
        Case("settings save switches free providers and respects MET interval", delegate {
            MainWindow w = Window(); try {
                settingsChoice = "openmeteo"; Call(w, "ShowSettings");
                Assert(((AppConfig)Get(w, "_config")).WeatherProvider == "openmeteo", "Open-Meteo selection was not saved");
                requests[0].SetException(new IOException("obsolete-met-error")); Drain();
                Assert(requests.Count == 2 && requestKeys[1] == "A/openmeteo" && Get(w, "_lastWeatherError") == null, "Settings did not schedule selected free provider");
                requests[1].SetResult(Snapshot("A", "Open-Meteo")); Drain();
                settingsChoice = "metno"; Call(w, "ShowSettings");
                Assert(((AppConfig)Get(w, "_config")).WeatherProvider == "metno" && Get(w, "_lastWeather") == null && requests.Count == 2,
                    "Switch back applied Open-Meteo data or repeated MET too soon");
            } finally { w.Close(); }
        });
        Case("legacy yandex complete ZIP rollback and retry", delegate { LegacyRollback(false); });
        Case("legacy yandex incomplete ZIP rollback and retry", delegate { LegacyRollback(true); });
        Console.WriteLine("Weather data scenarios passed=" + passed + "; failed=" + failed);
        if (failed != 0) throw new Exception("Weather data scenarios failed: " + failed);
    }
}
'@
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateInMemory = $false
    $options.GenerateExecutable = $true
    $options.OutputAssembly = Join-Path $temp 'WeatherRestartProbe.exe'
    $options.CompilerOptions = '/main:WeatherRestartProbe'
    $options.TreatWarningsAsErrors = $true
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll',
        'System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xml.dll','System.Xaml.dll',
        'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
        [void]$options.ReferencedAssemblies.Add((Join-Path $framework $reference))
    }
    $result = $provider.CompileAssemblyFromSource($options, [string[]]$sources)
    if ($result.Errors.Count) { throw ($result.Errors | Out-String) }
    $method = if ($WithoutIcu) { 'RunWithoutIcu' } else { 'Run' }
    $result.CompiledAssembly.GetType('WeatherTests').GetMethod($method).Invoke($null, @())
    Write-Output "PASS isolated weather data tests: $temp"
} finally { $provider.Dispose(); $env:APPSHELF_TEST_DATA = $previous }
