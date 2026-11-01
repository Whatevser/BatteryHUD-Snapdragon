using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
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

    // ------------------------------------------------------------- elevation

    /// <summary>
    /// Starts a new elevated instance (UAC prompt) that takes over from this one - see
    /// Program.TakeOver. Returns false if the user declined the prompt or it failed; the
    /// current instance keeps running in that case.
    /// </summary>
    public static bool RestartAsAdministrator(bool openSettings)
    {
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = openSettings ? "--replace --settings" : "--replace",
            });
            Log.Info("restarting as administrator");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("restart as administrator: UAC prompt declined");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"restart as administrator: {ex.Message}");
            return false;
        }
    }

    // --------------------------------------------------------------- autostart

    /*
       Two ways to start with Windows, because they behave differently:

       - HKCU\...\Run: what this always used. Anything started from it runs WITHOUT
         administrator rights, even for an admin account - Windows never elevates Run
         entries. So the charge limit never worked after a reboot, whatever the user did.

       - A scheduled task "at logon, run with highest privileges": starts elevated with no
         UAC prompt. Creating it needs administrator, so it is used when the switch is
         turned on from an elevated instance; a normal instance falls back to the Run key.

       The task is imported from XML rather than built with schtasks flags because the flags
       cannot turn off "start only on AC power" / "stop if going on batteries", which are on
       by default and would make a battery app refuse to start on battery.
    */

    private const string TaskName = "BatteryHud";
    private static bool? _taskExists;

    public static bool RunAtLoginEnabled() => RunKeyEnabled() || TaskExists();

    /// <summary>True when autostart is set up to run with administrator rights.</summary>
    public static bool RunAtLoginElevated() => TaskExists();

    private static bool RunKeyEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private static bool TaskExists()
    {
        if (_taskExists.HasValue) return _taskExists.Value;
        _taskExists = RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;
        return _taskExists.Value;
    }

    public static void SetRunAtLogin(bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (enabled && string.IsNullOrEmpty(exe)) return;

            if (!enabled)
            {
                SetRunKey(null);
                if (TaskExists())
                {
                    int rc = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out string output);
                    if (rc == 0) _taskExists = false;
                    else Log.Warn($"autostart: could not delete the scheduled task ({rc}): {output.Trim()}"
                                  + (AsusService.IsElevated ? "" : " - needs administrator"));
                }
                return;
            }

            if (AsusService.IsElevated && CreateElevatedTask(exe))
            {
                // One mechanism at a time, or the app would be started twice at logon.
                SetRunKey(null);
                Log.Info("autostart: scheduled task created (starts as administrator)");
                return;
            }

            SetRunKey($"\"{exe}\"");
            Log.Info("autostart: Run key set (starts without administrator rights)");
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

    private static bool CreateElevatedTask(string exe)
    {
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string esc(string v) => System.Security.SecurityElement.Escape(v) ?? "";

        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts BatteryHud with administrator rights at logon, so the ASUS charge limit can be applied.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{esc(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{esc(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{esc(exe)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        string file = Path.Combine(Path.GetTempPath(), $"BatteryHud-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            int rc = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out string output);
            if (rc != 0)
            {
                Log.Warn($"autostart: schtasks /Create failed ({rc}): {output.Trim()}");
                return false;
            }
            _taskExists = true;
            return true;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
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
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
