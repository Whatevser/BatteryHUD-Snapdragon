using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace BatteryHud;

/// <summary>
/// Resolves the palette actually used for drawing. Reads the Windows personalisation
/// registry keys directly (no WinRT dependency, works identically on ARM64) and merges
/// them with whatever the user overrode in config.json.
/// </summary>
public sealed class ThemeInfo
{
    public bool AppsDark { get; init; } = true;
    public bool SystemDark { get; init; } = true;   // the tray/taskbar theme
    public Color SystemAccent { get; init; } = Color.FromRgb(0x4C, 0xC2, 0xFF);

    public static ThemeInfo Read()
    {
        bool appsDark = true, sysDark = true;
        Color accent = Color.FromRgb(0x4C, 0xC2, 0xFF);

        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (personalize is not null)
            {
                if (personalize.GetValue("AppsUseLightTheme") is int a) appsDark = a == 0;
                if (personalize.GetValue("SystemUsesLightTheme") is int s) sysDark = s == 0;
            }
        }
        catch (Exception ex) { Log.Warn($"theme read: {ex.Message}"); }

        try
        {
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            // AccentColor is stored 0xAABBGGRR.
            if (dwm?.GetValue("AccentColor") is int raw)
            {
                uint v = unchecked((uint)raw);
                byte r = (byte)(v & 0xFF);
                byte g = (byte)((v >> 8) & 0xFF);
                byte b = (byte)((v >> 16) & 0xFF);
                accent = Color.FromRgb(r, g, b);
            }
        }
        catch (Exception ex) { Log.Warn($"accent read: {ex.Message}"); }

        return new ThemeInfo { AppsDark = appsDark, SystemDark = sysDark, SystemAccent = accent };
    }
}

/// <summary>The concrete brushes/colours a rebuild of the UI should use.</summary>
public sealed class Palette
{
    /// <summary>
    /// Always true. The app is dark-only: a light palette meant maintaining a second set of
    /// colours that never looked right against an acrylic backdrop tinted for dark. The
    /// field stays because the DWM dark-mode hint and the tray icon still need to say so.
    /// </summary>
    public bool Dark => true;

    /// <summary>
    /// Whether the TASKBAR is dark, which is a different registry value from the app theme
    /// and the one the tray icon has to follow - the app being dark-only says nothing about
    /// what colour is legible against the taskbar behind the icon.
    /// </summary>
    public required bool TaskbarDark { get; init; }

    public required Color Accent { get; init; }

    /// <summary>Glass tint including the alpha derived from Transparency.</summary>
    public required Color GlassTint { get; init; }

    /// <summary>Frost layer WPF paints over the blur. Transparent when GlassEffect is 0.</summary>
    public required Color Veil { get; init; }

    /// <summary>Opaque fallback if the compositor refuses to blur.</summary>
    public required Color Opaque { get; init; }

    public required Color Foreground { get; init; }
    public required Color Subtle { get; init; }
    public required Color Separator { get; init; }
    public required Color ControlFill { get; init; }
    public required Color ControlFillHover { get; init; }
    public required Color Charging { get; init; }
    public required Color Discharging { get; init; }
    public required Color Low { get; init; }
    public required Color Critical { get; init; }

    public SolidColorBrush AccentBrush => Freeze(Accent);
    public SolidColorBrush VeilBrush => Freeze(Veil);
    public SolidColorBrush OpaqueBrush => Freeze(Opaque);
    public SolidColorBrush ForegroundBrush => Freeze(Foreground);
    public SolidColorBrush SubtleBrush => Freeze(Subtle);
    public SolidColorBrush SeparatorBrush => Freeze(Separator);
    public SolidColorBrush ControlFillBrush => Freeze(ControlFill);
    public SolidColorBrush ControlFillHoverBrush => Freeze(ControlFillHover);

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Palette Build(AppConfig cfg, ThemeInfo theme)
    {
        var a = cfg.Appearance;

        Color accent = a.FollowSystemAccent
            ? theme.SystemAccent
            : ConfigStore.ParseColor(a.AccentColor, theme.SystemAccent);

        Color glass = ConfigStore.ParseColor(a.GlassColor, Color.FromRgb(0x1B, 0x1B, 0x1B));

        // Transparency 0..100 maps to the tint alpha the compositor blends over the blur.
        double t = Math.Clamp(a.Transparency, 0, 100) / 100.0;
        byte tintAlpha = (byte)Math.Round(255 * (1 - t));

        // Frost is a plain white wash painted by WPF on top of the blurred backdrop.
        double g = Math.Clamp(a.GlassEffect, 0, 100) / 100.0;
        byte veilAlpha = (byte)Math.Round(96 * g);

        // Blank means "follow the accent". This is the default for the level bar and the
        // discharge graph, so changing the accent actually changes them.
        Color discharging = string.IsNullOrWhiteSpace(a.DischargingColor)
            ? accent
            : ConfigStore.ParseColor(a.DischargingColor, accent);

        return new Palette
        {
            TaskbarDark = theme.SystemDark,
            Accent = accent,
            GlassTint = Color.FromArgb(tintAlpha, glass.R, glass.G, glass.B),
            Veil = Color.FromArgb(veilAlpha, 0xFF, 0xFF, 0xFF),
            Opaque = Color.FromRgb(glass.R, glass.G, glass.B),
            Foreground = ConfigStore.ParseColor(a.Foreground, Colors.White),
            Subtle = ConfigStore.ParseColor(a.SubtleForeground, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Separator = ConfigStore.ParseColor(a.Separator, Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            ControlFill = ConfigStore.ParseColor(a.ControlFill, Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            ControlFillHover = ConfigStore.ParseColor(a.ControlFillHover, Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            Charging = ConfigStore.ParseColor(a.ChargingColor, Color.FromRgb(0x4A, 0xDE, 0x80)),
            Discharging = discharging,
            Low = ConfigStore.ParseColor(a.LowColor, Color.FromRgb(0xFF, 0xB0, 0x20)),
            Critical = ConfigStore.ParseColor(a.CriticalColor, Color.FromRgb(0xFF, 0x4D, 0x4F)),
        };
    }
}

/// <summary>Fires when the Windows accent colour changes.</summary>
/// <summary>Fires when the user changes dark/light mode or the accent colour.</summary>
public sealed class ThemeWatcher : IDisposable
{
    private readonly System.Timers.Timer _poll = new(2000);
    private ThemeInfo _last = ThemeInfo.Read();

    public event Action<ThemeInfo>? Changed;
    public ThemeInfo Current => _last;

    public ThemeWatcher()
    {
        // WM_SETTINGCHANGE is unreliable for the accent colour, and SystemEvents on a
        // WPF+WinForms mixed app can be finicky, so a cheap 2 s registry poll is used
        // alongside the event. Both funnel into the same comparison.
        SystemEvents.UserPreferenceChanged += OnPref;
        _poll.Elapsed += (_, _) => Check();
        _poll.Start();
    }

    private void OnPref(object sender, UserPreferenceChangedEventArgs e) => Check();

    private void Check()
    {
        var now = ThemeInfo.Read();
        // The app theme is no longer tracked - the app is dark-only - but the taskbar theme
        // still is, because the tray icon has to stay legible against it.
        if (now.SystemAccent == _last.SystemAccent && now.SystemDark == _last.SystemDark) return;
        _last = now;
        Changed?.Invoke(now);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPref;
        _poll.Dispose();
    }
}
