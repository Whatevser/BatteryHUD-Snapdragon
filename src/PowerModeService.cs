using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryHud;

/// <summary>The three modes Windows 11 exposes in Settings → System → Power &amp; battery.</summary>
public enum PowerMode { Efficiency, Balanced, Performance }

/// <summary>
/// The Windows 11 "Power mode" selector, i.e. the *overlay* scheme — not the classic
/// power plans. Facts this is built on (verified against current Windows 11 behaviour):
///
///  • Overlay GUIDs: efficiency 961cc777-…, balanced 00000000-… (the empty GUID),
///    performance ded574b5-….
///  • Windows stores the choice per power source in
///    HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes as
///    ActiveOverlayAcPowerScheme / ActiveOverlayDcPowerScheme. Those values are readable
///    by any user but *not* writable, even elevated — the key is protected.
///  • The only working setter is PowerSetActiveOverlayScheme, and it applies to whichever
///    source the machine is on right now. There is no public per-source setter.
///  • Overlays only apply on top of the Balanced base plan. If the active plan is
///    High performance / Power saver / Ultimate, Windows hides the Power mode selector
///    and so do we.
///
/// The app therefore exposes a single control that sets the mode for whatever source the
/// machine is on. Windows itself remembers the two independently, so unplugging restores
/// the mode you last chose while on battery - which the flyout picks up and reflects.
/// </summary>
public static class PowerModeService
{
    public static readonly Guid GuidEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid GuidBalanced = Guid.Empty;
    public static readonly Guid GuidPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>The stock Balanced base plan. Overlays only work on top of this.</summary>
    public static readonly Guid SchemeBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    private static bool? _apiAvailable;

    // ------------------------------------------------------------------ mapping

    public static Guid ToGuid(PowerMode m) => m switch
    {
        PowerMode.Efficiency => GuidEfficiency,
        PowerMode.Performance => GuidPerformance,
        _ => GuidBalanced,
    };

    public static PowerMode FromGuid(Guid g)
    {
        if (g == GuidEfficiency) return PowerMode.Efficiency;
        if (g == GuidPerformance) return PowerMode.Performance;
        return PowerMode.Balanced;
    }

    public static string Label(PowerMode m) => m switch
    {
        PowerMode.Efficiency => "Efficiency",
        PowerMode.Performance => "Performance",
        _ => "Balanced",
    };

    // --------------------------------------------------------------- capability

    /// <summary>True when powrprof exposes the overlay entry points at all.</summary>
    public static bool ApiAvailable
    {
        get
        {
            if (_apiAvailable.HasValue) return _apiAvailable.Value;
            try
            {
                _apiAvailable = Native.PowerGetEffectiveOverlayScheme(out _) == 0;
            }
            catch (EntryPointNotFoundException) { _apiAvailable = false; }
            catch (DllNotFoundException) { _apiAvailable = false; }
            catch (Exception ex) { Log.Warn($"overlay probe: {ex.Message}"); _apiAvailable = false; }
            return _apiAvailable.Value;
        }
    }

    /// <summary>
    /// Windows only honours power modes on top of the Balanced plan; on anything else the
    /// Settings page hides the selector.
    /// </summary>
    public static bool BasePlanSupportsModes()
    {
        try
        {
            var active = GetActivePlan();
            // Some OEM/ARM images ship a renamed clone of Balanced with a different GUID,
            // so treat "unknown GUID that still accepts overlays" as supported and only
            // reject the well-known non-Balanced stock plans.
            if (active == SchemeBalanced) return true;
            if (active == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")) return false; // Power saver
            if (active == new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")) return false; // High performance
            if (active == new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")) return false; // Ultimate
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"base plan probe: {ex.Message}");
            return true;
        }
    }

    public static string ActivePlanName()
    {
        try
        {
            Guid g = GetActivePlan();
            uint size = 0;
            if (Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
                return "unknown plan";
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0)
                    return "unknown plan";
                return (Marshal.PtrToStringUni(buf) ?? "unknown plan").Trim();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            Log.Warn($"plan name: {ex.Message}");
            return "unknown plan";
        }
    }

    private static Guid GetActivePlan()
    {
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero)
            return Guid.Empty;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { Native.LocalFree(p); }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The mode in force right now.</summary>
    public static PowerMode? ReadEffective()
    {
        if (!ApiAvailable) return null;
        try
        {
            if (Native.PowerGetEffectiveOverlayScheme(out Guid g) != 0) return null;
            return FromGuid(g);
        }
        catch (Exception ex) { Log.Warn($"effective overlay: {ex.Message}"); return null; }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Reads the mode that was last *set*, as opposed to the one in force.</summary>
    public static PowerMode? ReadActual()
    {
        try
        {
            if (Native.PowerGetActualOverlayScheme(out Guid g) != 0) return null;
            return FromGuid(g);
        }
        catch (Exception ex) { Log.Warn($"actual overlay: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Applies a mode to the *current* power source. Never throws: a failure here must not
    /// take the app down, it just gets logged and reported to the caller.
    /// </summary>
    public static bool ApplyToCurrentSource(PowerMode mode)
    {
        if (!ApiAvailable) return false;
        try
        {
            Guid g = ToGuid(mode);
            uint rc = Native.PowerSetActiveOverlayScheme(ref g);
            if (rc != 0)
            {
                Log.Warn($"PowerSetActiveOverlayScheme({Label(mode)}) returned {rc}" +
                         (rc == 87 ? " (ERROR_INVALID_PARAMETER)" : "") +
                         $"; active plan \"{ActivePlanName()}\"");
                return false;
            }

            var actual = ReadActual();
            if (actual is not null && actual != mode)
                Log.Warn($"set {Label(mode)} but the system reports {Label(actual.Value)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"apply overlay: {ex.Message}");
            return false;
        }
    }

    public static IReadOnlyList<PowerMode> Order { get; } =
        new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance };
}
