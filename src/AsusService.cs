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
    private static bool? _firmwareImplementsLimit;
    private static bool? _elevated;

    /// <summary>
    /// The ATK WMI class refuses non-elevated callers with "access denied", so a
    /// non-admin run looks identical to "no ASUS machine" unless this is checked.
    /// </summary>
    public static bool IsElevated
    {
        get
        {
            if (_elevated.HasValue) return _elevated.Value;
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                _elevated = p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { _elevated = false; }
            return _elevated!.Value;
        }
    }

    /// <summary>
    /// False once the firmware has told us it does not implement the charge-limit device.
    /// Null until we have asked. Measured on an S5507QAD: DSTS reports nothing implemented
    /// in the whole 0x00120050-0x00120070 range and DEVS 0x00120057 always returns 0, so
    /// there is no point calling it again every time the limit changes.
    /// </summary>
    public static bool? FirmwareImplementsLimit => _firmwareImplementsLimit;

    // ------------------------------------------------------------- availability

    /// <summary>
    /// Raised (on a background thread, possibly) whenever something the UI shows about the
    /// charge limit may have changed: the interface probe finished, the DSTS scan reached a
    /// verdict, a limit was written, or charge-to-full started or ended. The Settings window
    /// listens so it never needs reopening to show the current state.
    /// </summary>
    public static event Action? StateChanged;

    private static void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn($"asus state changed handler: {ex.Message}"); }
    }

    private static DateTime _retryProbeAt = DateTime.MaxValue;

    /// <summary>True when the ATK WMI class with a DEVS method is present.</summary>
    public static bool Available
    {
        get
        {
            if (_available.HasValue && DateTime.UtcNow < _retryProbeAt) return _available.Value;

            // The startup task and the UI thread both ask this. Without the lock the second
            // caller can get in while the first is still probing and read a premature
            // "false", which the Settings window then reports as "no ASUS interface".
            lock (ProbeGate)
            {
                if (_available.HasValue && DateTime.UtcNow < _retryProbeAt) return _available.Value;
                bool found = false;
                bool transient = false;
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
                            found = probe is not null;
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // Elevated and still refused is usually the ASUS WMI provider not being up
                    // yet - seen right after logon. Caching that for the whole session would
                    // report "no ASUS interface" until the next restart, so try again shortly.
                    // Non-elevated refusals are permanent for this process; no point retrying.
                    transient = IsElevated;
                    Log.Warn($"asus: {WmiClass}/DEVS not available: {ex.Message.Trim()}"
                             + (IsElevated ? " (elevated; will retry)" : " (not running as administrator)"));
                }

                bool changed = _available != found;
                _available = found;
                _retryProbeAt = transient ? DateTime.UtcNow.AddSeconds(20) : DateTime.MaxValue;
                if (changed || !transient) Log.Info($"asus interface available: {found}");
                if (changed) RaiseStateChanged();
                return found;
            }
        }
    }

    private static readonly object ProbeGate = new();
    private static readonly object ScanGate = new();

    /// <summary>True when the ASUS service's own limit store exists. Readable without admin.</summary>
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

    /// <summary>The ACPI route: the ATK class answers and the firmware has not refused the device.</summary>
    public static bool FirmwareRouteUsable => Available && _firmwareImplementsLimit != false;

    /// <summary>
    /// The ASUS service route: ChargingRate in its registry store, which the service applies
    /// on the next power event. Needs administrator to write, but not the ATK WMI class.
    ///
    /// This is the route that actually works on firmware that implements none of the
    /// battery devices (measured: S5507QAD), so it must count as "can set the limit" on its
    /// own. Treating firmware refusal as "cannot set a limit" was a regression.
    /// </summary>
    public static bool ServiceRouteUsable => IsElevated && ServiceStorePresent;

    /// <summary>True when at least one route to the charge limit can be used.</summary>
    public static bool CanSetLimit => FirmwareRouteUsable || ServiceRouteUsable;

    /// <summary>One plain sentence on why CanSetLimit is false, or null when it is true.</summary>
    public static string? WhyCannotSetLimit()
    {
        if (CanSetLimit) return null;
        if (!IsElevated)
            return "BatteryHud is not running as administrator. Both the ASUS interface and the "
                 + "ASUS service store refuse a normal process, so the limit cannot be changed.";
        if (!ServiceStorePresent && !Available)
            return "No ASUS charge-limit interface on this machine (no ATK WMI class, no ASUS "
                 + "service store). Whatever already caps the battery keeps doing it.";
        return "The firmware does not implement the ASUS charge-limit device and the ASUS "
             + "service store is missing, so there is nowhere to write the limit. Set it in MyASUS.";
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The limit the ASUS service currently believes in, or null. The ACPI side cannot be
    /// read back - the Linux driver notes the same and caches instead - so the registry is
    /// the only source of truth available. Readable without administrator.
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
        try { return SetLimitCore(Math.Clamp(percent, MinLimit, MaxLimit)); }
        finally { RaiseStateChanged(); }
    }

    private static ChargeLimitResult SetLimitCore(int percent)
    {
        if (Available) ScanCapabilitiesOnce();

        if (!CanSetLimit)
            return new ChargeLimitResult(ChargeLimitOutcome.Unavailable, percent,
                WhyCannotSetLimit() ?? "No route to the charge limit.");

        // 1. ACPI, unless the firmware has already said it does not implement the device.
        //    Re-asking on every change just fills the log with identical refusals.
        bool firmwareTook = false;
        int rc = 0;
        if (FirmwareRouteUsable)
        {
            rc = InvokeDevs(DevIdChargeLimit, (uint)percent);
            firmwareTook = rc == 1;
            if (!firmwareTook && _firmwareImplementsLimit is null)
            {
                _firmwareImplementsLimit = false;
                Log.Warn($"asus: firmware does not implement 0x{DevIdChargeLimit:X8} " +
                         "(DEVS returned " + rc + "); falling back to the ASUS service store");
            }
            else if (firmwareTook)
            {
                _firmwareImplementsLimit = true;
            }
        }

        // 2. The service's store, so it does not re-assert an older value over the top -
        //    and, on firmware without the ACPI device, the only route that works at all.
        string? storeError = WriteServiceLimit(percent);
        bool stored = storeError is null;
        if (stored) Log.Info($"asus: {ChargingRateValue} = {percent} written to the ASUS service store");

        if (firmwareTook)
        {
            return new ChargeLimitResult(ChargeLimitOutcome.AppliedByFirmware, percent,
                stored ? "Applied by firmware; ASUS service updated to match."
                       : $"Applied by firmware, but the ASUS service store was not updated ({storeError}), "
                         + "so it may revert on the next power event.");
        }

        if (stored)
        {
            return new ChargeLimitResult(ChargeLimitOutcome.StoredForService, percent,
                $"Saved to the ASUS service ({percent}%). It takes effect on the next "
                + "plug/unplug or resume.");
        }

        return new ChargeLimitResult(ChargeLimitOutcome.Failed, percent,
            $"Could not write the limit: {storeError}.");
    }

    /// <summary>Null on success, otherwise a short reason fit for the UI.</summary>
    private static string? WriteServiceLimit(int percent)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(OptimizationKey, writable: true);
            if (k is null)
            {
                Log.Warn($"asus: service store key missing: HKLM\\{OptimizationKey}");
                return "the ASUS service store key does not exist (is ASUS System Control Interface installed?)";
            }
            k.SetValue(ChargingRateValue, percent, RegistryValueKind.DWord);

            // Read back - a protected or policy-managed key can swallow the write.
            using var verify = Registry.LocalMachine.OpenSubKey(OptimizationKey);
            var back = verify?.GetValue(ChargingRateValue);
            if (back is int v && v == percent) return null;
            Log.Warn($"asus: wrote {ChargingRateValue}={percent} but it reads back as {back ?? "nothing"}");
            return $"the value did not stick (reads back as {back ?? "nothing"})";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"asus: {ChargingRateValue}: access denied (elevated: {IsElevated})");
            return IsElevated
                ? "access to the ASUS service store was denied even with administrator rights"
                : "administrator rights are needed";
        }
        catch (Exception ex)
        {
            Log.Warn($"asus: write {ChargingRateValue}: {ex.Message}");
            return ex.Message;
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
        // Locked so a SetLimit arriving mid-scan waits for the verdict instead of seeing
        // _scanned already true with _firmwareImplementsLimit still unset.
        lock (ScanGate)
        {
            if (_scanned) return;
            _scanned = true;
            if (!Available) return;
            ScanCore();
        }
        RaiseStateChanged();
    }

    private static void ScanCore()
    {
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

            if (supported.Count == 0)
            {
                // Nothing in the battery subgroup exists, so the charge-limit device will
                // not either. Recorded so the UI can say that plainly instead of offering
                // a switch that cannot work.
                _firmwareImplementsLimit = false;
                Log.Info("asus DSTS scan: no device in 0x00120050-0x00120070 is implemented");
            }
            else
            {
                Log.Info("asus DSTS scan, implemented devices: " + string.Join(" | ", supported));
            }
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

    /// <summary>
    /// Whether mains has been seen since the bypass started. Starting charge-to-full on
    /// battery is normal ("top up next time I plug in"); only an unplug *after* charging on
    /// mains should end it. Previously the very next sample saw "on battery" and cancelled
    /// it within a second.
    /// </summary>
    private static bool _bypassSawAc;

    public static ChargeLimitResult BeginChargeToFull(int limitToRestore, bool acOnlineNow = false)
    {
        _limitBeforeBypass = Math.Clamp(limitToRestore, MinLimit, MaxLimit);
        var result = SetLimit(MaxLimit);
        _bypassSawAc = acOnlineNow;
        BypassActive = result.Outcome is ChargeLimitOutcome.AppliedByFirmware
                                      or ChargeLimitOutcome.StoredForService;
        if (BypassActive)
            Log.Info($"asus: charge-to-full started, will restore {_limitBeforeBypass}%"
                     + (acOnlineNow ? "" : " (waiting for the charger)"));
        RaiseStateChanged();
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
            RaiseStateChanged();
            return;
        }

        if (acOnline) { _bypassSawAc = true; return; }

        // Unplugged after charging on mains: the top-up is over, put the limit back.
        if (_bypassSawAc)
        {
            BypassActive = false;
            Log.Info($"asus: unplugged during charge-to-full, restoring {_limitBeforeBypass}%");
            RestoreAsync(_limitBeforeBypass);
            RaiseStateChanged();
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
        if (!cfg.ChargeLimit.Enabled) return;
        if (BypassActive) return;
        if (!CanSetLimit) return;
        SetLimit(cfg.ChargeLimit.Percent);
    }
}
