using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BatteryHud;

/// <summary>
/// Test-only control of ASUS Optimization. Unlike G-Helper's broad Stop Services command,
/// this never changes the service start type and never touches other ASUS services.
/// </summary>
internal static class AsusOptimizationControl
{
    private static readonly object Gate = new();
    private static DateTime _nextCheck = DateTime.MinValue;
    private static int _checking;

    private static (int ExitCode, string Output) RunSc(string action)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = action + " ASUSOptimization",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            try { process.Kill(); } catch { }
            return (-1, "Service control timed out");
        }
        return (process.ExitCode, output);
    }

    public static string Status()
    {
        try
        {
            var result = RunSc("query");
            if (result.ExitCode != 0) return "Unavailable";
            if (result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "Running";
            if (result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "Stopped";
            return "Transitioning";
        }
        catch (Exception ex) { Log.Warn($"ASUSOptimization status: {ex.Message}"); return "Unknown"; }
    }

    public static string Stop()
    {
        if (!AsusService.IsElevated) return "Run BatteryHud as administrator to stop ASUS Optimization.";
        try
        {
            if (Status() == "Stopped") return "ASUS Optimization is already stopped.";
            var result = RunSc("stop");
            Log.Info($"asus service stop: exit={result.ExitCode}; {result.Output.Trim()}");
            return result.ExitCode == 0
                ? "Stop requested for ASUS Optimization (startup mode unchanged)."
                : $"Unable to stop ASUS Optimization: {result.Output.Trim()}";
        }
        catch (Exception ex) { Log.Warn($"ASUSOptimization stop: {ex.Message}"); return ex.Message; }
    }

    public static string Start()
    {
        if (!AsusService.IsElevated) return "Run BatteryHud as administrator to start ASUS Optimization.";
        try
        {
            if (Status() == "Running") return "ASUS Optimization is already running.";
            var result = RunSc("start");
            Log.Info($"asus service start: exit={result.ExitCode}; {result.Output.Trim()}");
            return result.ExitCode == 0 ? "Start requested for ASUS Optimization." : result.Output.Trim();
        }
        catch (Exception ex) { Log.Warn($"ASUSOptimization start: {ex.Message}"); return ex.Message; }
    }

    public static void CheckGHelper(bool enabled)
    {
        if (!enabled || !AsusService.IsElevated) return;
        lock (Gate)
        {
            if (DateTime.UtcNow < _nextCheck) return;
            _nextCheck = DateTime.UtcNow.AddSeconds(30);
        }
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var probe = Process.GetProcessesByName("GHelper").FirstOrDefault();
                if (probe is not null && Status() == "Running")
                    Log.Info("G-Helper detected: " + Stop());
            }
            catch (Exception ex) { Log.Warn($"G-Helper auto-stop: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _checking, 0); }
        });
    }
}
