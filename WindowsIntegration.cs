// HKCU logon registration and unified, application-owned sound notifications.
// No administrator rights, external tools, services or scheduled tasks.
using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AppShelf
{
    public static class WindowsStartup
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppValueName = "AppShelf";

        public sealed class State
        {
            public string RawValue;
            public bool Enabled;
            public bool InTray;
            public bool PointsToCurrentExe;
        }

        public static bool IsTrayLaunch()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
                if (String.Equals(args[i], "--tray", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string CurrentExecutable()
        {
            string filename = Process.GetCurrentProcess().MainModule.FileName;
            if (String.IsNullOrWhiteSpace(filename) ||
                filename.IndexOf('"') >= 0 || !Path.IsPathRooted(filename))
                throw new InvalidOperationException("Не удалось определить путь к AppShelf.exe.");
            return Path.GetFullPath(filename);
        }

        private static string Command(bool inTray)
        {
            return "\"" + CurrentExecutable() + "\"" + (inTray ? " --tray" : "");
        }

        public static State Read()
        {
            string value = null;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                if (key != null) value = key.GetValue(AppValueName) as string;
            bool enabled = !String.IsNullOrWhiteSpace(value);
            return new State
            {
                RawValue = value,
                Enabled = enabled,
                InTray = enabled && value.TrimEnd().EndsWith(" --tray", StringComparison.OrdinalIgnoreCase),
                PointsToCurrentExe = enabled &&
                    (String.Equals(value, Command(false), StringComparison.OrdinalIgnoreCase) ||
                     String.Equals(value, Command(true), StringComparison.OrdinalIgnoreCase))
            };
        }

        public static void Apply(bool enabled, bool inTray)
        {
            WriteRaw(enabled ? Command(inTray) : null);
        }

        // Restore an exact previous Run value if saving AppShelf config fails.
        public static void WriteRaw(string command)
        {
            if (String.IsNullOrWhiteSpace(command))
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    if (key != null) key.DeleteValue(AppValueName, false);
                return;
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) throw new InvalidOperationException("Нет доступа к автозапуску Windows.");
                key.SetValue(AppValueName, command, RegistryValueKind.String);
            }
        }
    }

    public static class NotificationAudio
    {
        public static bool ShouldPlayPomodoro(AppConfig config)
        {
            return config != null && config.SoundsEnabled != false &&
                config.PomodoroSoundEnabled != false;
        }

        public static bool ShouldPlayReminder(AppConfig config)
        {
            return config != null && config.SoundsEnabled != false &&
                config.RemindersSoundEnabled == true;
        }

        public static bool ShouldPlayCountdown(AppConfig config)
        {
            return config != null && config.SoundsEnabled != false &&
                config.CountdownSoundEnabled != false;
        }

        public static void PlayPomodoro(AppConfig config)
        {
            if (!ShouldPlayPomodoro(config)) return;
            try { System.Media.SystemSounds.Asterisk.Play(); }
            catch (Exception) { } // No sound should interrupt a timer.
        }

        public static void PlayReminder(AppConfig config)
        {
            if (!ShouldPlayReminder(config)) return;
            try { System.Media.SystemSounds.Exclamation.Play(); }
            catch (Exception) { }
        }

        public static void PlayCountdown(AppConfig config)
        {
            if (!ShouldPlayCountdown(config)) return;
            try { System.Media.SystemSounds.Asterisk.Play(); }
            catch (Exception) { }
        }
    }
}
