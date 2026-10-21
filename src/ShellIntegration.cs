using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Microsoft.Win32;

namespace BatteryHud;

/// <summary>
/// Shell odds and ends: autostart and shortcuts to the OS pages.
///
/// There is deliberately no "hide the built-in battery icon" function here. The obvious
/// candidate, the HideSCAPower policy, is ignored by the classic tray ExplorerPatcher
/// provides. That tray keeps per-icon visibility in
/// Local Settings\...\CurrentVersion\TrayNotSIB\IconStreams, which is a ROT-13 obfuscated
/// ~55 KB blob of variable-length records that Explorer reads only at startup and writes
/// only at shutdown. Changing it from a running app would mean killing Explorer, rewriting
/// a structure we cannot validate, and restarting Explorer - risking the user's entire tray
/// layout, every time. OpenNotificationAreaIcons points at the page that does it safely.
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

    public static bool RunAtLoginEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void SetRunAtLogin(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (k is null) return;
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe)) return;
                k.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                k.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) { Log.Warn($"run at login: {ex.Message}"); }
    }
}
