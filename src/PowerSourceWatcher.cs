using System;
using System.Windows.Interop;

namespace BatteryHud;

/// <summary>
/// Raises an event the instant Windows switches between mains and battery, instead of
/// waiting for the next poll.
///
/// This matters because Windows gives no way to set the power mode for the source you are
/// *not* on - PowerSetActiveOverlayScheme always targets the current one. So a mode chosen
/// for the other source has to be pushed at the moment of the transition, and the gap
/// between the plug moving and the mode being applied is exactly this notification's
/// latency. Polling made that gap up to a full second; this makes it imperceptible.
/// </summary>
public sealed class PowerSourceWatcher : IDisposable
{
    private const int HWND_MESSAGE = -3;

    private readonly HwndSource _source;
    private IntPtr _registration;

    /// <summary>True when running on mains.</summary>
    public event Action<bool>? SourceChanged;

    public PowerSourceWatcher()
    {
        // A message-only window: never shown, exists purely to receive WM_POWERBROADCAST.
        _source = new HwndSource(new HwndSourceParameters("BatteryHud.PowerSourceWatcher")
        {
            ParentWindow = new IntPtr(HWND_MESSAGE),
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);

        var guid = Native.GUID_ACDC_POWER_SOURCE;
        _registration = Native.RegisterPowerSettingNotification(
            _source.Handle, ref guid, Native.DEVICE_NOTIFY_WINDOW_HANDLE);

        if (_registration == IntPtr.Zero)
            Log.Warn("RegisterPowerSettingNotification failed; falling back to polling only");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_POWERBROADCAST || wParam.ToInt32() != Native.PBT_POWERSETTINGCHANGE)
            return IntPtr.Zero;

        try
        {
            var setting = System.Runtime.InteropServices.Marshal
                .PtrToStructure<Native.POWERBROADCAST_SETTING>(lParam);

            if (setting.PowerSetting == Native.GUID_ACDC_POWER_SOURCE)
            {
                // 0 = mains, 1 = battery, 2 = short-term (UPS).
                bool acOnline = setting.Data == 0;
                SourceChanged?.Invoke(acOnline);
            }
        }
        catch (Exception ex) { Log.Warn($"power broadcast: {ex.Message}"); }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            if (_registration != IntPtr.Zero)
            {
                Native.UnregisterPowerSettingNotification(_registration);
                _registration = IntPtr.Zero;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch (Exception ex) { Log.Warn($"power watcher dispose: {ex.Message}"); }
    }
}
