using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace BatteryHud;

public sealed record DisplayTarget(string DeviceName, string FriendlyName, bool IsPrimary,
    uint Width, uint Height, uint CurrentHz, IReadOnlyList<uint> AvailableHz);

/// <summary>
/// Enumerates attached displays and their refresh rates via the classic
/// EnumDisplaySettingsEx / ChangeDisplaySettingsEx pair. Works unchanged on ARM64.
/// </summary>
public static class DisplayService
{
    public static List<DisplayTarget> Enumerate()
    {
        var result = new List<DisplayTarget>();

        for (uint i = 0; ; i++)
        {
            var dd = new Native.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>();
            if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var current = new Native.DEVMODE();
            current.dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>();
            if (!Native.EnumDisplaySettingsEx(dd.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref current, 0))
                continue;

            var rates = new SortedSet<uint>();
            for (int m = 0; ; m++)
            {
                var mode = new Native.DEVMODE();
                mode.dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>();
                if (!Native.EnumDisplaySettingsEx(dd.DeviceName, m, ref mode, 0)) break;
                if (mode.dmPelsWidth != current.dmPelsWidth) continue;
                if (mode.dmPelsHeight != current.dmPelsHeight) continue;
                if (mode.dmBitsPerPel != current.dmBitsPerPel) continue;
                if (mode.dmDisplayFrequency <= 1) continue; // 0/1 mean "hardware default"
                rates.Add(mode.dmDisplayFrequency);
            }

            // Monitor friendly name lives one level down.
            string friendly = dd.DeviceString;
            var mon = new Native.DISPLAY_DEVICE();
            mon.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>();
            if (Native.EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString))
                friendly = mon.DeviceString;

            result.Add(new DisplayTarget(
                dd.DeviceName,
                friendly,
                (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                current.dmPelsWidth, current.dmPelsHeight,
                current.dmDisplayFrequency,
                rates.ToList()));
        }

        return result;
    }

    public static bool SetRefreshRate(string deviceName, uint hz)
    {
        var mode = new Native.DEVMODE();
        mode.dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>();
        if (!Native.EnumDisplaySettingsEx(deviceName, Native.ENUM_CURRENT_SETTINGS, ref mode, 0))
            return false;

        mode.dmDisplayFrequency = hz;
        mode.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_BITSPERPEL | Native.DM_DISPLAYFREQUENCY;

        int test = Native.ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
        if (test != Native.DISP_CHANGE_SUCCESSFUL)
        {
            Log.Warn($"refresh {hz}Hz rejected on {deviceName}: {test}");
            return false;
        }

        int rc = Native.ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (rc != Native.DISP_CHANGE_SUCCESSFUL) Log.Warn($"ChangeDisplaySettingsEx: {rc}");
        return rc == Native.DISP_CHANGE_SUCCESSFUL;
    }
}
