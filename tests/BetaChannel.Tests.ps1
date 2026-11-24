# Offline regression for the stable/beta release contract. Windows PowerShell 5.1 -STA.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $source = [IO.File]::ReadAllText((Join-Path $root 'AppUpdate.cs'))
    $source = $source.Replace('using System.Windows;', '')
    $previousTestExe = $env:APPSHELF_UPDATE_TEST_EXE
    $env:APPSHELF_UPDATE_TEST_EXE = Join-Path $root 'AppShelf.exe'
    $harness = @'
using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace AppShelf
{
    internal static class ConfigStore { internal static void Log(Exception error) { } }
    public static class BetaChannelTests
    {
        private static int count;
        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            count++;
        }

        private static string Entry(string tag, bool beta, bool draft, string digest = null)
        {
            return "{\"tag_name\":\"" + tag + "\",\"prerelease\":" +
                (beta ? "true" : "false") + ",\"draft\":" +
                (draft ? "true" : "false") + ",\"assets\":[{\"name\":\"AppShelf.exe\",\"size\":20000," +
                "\"digest\":\"" + (digest ?? ("sha256:" + new string('a', 64))) + "\"," +
                "\"browser_download_url\":\"https://github.com/mishakov1c/app_shelf/releases/download/" +
                tag + "/AppShelf.exe\"}]}";
        }

        private static AppUpdate.Release Select(bool include, params string[] entries)
        {
            return AppUpdate.SelectReleaseFromJson("[" + String.Join(",", entries) + "]", include);
        }

        private static void Reject(Action action, string name)
        {
            try { action(); }
            catch (InvalidDataException) { count++; return; }
            throw new Exception(name);
        }

        private static void CacheChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "AppShelf-UpdateCache-" + Guid.NewGuid().ToString("N"));
            DateTime now = new DateTime(2026, 11, 24, 7, 0, 0, DateTimeKind.Utc);
            int calls = 0;
            Func<bool, string> fetch = delegate(bool beta)
            {
                calls++;
                return "[" + Entry("2.23.0", false, false) +
                    (beta ? "," + Entry("2.24.0-beta.1", true, false) : "") + "]";
            };
            try
            {
                AppUpdate.CheckResult first = AppUpdate.CheckWithCache(true, root, fetch, now);
                Assert(calls == 1 && !first.FromCache && first.Releases[0].IsBeta, "fresh beta check");
                AppUpdate.CheckResult again = AppUpdate.CheckWithCache(true, root, fetch, now.AddSeconds(30));
                Assert(calls == 1 && again.FromCache && again.CheckedUtc == now, "cache hit and persisted timestamp");
                AppUpdate.CheckResult stable = AppUpdate.CheckWithCache(false, root, fetch, now.AddSeconds(30));
                Assert(calls == 2 && !stable.Releases[0].IsBeta, "separate stable cache");
                AppUpdate.CheckResult fresh = AppUpdate.CheckWithCache(true, root, fetch, now.AddSeconds(60));
                Assert(calls == 3 && !fresh.FromCache, "cache expires after sixty seconds");
                DateTime blockedUntil = now.AddMinutes(10);
                AppUpdate.CheckResult limited = AppUpdate.CheckWithCache(true, root, delegate(bool beta)
                {
                    calls++;
                    throw new AppUpdate.GithubRateLimitException(blockedUntil);
                }, now.AddMinutes(2));
                Assert(calls == 4 && limited.RetryUtc == blockedUntil &&
                    limited.FromCache && limited.CheckedUtc == now.AddSeconds(60), "rate limit retains old timestamp");
                AppUpdate.CheckResult blocked = AppUpdate.CheckWithCache(false, root, fetch, now.AddMinutes(3));
                Assert(calls == 4 && blocked.RetryUtc == blockedUntil && !blocked.Releases[0].IsBeta,
                    "persisted cooldown applies to both channels");
                AppUpdate.CheckResult recovered = AppUpdate.CheckWithCache(true, root, fetch, blockedUntil);
                Assert(calls == 5 && !recovered.RetryUtc.HasValue, "network resumes at reset");
                File.WriteAllText(Path.Combine(root, "beta.json"), "invalid");
                Assert(!AppUpdate.CheckWithCache(true, root, fetch, blockedUntil.AddSeconds(1)).FromCache &&
                    calls == 6, "corrupt cache falls back to network");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            Assert(!AppUpdate.RateLimitUntil(403, "12", "", "", now).HasValue,
                "ordinary forbidden is not falsely called rate limit");
            Assert(AppUpdate.RateLimitUntil(429, null, null, "120", now) == now.AddMinutes(2),
                "retry-after seconds");
            Assert(AppUpdate.RateLimitUntil(403, "0", "1795507200", "", now) ==
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1795507200),
                "primary limit reset");
            Assert(AppUpdate.RateLimitUntil(429, null, null, null, now) == now.AddMinutes(1),
                "429 fallback cooldown");
        }

        private static void RenamedExecutableChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "AppShelf-Renamed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string source = Environment.GetEnvironmentVariable("APPSHELF_UPDATE_TEST_EXE");
                string target = Path.Combine(root, "AppShelf (1).exe");
                File.Copy(source, target);
                string hash;
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream input = File.OpenRead(source))
                    hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                AppUpdate.ValidateInstalledTarget(target, hash);
                Assert(true, "renamed genuine AppShelf accepted");
                bool rejected = false;
                try { AppUpdate.ValidateInstalledTarget(target, new string('0', 64)); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "different executable hash rejected");
                string wrong = Path.Combine(root, "AppShelf.txt");
                File.Copy(source, wrong);
                rejected = false;
                try { AppUpdate.ValidateInstalledTarget(wrong, hash); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "non-exe target rejected");
            }
            finally { Directory.Delete(root, true); }
        }

        public static int Run()
        {
            CacheChecks();
            RenamedExecutableChecks();
            Assert(AppUpdate.CompareReleaseVersions("2.24.0-beta.10", "2.24.0-beta.2") > 0, "numeric beta order");
            Assert(AppUpdate.CompareReleaseVersions("2.24.0", "2.24.0-beta.999") > 0, "final beats beta");
            Assert(AppUpdate.CompareReleaseVersions("2.25.0-beta.1", "2.24.0") > 0, "next beta beats previous stable");
            Assert(AppUpdate.CompareReleaseVersions("2.23.0", "2.24.0-beta.1") < 0, "no downgrade");
            Assert(AppUpdate.CompareReleaseVersions("v2.24.0", "2.24.0") == 0, "v prefix");
            Reject(delegate { AppUpdate.NormalizeReleaseVersion("2.24.0-beta.0"); }, "beta zero");
            Reject(delegate { AppUpdate.NormalizeReleaseVersion("2.24.0-beta.01"); }, "leading zero");
            Reject(delegate { AppUpdate.NormalizeReleaseVersion("2.24.0-rc.1"); }, "unsupported suffix");
            Reject(delegate { AppUpdate.NormalizeReleaseVersion("2.24.65535"); }, "assembly component bound");
            string stable = Entry("2.23.0", false, false);
            string b2 = Entry("2.24.0-beta.2", true, false);
            string b10 = Entry("2.24.0-beta.10", true, false);
            string final = Entry("2.24.0", false, false);
            string draft = Entry("9.0.0", false, true);
            Assert(Select(false, b10, draft, stable).VersionText == "2.23.0", "stable channel excludes beta and draft");
            AppUpdate.Release selected = Select(true, b2, stable, b10, draft);
            Assert(selected.IsBeta && selected.VersionText == "2.24.0-beta.10", "beta channel numeric ordering");
            selected = Select(true, b10, final, stable);
            Assert(!selected.IsBeta && selected.VersionText == "2.24.0", "stable release remains available");
            AppUpdate.Release[] choices = AppUpdate.SelectAvailableFromJson(
                "[" + final + "," + Entry("2.25.0-beta.1", true, false) + "]", true);
            Assert(choices.Length == 2 && choices[0].IsBeta &&
                !choices[1].IsBeta && choices[1].VersionText == "2.24.0",
                "newer beta must not hide available stable update");
            choices = AppUpdate.SelectAvailableFromJson(
                "[" + final + "," + b10 + "]", false);
            Assert(choices.Length == 1 && !choices[0].IsBeta, "stable-only choices");
            Assert(Select(true, stable, Entry("3.0.0", true, false)).VersionText == "2.23.0", "flag/tag mismatch");
            Assert(Select(true, stable, Entry("3.0.0-beta.1", false, false)).VersionText == "2.23.0", "unmarked beta rejected");
            Reject(delegate { Select(false, b2, draft); }, "no stable release");
            Reject(delegate { Select(true, stable, Entry("2.24.0-beta.11", true, false, "bad")); }, "missing digest blocks latest");
            Reject(delegate { Select(true, b10.Replace("/download/2.24.0-beta.10/", "/download/2.24.0-beta.2/")); }, "tag/url mismatch");
            return count;
        }
    }
}
'@
    $parameters = New-Object System.CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.TreatWarningsAsErrors = $true
    foreach ($reference in @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) {
        [void]$parameters.ReferencedAssemblies.Add($reference)
    }
    $result = $provider.CompileAssemblyFromSource($parameters, [string[]]@($source, $harness))
    if ($result.Errors.HasErrors) { throw ($result.Errors | Out-String) }
    $count = $result.CompiledAssembly.GetType('AppShelf.BetaChannelTests').GetMethod('Run').Invoke($null, $null)
    Write-Host "Beta channel: $count passed."
}
finally { $provider.Dispose(); $env:APPSHELF_UPDATE_TEST_EXE = $previousTestExe }
