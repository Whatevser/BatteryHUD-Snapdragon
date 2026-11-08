using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryHud;

/// <summary>
/// Experimental native ASUS ATKACPI path, following G-Helper's DEVS buffer layout.
/// No unknown device IDs are written: only the documented 0x00120057 charge limit.
/// </summary>
internal static class NativeAsusAcpi
{
    private const string DevicePath = @"\\.\ATKACPI";
    private const uint Ioctl = 0x0022240C;
    private const uint Devs = 0x53564544;
    private const uint ChargeLimitId = 0x00120057;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle, uint code,
        byte[] input, uint inputSize, byte[] output, uint outputSize,
        out uint bytesReturned, IntPtr overlapped);

    private static SafeFileHandle Open()
        => CreateFile(DevicePath, 0xC0000000, 0x00000003, IntPtr.Zero, 3, 0, IntPtr.Zero);

    public static bool Available
    {
        get
        {
            try
            {
                using var h = Open();
                return !h.IsInvalid;
            }
            catch { return false; }
        }
    }

    /// <summary>Return 1 if accepted, 0 if explicitly refused, -1 if transport unavailable.</summary>
    public static int SetChargeLimit(int percent)
    {
        try
        {
            using var h = Open();
            if (h.IsInvalid) return -1;

            // Same 16-byte DEVS packet as G-Helper: method, arg bytes, device ID, value.
            var request = new byte[16];
            BitConverter.GetBytes(Devs).CopyTo(request, 0);
            BitConverter.GetBytes((uint)8).CopyTo(request, 4);
            BitConverter.GetBytes(ChargeLimitId).CopyTo(request, 8);
            BitConverter.GetBytes((uint)percent).CopyTo(request, 12);
            var response = new byte[16];
            if (!DeviceIoControl(h, Ioctl, request, (uint)request.Length,
                    response, (uint)response.Length, out uint bytes, IntPtr.Zero)
                || bytes < sizeof(int))
            {
                Log.Warn($"asus ATKACPI DEVS: DeviceIoControl failed ({Marshal.GetLastWin32Error()})");
                return -1;
            }
            int result = BitConverter.ToInt32(response, 0);
            Log.Info($"asus ATKACPI DEVS charge limit = {percent} -> {result}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"asus ATKACPI DEVS failed: {ex.Message}");
            return -1;
        }
    }
}
