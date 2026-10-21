using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace BatteryHud;

/// <summary>
/// Internal panel brightness goes through the WmiMonitorBrightnessMethods WMI class
/// (the same path the Windows brightness slider uses). External monitors, when enabled
/// in config, are driven over DDC/CI.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrightnessService
{
    private const string Scope = @"root\WMI";

    public static bool InternalSupported { get; private set; } = true;

    public static int GetInternal()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var v = mo["CurrentBrightness"];
                    if (v is not null) return Convert.ToInt32(v);
                }
            }
        }
        catch (Exception ex)
        {
            InternalSupported = false;
            Log.Warn($"WmiMonitorBrightness: {ex.Message}");
        }
        return -1;
    }

    /// <summary>Brightness levels the panel actually accepts (often 0..100, sometimes sparse).</summary>
    public static int[] GetInternalLevels()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["Level"] is byte[] levels && levels.Length > 0)
                        return levels.Select(b => (int)b).Distinct().OrderBy(x => x).ToArray();
                }
            }
        }
        catch (Exception ex) { Log.Warn($"brightness levels: {ex.Message}"); }
        return Enumerable.Range(0, 101).ToArray();
    }

    public static bool SetInternal(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            InternalSupported = false;
            Log.Warn($"WmiSetBrightness: {ex.Message}");
        }
        return false;
    }

    // ------------------------------------------------------------ live tracking

    /// <summary>
    /// Raises an event when the panel brightness changes for any reason - the F-key
    /// shortcuts, the Action Center slider, adaptive brightness, a power-plan transition.
    ///
    /// WMI publishes WmiMonitorBrightnessEvent for exactly this, so there is no need to
    /// poll: polling would either lag behind the keyboard or hammer WMI once a second.
    /// </summary>
    public sealed class BrightnessWatcher : IDisposable
    {
        private ManagementEventWatcher? _watcher;

        /// <summary>New brightness, 0-100.</summary>
        public event Action<int>? Changed;

        public BrightnessWatcher()
        {
            try
            {
                var scope = new ManagementScope(Scope);
                var query = new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
                _watcher = new ManagementEventWatcher(scope, query);
                _watcher.EventArrived += OnEvent;
                _watcher.Start();
            }
            catch (Exception ex)
            {
                Log.Warn($"brightness watcher: {ex.Message}");
                _watcher = null;
            }
        }

        private void OnEvent(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var v = e.NewEvent["Brightness"];
                if (v is not null) Changed?.Invoke(Convert.ToInt32(v));
            }
            catch (Exception ex) { Log.Warn($"brightness event: {ex.Message}"); }
        }

        public void Dispose()
        {
            try
            {
                if (_watcher is null) return;
                _watcher.EventArrived -= OnEvent;
                _watcher.Stop();
                _watcher.Dispose();
                _watcher = null;
            }
            catch (Exception ex) { Log.Warn($"brightness watcher dispose: {ex.Message}"); }
        }
    }

    // -------------------------------------------------------------------- DDC/CI

    public sealed record ExternalMonitor(IntPtr Handle, string Description, uint Min, uint Current, uint Max);

    public static List<ExternalMonitor> EnumerateExternal()
    {
        var list = new List<ExternalMonitor>();
        try
        {
            Native.GetCursorPos(out var pt);
            // Enumerate every monitor by walking the virtual desktop through display devices.
            foreach (var target in DisplayService.Enumerate())
            {
                var probe = new Native.POINT { X = (int)(target.Width / 2), Y = (int)(target.Height / 2) };
                IntPtr hmon = Native.MonitorFromPoint(probe, Native.MONITOR_DEFAULTTONEAREST);
                if (hmon == IntPtr.Zero) continue;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) || count == 0) continue;

                var arr = new Native.PHYSICAL_MONITOR[count];
                if (!Native.GetPhysicalMonitorsFromHMONITOR(hmon, count, arr)) continue;

                foreach (var pm in arr)
                {
                    if (Native.GetMonitorBrightness(pm.hPhysicalMonitor, out uint min, out uint cur, out uint max))
                        list.Add(new ExternalMonitor(pm.hPhysicalMonitor, pm.szPhysicalMonitorDescription, min, cur, max));
                    else
                        Native.DestroyPhysicalMonitors(1, new[] { pm });
                }
            }
            _ = pt;
        }
        catch (Exception ex) { Log.Warn($"ddc enumerate: {ex.Message}"); }
        return list;
    }

    public static bool SetExternal(IntPtr handle, uint value)
    {
        try { return Native.SetMonitorBrightness(handle, value); }
        catch (Exception ex) { Log.Warn($"ddc set: {ex.Message}"); return false; }
    }

    public static void ReleaseExternal(IEnumerable<ExternalMonitor> monitors)
    {
        foreach (var m in monitors)
        {
            try { Native.DestroyPhysicalMonitors(1, new[] { new Native.PHYSICAL_MONITOR { hPhysicalMonitor = m.Handle, szPhysicalMonitorDescription = "" } }); }
            catch { /* ignore */ }
        }
    }
}
