using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryHud;

public sealed record BatteryStatic(
    string DevicePath,
    string DeviceName,
    string Manufacturer,
    string SerialNumber,
    string Chemistry,
    uint DesignedCapacity,
    uint FullChargedCapacity,
    uint CycleCount,
    bool CapacityRelative);

public sealed record BatterySample(
    DateTime Time,
    double Percent,
    uint CapacityMwh,
    uint VoltageMv,
    int RateMw,          // signed: >0 charging, <0 discharging
    bool AcOnline,
    bool Charging,
    bool Discharging,
    bool Critical,
    bool RateKnown);

/// <summary>
/// Reads the battery directly through the battery class driver
/// (IOCTL_BATTERY_QUERY_STATUS / _INFORMATION) rather than WMI. This is what gives us a
/// real instantaneous charge/discharge rate in milliwatts, which is the number you need
/// to compare two chargers.
/// </summary>
public sealed class BatteryService : IDisposable
{
    private SafeFileHandle? _handle;
    private uint _tag;
    private string _devicePath = "";
    private readonly List<BatterySample> _history = new();
    private readonly object _gate = new();
    private AppConfig _cfg;

    public BatteryStatic? Info { get; private set; }
    public IReadOnlyList<BatterySample> History { get { lock (_gate) return _history.ToArray(); } }
    public BatterySample? Latest { get; private set; }

    /// <summary>Rolling charger session: reset whenever AC is plugged in or the user resets it.</summary>
    public DateTime SessionStart { get; private set; } = DateTime.Now;
    public uint SessionStartCapacity { get; private set; }
    public double SessionStartPercent { get; private set; }
    public bool SessionIsCharging { get; private set; }

    public BatteryService(AppConfig cfg)
    {
        _cfg = cfg;
        TryOpen();
    }

    public void UpdateConfig(AppConfig cfg) => _cfg = cfg;

    // ------------------------------------------------------------ charge ceiling

    /// <summary>
    /// Fraction of a full pack the charger is allowed to reach: 0.80 when an 80% cap is
    /// configured, 1.0 when the feature is off. Everything the user *sees* is scaled by
    /// this; the raw readings underneath are left alone.
    /// </summary>
    public double Ceiling
    {
        get
        {
            var c = _cfg.ChargeLimit;
            if (!c.ShowAsFull) return 1.0;
            return Math.Clamp(c.Percent, 40, 100) / 100.0;
        }
    }

    public bool CeilingActive => Ceiling < 0.999;

    /// <summary>The percentage to display: the cap reads as 100%.</summary>
    public double DisplayPercent(BatterySample s) => Math.Clamp(s.Percent / Ceiling, 0, 100);

    // ---------------------------------------------------------------- device open

