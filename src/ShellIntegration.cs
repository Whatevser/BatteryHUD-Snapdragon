using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace BatteryHud;

/// <summary>
/// Shell odds and ends: autostart and shortcuts to the OS pages.
///
/// Hiding the built-in battery icon is left to Windows: OpenNotificationAreaIcons points at
/// the classic page that does it.
/// </summary>
public static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BatteryHud";


    // ------------------------------------------------------------------- links

    /// <summary>Settings → System → Power &amp; battery (the modern page with the two
    /// power-mode dropdowns on Windows 11 24H2 and later).</summary>
    public static void OpenWindowsPowerSettings() => Launch("ms-settings:powersleep");

    /// <summary>Control Panel → Hardware and Sound → Power Options (classic power plans).</summary>
    public static void OpenControlPanelPowerOptions()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.PowerOptions")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"open power options: {ex.Message}");
            Launch("powercfg.cpl");
        }
    }

    public static void OpenConfigFolder() => Launch(ConfigStore.Directory);

    /// <summary>Settings → System → Display → Night light.</summary>
    public static void OpenNightLightSettings() => Launch("ms-settings:nightlight");

    /// <summary>Settings → Personalization → Taskbar, where the tray icons are listed.</summary>
    public static void OpenTaskbarSettings() => Launch("ms-settings:taskbar");

    /// <summary>
    /// The classic "Notification Area Icons" control panel. This is the page that still lets
    /// you turn the battery icon off by hand when the classic tray is in use (ExplorerPatcher
    /// with the Windows 10 taskbar), and it is genuinely hard to find otherwise - Windows 11
    /// removed every link to it, but the shell folder is still registered.
    /// </summary>
    public static void OpenNotificationAreaIcons()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                "shell:::{05d7b0f4-2121-4eff-bf6b-ed3f69b894d9}")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"notification area icons: {ex.Message}");
            OpenTaskbarSettings();
        }
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"launch {target}: {ex.Message}"); }
    }

    // --------------------------------------------------------------- autostart

    // The former charge-limit implementation installed a highest-privileges logon task.
    // Retain deletion/migration of that old task, but never create elevated tasks again.
    private const string TaskName = "BatteryHud";
    private static bool? _taskExists;

    public static bool RunAtLoginEnabled() => RunKeyEnabled() || LegacyElevatedTaskPresent();
    public static bool LegacyElevatedTaskPresent() => TaskExists();

    private static bool RunKeyEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string command && command.Length > 0;
        }
        catch { return false; }
    }

    private static bool TaskExists()
    {
        if (_taskExists.HasValue) return _taskExists.Value;
        _taskExists = RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;
        return _taskExists.Value;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool DeleteLegacyTask()
    {
        if (!TaskExists()) return true;
        int rc = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out string output);
        if (rc == 0)
        {
            _taskExists = false;
            return true;
        }
        Log.Warn($"autostart: cannot delete old elevated task ({rc}): {output.Trim()}");
        return false;
    }

    /// <summary>
    /// If an older elevated startup task launched us, migrate it to HKCU Run immediately.
    /// If running unelevated, preserve the old entry and explain one-time cleanup in Settings.
    /// </summary>
    public static void MigrateLegacyAutostart()
    {
        if (!TaskExists()) return;
        if (!IsElevated())
        {
            Log.Warn("autostart: legacy elevated task remains; one elevated launch is needed to migrate it");
            return;
        }

        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(exe)) return;
        bool hadRunKey = RunKeyEnabled();
        try
        {
            SetRunKey($"\"{exe}\"");
            if (!DeleteLegacyTask())
            {
                if (!hadRunKey) SetRunKey(null);
                return;
            }
            Log.Info("autostart: migrated legacy elevated task to standard HKCU Run");
        }
        catch (Exception ex)
        {
            Log.Warn($"autostart migration: {ex.Message}");
        }
    }

    public static void SetRunAtLogin(bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(exe)) return;
                if (TaskExists())
                {
                    MigrateLegacyAutostart();
                    if (TaskExists())
                    {
                        Log.Warn("autostart: old elevated task remains; avoiding a duplicate Run entry");
                        return;
                    }
                }
                SetRunKey($"\"{exe}\"");
                Log.Info("autostart: enabled for this user without administrator rights");
            }
            else
            {
                if (TaskExists() && !DeleteLegacyTask())
                    Log.Warn("autostart: legacy task needs one-time administrator cleanup");
                SetRunKey(null);
                Log.Info("autostart: HKCU Run disabled");
            }
        }
        catch (Exception ex) { Log.Warn($"run at login: {ex.Message}"); }
    }

    private static void SetRunKey(string? command)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (k is null) return;
        if (command is null) k.DeleteValue(RunValue, throwOnMissingValue: false);
        else k.SetValue(RunValue, command);
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return -1;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(); } catch { }
                return -1;
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
