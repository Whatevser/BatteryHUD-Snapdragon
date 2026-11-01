using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BatteryHud;

/// <summary>What happened when we tried to push a charge limit at the hardware.</summary>
public enum ChargeLimitOutcome
{
    /// <summary>No ASUS interface on this machine.</summary>
    Unavailable,

    /// <summary>The ACPI call accepted it. Takes effect immediately.</summary>
    AppliedByFirmware,

    /// <summary>
    /// Firmware refused the device, but the value was stored where the ASUS service reads
    /// it. Applies when the service next acts - typically a plug/unplug or a resume.
    /// </summary>
    StoredForService,

    /// <summary>Firmware refused it and the registry write failed (usually needs admin).</summary>
    Failed,
}

public sealed record ChargeLimitResult(ChargeLimitOutcome Outcome, int Percent, string Detail);

/// <summary>
/// The ASUS battery charge limit, via the ATK ACPI interface that ASUS System Control
/// Interface exposes.
///
/// Two transports exist and both were confirmed present on a Vivobook S 15 S5507QAD
/// (ARM64, SCI v3.2.72.0): the WMI class AsusAtkWmi_WMNB in root\wmi, and the device
/// \\.\ATKACPI. The WMI one is used here - same methods, no handle to manage.
///
/// The interface is a generic "set device state" keyed on a numeric device ID. The charge
/// limit is 0x00120057, confirmed from two independent sources: g-helper's AsusACPI.cs and
/// the Linux kernel asus-wmi driver (ASUS_WMI_CHARGE_THRESHOLD).
///
/// IMPORTANT, measured rather than assumed: on that S5507QAD, DEVS 0x00120057 returns 0,
/// and 0 means "device not supported" in this interface (1 = accepted). So the ATK
/// transport works on ARM64 but this particular device is not implemented by that firmware.
/// Hence the two-pronged approach:
///
///   1. try the ACPI write anyway - harmless if unsupported, immediate where it works;
///   2. write ChargingRate where the ASUS service keeps its own idea of the limit.
///
/// Step 2 is also what makes this cooperative rather than a fight: the service re-asserts
/// its stored value on power events, so if we only did step 1 the limit would silently
/// revert. g-helper writes both for exactly this reason.
///
/// Nothing here throws. A failure is logged and reported back through ChargeLimitResult.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AsusService
{
    /// <summary>Charge limit. Same ID in g-helper and in the Linux asus-wmi driver.</summary>
    public const uint DevIdChargeLimit = 0x00120057;

    /// <summary>Where the ASUS service stores the limit MyASUS last set.</summary>
    private const string OptimizationKey =
        @"SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys";
    private const string ChargingRateValue = "ChargingRate";

    private const string WmiScope = @"root\wmi";
    private const string WmiClass = "AsusAtkWmi_WMNB";

    public const int MinLimit = 40;
    public const int MaxLimit = 100;

    private static bool? _available;
    private static bool _scanned;

    // ------------------------------------------------------------- availability

    /// <summary>True when the ATK WMI class with a DEVS method is present.</summary>
    public static bool Available
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            _available = false;
            try
            {
                using var searcher = new ManagementObjectSearcher(WmiScope, $"SELECT * FROM {WmiClass}");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        // Having the class is not enough, it must expose DEVS. Asking for
                        // the method's parameters throws when the method is absent, which
                        // is a cheaper and more direct test than walking a method list.
                        using var probe = mo.GetMethodParameters("DEVS");
                        _available = probe is not null;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"asus: {WmiClass}/DEVS not available: {ex.Message}");
            }

            Log.Info($"asus interface available: {_available}");
            return _available.Value;
        }
    }

    /// <summary>True when the ASUS service's own limit store exists.</summary>
    public static bool ServiceStorePresent
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(OptimizationKey);
                return k is not null;
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The limit the ASUS service currently believes in, or null. The ACPI side cannot be
    /// read back - the Linux driver notes the same and caches instead - so the registry is
    /// the only source of truth available.
    /// </summary>
    public static int? ReadLimit()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(OptimizationKey);
            if (k?.GetValue(ChargingRateValue) is int v && v >= MinLimit && v <= MaxLimit)
                return v;
        }
        catch (Exception ex) { Log.Warn($"asus: read {ChargingRateValue}: {ex.Message}"); }
        return null;
    }

    // ------------------------------------------------------------------ writing

    public static ChargeLimitResult SetLimit(int percent)
    {
        percent = Math.Clamp(percent, MinLimit, MaxLimit);

        if (!Available)
            return new ChargeLimitResult(ChargeLimitOutcome.Unavailable, percent,
                "No ASUS ATK interface. Install ASUS System Control Interface.");

        ScanCapabilitiesOnce();

        // 1. ACPI. Where the firmware implements the device this is immediate.
        int rc = InvokeDevs(DevIdChargeLimit, (uint)percent);
        bool firmwareTook = rc == 1;

        // 2. The service's store, so it does not re-assert an older value over the top.
        bool stored = WriteServiceLimit(percent);

        if (firmwareTook)
        {
            return new ChargeLimitResult(ChargeLimitOutcome.AppliedByFirmware, percent,
                stored ? "Applied by firmware; ASUS service updated to match."
                       : "Applied by firmware. Could not update the ASUS service store " +
                         "(needs administrator), so it may revert on the next power event.");
        }

        if (stored)
        {
            return new ChargeLimitResult(ChargeLimitOutcome.StoredForService, percent,
                $"Firmware refused device 0x{DevIdChargeLimit:X8} (returned {rc}). " +
                "Saved where the ASUS service reads it; it should apply on the next " +
                "plug/unplug or resume.");
        }

        return new ChargeLimitResult(ChargeLimitOutcome.Failed, percent,
            $"Firmware refused device 0x{DevIdChargeLimit:X8} (returned {rc}) and the " +
            "registry write failed. Run BatteryHud as administrator and try again.");
    }

    private static bool WriteServiceLimit(int percent)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(OptimizationKey, writable: true);
            if (k is null) return false;
            k.SetValue(ChargingRateValue, percent, RegistryValueKind.DWord);

            // Read back - a protected or policy-managed key can swallow the write.
            using var verify = Registry.LocalMachine.OpenSubKey(OptimizationKey);
            return verify?.GetValue(ChargingRateValue) is int v && v == percent;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn($"asus: {ChargingRateValue} needs administrator");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"asus: write {ChargingRateValue}: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------- ACPI helpers

    /// <summary>DEVS - set a device state. Returns the raw result; 1 means accepted.</summary>
    private static int InvokeDevs(uint deviceId, uint value)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope, $"SELECT * FROM {WmiClass}");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var args = mo.GetMethodParameters("DEVS");
                    args["Device_ID"] = deviceId;
                    args["Control_status"] = value;
                    using var outParams = mo.InvokeMethod("DEVS", args, null);
                    int rc = Convert.ToInt32(outParams?["result"] ?? 0);
                    Log.Info($"asus DEVS 0x{deviceId:X8} = {value} -> {rc}" +
                             (rc == 1 ? " (accepted)" : " (not accepted)"));
                    return rc;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"asus DEVS 0x{deviceId:X8}: {ex.Message}"); }
        return -1;
    }

    /// <summary>
    /// DSTS - read a device state. A supported device returns the 0x10000 bit set, so
    /// anything below that means the firmware does not implement it. Read-only.
    /// </summary>
    private static int InvokeDsts(uint deviceId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiScope, $"SELECT * FROM {WmiClass}");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var args = mo.GetMethodParameters("DSTS");
                    args["Device_ID"] = deviceId;
                    using var outParams = mo.InvokeMethod("DSTS", args, null);
                    return Convert.ToInt32(outParams?["result"] ?? 0);
                }
            }
        }
        catch { /* unsupported IDs are expected to fail */ }
        return 0;
    }

    /// <summary>
    /// Logs which battery-area device IDs this firmware actually implements, once per run.
    ///
    /// Purely diagnostic, and deliberately read-only: DEVS is a generic setter keyed on a
    /// number, so writing to an ID whose meaning we have not confirmed could poke an
    /// unrelated subsystem. This only reads, and only reports.
    /// </summary>
    public static void ScanCapabilitiesOnce()
    {
        if (_scanned) return;
        _scanned = true;
        if (!Available) return;

        try
        {
            var known = new Dictionary<uint, string>
            {
                [0x00120057] = "charge limit (g-helper / asus-wmi)",
                [0x0012005A] = "battery discharge rate (g-helper)",
                [0x0012006C] = "charger mode (g-helper)",
            };

            var supported = new List<string>();
            foreach (var kv in known)
            {
                int raw = InvokeDsts(kv.Key);
                bool present = (raw & 0x10000) != 0;
                if (present) supported.Add($"0x{kv.Key:X8} ({kv.Value}) raw=0x{raw:X}");
            }

            // Sweep the rest of the battery subgroup for anything else implemented.
            for (uint id = 0x00120050; id <= 0x00120070; id++)
            {
                if (known.ContainsKey(id)) continue;
                int raw = InvokeDsts(id);
                if ((raw & 0x10000) != 0) supported.Add($"0x{id:X8} (unknown) raw=0x{raw:X}");
            }

            Log.Info(supported.Count == 0
                ? "asus DSTS scan: no device in 0x00120050-0x00120070 is implemented"
                : "asus DSTS scan, implemented devices: " + string.Join(" | ", supported));
        }
        catch (Exception ex) { Log.Warn($"asus DSTS scan: {ex.Message}"); }
    }

    // ------------------------------------------------- one-time charge to full

    /// <summary>
    /// Lifts the cap until the battery reaches full, then puts it back.
    ///
    /// ASUS has its own version of this (InstantFullChargeTime / InstantFullChargeTimeout
    /// in the service's registry store, on a 24-hour timeout), but this does not use it:
    /// writing 100 and restoring the saved value when the battery tops out is the same
    /// behaviour, is ours end to end, and is predictable - it ends when the battery is
    /// actually full rather than when an invisible timer expires. g-helper does the same.
    /// </summary>
    public static bool BypassActive { get; private set; }

    /// <summary>The limit to restore once the battery is full.</summary>
    private static int _limitBeforeBypass = 80;

    public static ChargeLimitResult BeginChargeToFull(int limitToRestore)
    {
        _limitBeforeBypass = Math.Clamp(limitToRestore, MinLimit, MaxLimit);
        var result = SetLimit(MaxLimit);
        BypassActive = result.Outcome is ChargeLimitOutcome.AppliedByFirmware
                                      or ChargeLimitOutcome.StoredForService;
        if (BypassActive)
            Log.Info($"asus: charge-to-full started, will restore {_limitBeforeBypass}%");
        return result;
    }

    public static void CancelChargeToFull()
    {
        if (!BypassActive) return;
        BypassActive = false;
        Log.Info($"asus: charge-to-full cancelled, restoring {_limitBeforeBypass}%");
        SetLimit(_limitBeforeBypass);
    }

    /// <summary>
    /// Call on every battery sample. Ends the bypass once the battery is effectively full.
    /// </summary>
    public static void OnBatterySample(double truePercent, bool acOnline)
    {
        if (!BypassActive) return;

        // >= 99 rather than 100: a pack that has lost capacity may never report a round
        // 100, which would leave the bypass stuck on forever.
        if (truePercent >= 99)
        {
            BypassActive = false;
            Log.Info($"asus: battery full, restoring {_limitBeforeBypass}%");
            RestoreAsync(_limitBeforeBypass);
            return;
        }

        // Unplugged mid-bypass: the point was to top up on mains, so stop waiting.
        if (!acOnline)
        {
            BypassActive = false;
            Log.Info($"asus: unplugged during charge-to-full, restoring {_limitBeforeBypass}%");
            RestoreAsync(_limitBeforeBypass);
        }
    }

    /// <summary>
    /// OnBatterySample runs on the poll tick, i.e. the UI thread. A WMI call plus a
    /// registry write-and-verify there would stall the interface, so the flag is cleared
    /// synchronously and only the write is handed off.
    /// </summary>
    private static void RestoreAsync(int percent)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try { SetLimit(percent); }
            catch (Exception ex) { Log.Warn($"asus restore: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Pushes the configured limit again. The BIOS drops the limit on every restart and
    /// the ASUS service can re-assert its own value on power transitions, so this is called
    /// at startup and whenever the power source changes.
    /// </summary>
    public static void Reapply(AppConfig cfg)
    {
        if (!cfg.AsusCharge.Enabled) return;
        if (BypassActive) return;
        if (!Available) return;
        SetLimit(cfg.AsusCharge.LimitPercent);
    }
}