    private void TryOpen()
    {
        Close();
        foreach (var path in EnumerateBatteryDevicePaths())
        {
            try
            {
                var h = Native.CreateFile(path,
                    Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }

                if (!QueryTag(h, out uint tag)) { h.Dispose(); continue; }

                _handle = h;
                _tag = tag;
                _devicePath = path;
                Info = ReadStatic(h, tag, path);
                Log.Info($"battery opened: {Info?.DeviceName} ({Info?.Manufacturer}) design={Info?.DesignedCapacity} full={Info?.FullChargedCapacity}");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"open {path}: {ex.Message}");
            }
        }
        Log.Warn("no battery device could be opened; falling back to GetSystemPowerStatus");
    }

    private static IEnumerable<string> EnumerateBatteryDevicePaths()
    {
        var guid = Native.GUID_DEVICE_BATTERY;
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;

        try
        {
            for (uint i = 0; ; i++)
            {
                var did = new Native.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did))
                    yield break;

                Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;

                IntPtr buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on 64-bit, 6 on 32-bit.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, required, out _, IntPtr.Zero))
                        continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (!string.IsNullOrEmpty(path)) yield return path;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    private static bool QueryTag(SafeFileHandle h, out uint tag)
    {
        tag = 0;
        uint timeout = 0;
        IntPtr inBuf = Marshal.AllocHGlobal(sizeof(uint));
        IntPtr outBuf = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(inBuf, (int)timeout);
            if (!Native.DeviceIoControl(h, Native.IOCTL_BATTERY_QUERY_TAG, inBuf, sizeof(uint),
                    outBuf, sizeof(uint), out uint ret, IntPtr.Zero) || ret == 0)
                return false;
            tag = unchecked((uint)Marshal.ReadInt32(outBuf));
            return tag != 0;
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }

    private static BatteryStatic ReadStatic(SafeFileHandle h, uint tag, string path)
    {
        var bi = QueryInformation<Native.BATTERY_INFORMATION>(h, tag, Native.BATTERY_QUERY_INFORMATION_LEVEL.BatteryInformation);

        string chem = "";
        uint designed = 0, full = 0, cycles = 0;
        bool relative = false;
        if (bi.HasValue)
        {
            var v = bi.Value;
            chem = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(v.Chemistry)).Trim('\0', ' ');
            designed = v.DesignedCapacity;
            full = v.FullChargedCapacity;
            cycles = v.CycleCount;
            relative = (v.Capabilities & Native.BATTERY_CAPACITY_RELATIVE) != 0;
        }

        return new BatteryStatic(
            path,
            QueryString(h, tag, Native.BATTERY_QUERY_INFORMATION_LEVEL.BatteryDeviceName),
            QueryString(h, tag, Native.BATTERY_QUERY_INFORMATION_LEVEL.BatteryManufactureName),
            QueryString(h, tag, Native.BATTERY_QUERY_INFORMATION_LEVEL.BatterySerialNumber),
            chem, designed, full, cycles, relative);
    }

    private static T? QueryInformation<T>(SafeFileHandle h, uint tag, Native.BATTERY_QUERY_INFORMATION_LEVEL level)
        where T : struct
    {
        var q = new Native.BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = (uint)level, AtRate = 0 };
        int inSize = Marshal.SizeOf<Native.BATTERY_QUERY_INFORMATION>();
        int outSize = Marshal.SizeOf<T>();
        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(q, inBuf, false);
            if (!Native.DeviceIoControl(h, Native.IOCTL_BATTERY_QUERY_INFORMATION, inBuf, (uint)inSize,
                    outBuf, (uint)outSize, out uint ret, IntPtr.Zero) || ret == 0)
                return null;
            return Marshal.PtrToStructure<T>(outBuf);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }

    private static string QueryString(SafeFileHandle h, uint tag, Native.BATTERY_QUERY_INFORMATION_LEVEL level)
    {
        var q = new Native.BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = (uint)level, AtRate = 0 };
        int inSize = Marshal.SizeOf<Native.BATTERY_QUERY_INFORMATION>();
        const int outSize = 512;
        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(q, inBuf, false);
            for (int i = 0; i < outSize; i++) Marshal.WriteByte(outBuf, i, 0);
            if (!Native.DeviceIoControl(h, Native.IOCTL_BATTERY_QUERY_INFORMATION, inBuf, (uint)inSize,
                    outBuf, outSize, out uint ret, IntPtr.Zero) || ret == 0)
                return "";
            return (Marshal.PtrToStringUni(outBuf) ?? "").Trim();
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }

    // ------------------------------------------------------------------- sampling

    public BatterySample Poll()
    {
        Native.GetSystemPowerStatus(out var sps);
        bool acOnline = sps.ACLineStatus == 1;

        BatterySample sample;

        if (_handle is { IsInvalid: false } && TryQueryStatus(out var st))
        {
            bool relative = Info?.CapacityRelative ?? false;
            uint full = Info?.FullChargedCapacity ?? 0;
            uint cap = st.Capacity == unchecked((uint)Native.BATTERY_UNKNOWN_CAPACITY) ? 0 : st.Capacity;

            double percent;
            if (full > 0 && cap > 0) percent = Math.Clamp(cap * 100.0 / full, 0, 100);
            else if (sps.BatteryLifePercent <= 100) percent = sps.BatteryLifePercent;
            else percent = 0;

            bool rateKnown = st.Rate != Native.BATTERY_UNKNOWN_RATE;
            int rate = rateKnown ? st.Rate : 0;

            sample = new BatterySample(
                DateTime.Now, percent, cap,
                st.Voltage == unchecked((uint)Native.BATTERY_UNKNOWN_VOLTAGE) ? 0 : st.Voltage,
                rate,
                (st.PowerState & Native.BATTERY_POWER_ON_LINE) != 0 || acOnline,
                (st.PowerState & Native.BATTERY_CHARGING) != 0,
                (st.PowerState & Native.BATTERY_DISCHARGING) != 0,
                (st.PowerState & Native.BATTERY_CRITICAL) != 0,
                rateKnown && rate != 0);

            _ = relative; // surfaced through Info.CapacityRelative for the formatter
        }
        else
        {
            // Fallback: no battery device, only the coarse system status.
            sample = new BatterySample(
                DateTime.Now,
                sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : 0,
                0, 0, 0, acOnline,
                (sps.BatteryFlag & 8) != 0,
                !acOnline,
                (sps.BatteryFlag & 4) != 0,
                false);

            // The device may have gone away (dock/undock). Retry occasionally.
            if (_handle is null || _handle.IsInvalid) TryOpen();
        }

        lock (_gate)
        {
            // Session bookkeeping: restart on AC transition.
            if (Latest is null || Latest.AcOnline != sample.AcOnline)
                ResetSessionLocked(sample);

            _history.Add(sample);
            int max = Math.Max(60, _cfg.General.HistorySamples);
            if (_history.Count > max) _history.RemoveRange(0, _history.Count - max);
        }

        Latest = sample;
        MaybeLogCsv(sample);
        return sample;
    }

    private bool TryQueryStatus(out Native.BATTERY_STATUS status)
    {
        status = default;
        if (_handle is null || _handle.IsInvalid) return false;

        var wait = new Native.BATTERY_WAIT_STATUS { BatteryTag = _tag, Timeout = 0 };
        int inSize = Marshal.SizeOf<Native.BATTERY_WAIT_STATUS>();
        int outSize = Marshal.SizeOf<Native.BATTERY_STATUS>();
        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(wait, inBuf, false);
            if (!Native.DeviceIoControl(_handle, Native.IOCTL_BATTERY_QUERY_STATUS, inBuf, (uint)inSize,
                    outBuf, (uint)outSize, out uint ret, IntPtr.Zero) || ret == 0)
            {
                // Tag changes when the battery is removed/reinserted; re-acquire once.
                if (QueryTag(_handle, out uint newTag) && newTag != _tag)
                {
                    _tag = newTag;
                    Info = ReadStatic(_handle, _tag, _devicePath);
                }
                return false;
            }
            status = Marshal.PtrToStructure<Native.BATTERY_STATUS>(outBuf);
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }

    // ------------------------------------------------------------ derived numbers

    /// <summary>
    /// Smoothed rate in mW. Uses the firmware rate when available, otherwise differentiates
    /// remaining capacity over DerivedRateWindowSeconds (some ARM64 firmwares report 0).
    /// </summary>
    public double SmoothedRateMw()
    {
        BatterySample[] snap;
        lock (_gate) snap = _history.ToArray();
        if (snap.Length == 0) return 0;

        int n = Math.Clamp(_cfg.General.RateSmoothingSamples, 1, snap.Length);
        var window = snap.Skip(snap.Length - n).ToArray();

        if (window.Any(s => s.RateKnown))
        {
            var known = window.Where(s => s.RateKnown).Select(s => (double)s.RateMw).ToArray();
            return known.Average();
        }

        // Derived: dCapacity / dt.
        var last = snap[^1];
        var target = last.Time.AddSeconds(-Math.Max(10, _cfg.General.DerivedRateWindowSeconds));
        var first = snap.FirstOrDefault(s => s.Time >= target) ?? snap[0];
        double hours = (last.Time - first.Time).TotalHours;
        if (hours <= 0 || first.CapacityMwh == 0 || last.CapacityMwh == 0) return 0;
        return (last.CapacityMwh - (double)first.CapacityMwh) / hours; // mWh per hour = mW
    }

    /// <summary>Seconds until full or empty at the current smoothed rate; null if unknowable.</summary>
    public TimeSpan? TimeRemaining()
    {
        var s = Latest;
        if (s is null) return null;
        double rate = SmoothedRateMw();
        uint full = Info?.FullChargedCapacity ?? 0;

        if (Math.Abs(rate) < 250 || s.CapacityMwh == 0 || full == 0)
        {
            Native.GetSystemPowerStatus(out var sps);
            if (!s.AcOnline && sps.BatteryLifeTime > 0) return TimeSpan.FromSeconds(sps.BatteryLifeTime);
            return null;
        }

        // When a charge cap is configured, "full" is the cap, so the countdown ends where
        // charging actually stops rather than at a 100% the pack will never reach.
        double target = full * Ceiling;
        double mwh = rate > 0 ? (target - s.CapacityMwh) : s.CapacityMwh;
        if (rate > 0 && mwh <= 0) return TimeSpan.Zero;
        double hours = mwh / Math.Abs(rate);
        if (hours <= 0 || hours > 72) return null;
        return TimeSpan.FromHours(hours);
    }

    public double HealthPercent()
    {
        var i = Info;
        if (i is null || i.DesignedCapacity == 0 || i.FullChargedCapacity == 0) return 0;
        return Math.Clamp(i.FullChargedCapacity * 100.0 / i.DesignedCapacity, 0, 200);
    }

    // ------------------------------------------------------------------- session

    public void ResetSession()
    {
        lock (_gate)
        {
            var s = Latest;
            if (s is not null) ResetSessionLocked(s);
        }
    }

    private void ResetSessionLocked(BatterySample s)
    {
        SessionStart = s.Time;
        SessionStartCapacity = s.CapacityMwh;
        SessionStartPercent = s.Percent;
        SessionIsCharging = s.AcOnline;
    }

    public (TimeSpan Duration, double DeltaMwh, double DeltaPercent, double AvgRateMw) SessionStats()
    {
        var s = Latest;
        if (s is null) return (TimeSpan.Zero, 0, 0, 0);
        var dur = s.Time - SessionStart;
        double dCap = s.CapacityMwh - (double)SessionStartCapacity;
        double dPct = s.Percent - SessionStartPercent;
        double avg = dur.TotalHours > 0.0005 ? dCap / dur.TotalHours : 0;
        return (dur, dCap, dPct, avg);
    }

    // ----------------------------------------------------------------- csv logging

    private string CsvPath()
    {
        var p = _cfg.Logging.CsvPath;
        return string.IsNullOrWhiteSpace(p) ? Path.Combine(ConfigStore.Directory, "samples.csv") : p;
    }

    private void MaybeLogCsv(BatterySample s)
    {
        if (!_cfg.Logging.CsvEnabled) return;
        if (_cfg.Logging.CsvOnlyWhileCharging && !s.AcOnline) return;
        try
        {
            string path = CsvPath();
            bool header = !File.Exists(path);
            using var w = new StreamWriter(path, append: true);
            if (header) w.WriteLine("timestamp,percent,capacity_mwh,voltage_mv,rate_mw,smoothed_rate_mw,ac,charging");
            w.WriteLine($"{s.Time:yyyy-MM-dd HH:mm:ss},{s.Percent:F2},{s.CapacityMwh},{s.VoltageMv},{s.RateMw},{SmoothedRateMw():F0},{(s.AcOnline ? 1 : 0)},{(s.Charging ? 1 : 0)}");
        }
        catch (Exception ex) { Log.Warn($"csv: {ex.Message}"); }
    }

    // ---------------------------------------------------------------------------

    private void Close()
    {
        _handle?.Dispose();
        _handle = null;
        _tag = 0;
    }

    public void Dispose() => Close();
}
