using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BatteryHud;

/// <summary>
/// Base for every window the app shows.
///
/// There is exactly one backdrop: the Win10-era acrylic from SetWindowCompositionAttribute.
/// The DWM system-backdrop types (Mica, Mica Alt, DWM acrylic) were tried and dropped -
/// they gave no control over tint and did not behave. The accent API takes a single ARGB
/// tint, which is what the colour picker and the transparency slider drive. The frost
/// slider is a separate white wash painted by WPF on top of the blur.
/// </summary>
public abstract class HudWindow : Window
{
    protected readonly AppConfig Cfg;
    protected readonly Palette Pal;
    private readonly bool _toolWindow;

    /// <summary>Set by subclasses so the fallback can make it opaque if blurring fails.</summary>
    protected Border? RootBorder { get; set; }

    protected HudWindow(AppConfig cfg, Palette pal, bool toolWindow)
    {
        Cfg = cfg;
        Pal = pal;
        _toolWindow = toolWindow;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = !toolWindow;
        Topmost = toolWindow;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    /// <summary>The frost layer. Sits over the blur, under the content.</summary>
    protected Brush GlassBrush => Pal.VeilBrush;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try { ApplyChrome(); }
        catch (Exception ex) { Log.Warn($"chrome: {ex.Message}"); }
    }

    private void ApplyChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var src = (HwndSource?)PresentationSource.FromVisual(this);

        if (_toolWindow)
        {
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        }

        int dark = Pal.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corner = Cfg.Appearance.CornerRadius <= 0 ? Native.DWMWCP_DONOTROUND
                   : Cfg.Appearance.CornerRadius <= 6 ? Native.DWMWCP_ROUNDSMALL
                   : Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        if (Cfg.Appearance.BorderColor.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            uint none = Native.DWMWA_COLOR_NONE;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref none, sizeof(uint));
        }
        else
        {
            var c = ConfigStore.ParseColor(Cfg.Appearance.BorderColor, Colors.Gray);
            uint bgr = (uint)(c.R | (c.G << 8) | (c.B << 16));
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref bgr, sizeof(uint));
        }

        // WPF must not paint an opaque surface or the blur never shows through.
        if (src is not null) src.CompositionTarget.BackgroundColor = Colors.Transparent;

        if (!ApplyAcrylic(hwnd))
        {
            Log.Warn("acrylic unavailable; falling back to an opaque window");
            if (RootBorder is not null) RootBorder.Background = Pal.OpaqueBrush;
            if (src is not null) src.CompositionTarget.BackgroundColor = Pal.Opaque;
        }
    }

    private bool ApplyAcrylic(IntPtr hwnd)
    {
        var tint = Pal.GlassTint;
        // ACCENT_POLICY.GradientColor is 0xAABBGGRR.
        uint abgr = (uint)((tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R);

        var policy = new Native.ACCENT_POLICY
        {
            AccentState = Native.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = abgr,
        };

        int size = Marshal.SizeOf<Native.ACCENT_POLICY>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new Native.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = Native.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            return Native.SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"acrylic: {ex.Message}");
            return false;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // ---------------------------------------------------------------- placement

    /// <summary>
    /// Puts the window next to the cursor, on the opposite side of the screen from the
    /// taskbar, clamped into the work area. Size is left to WPF (SWP_NOSIZE).
    /// </summary>
    protected void PositionNearCursor(bool centreOnCursor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        uint dpi;
        try { dpi = Native.GetDpiForWindow(hwnd); } catch { dpi = 96; }
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        int w, h;
        if (Native.GetWindowRect(hwnd, out var wr)) { w = wr.Right - wr.Left; h = wr.Bottom - wr.Top; }
        else { w = (int)Math.Round(ActualWidth * scale); h = (int)Math.Round(ActualHeight * scale); }

        Native.GetCursorPos(out var pt);
        IntPtr hmon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(), szDevice = "" };
        if (!Native.GetMonitorInfo(hmon, ref mi))
        {
            mi.rcWork = new Native.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            mi.rcMonitor = mi.rcWork;
        }

        int margin = (int)Math.Round(Cfg.Appearance.EdgeMargin * scale);

        int x = centreOnCursor ? pt.X - w / 2 : pt.X;
        x = Math.Clamp(x, mi.rcWork.Left + margin,
            Math.Max(mi.rcWork.Left + margin, mi.rcWork.Right - w - margin));

        bool taskbarOnTop = mi.rcWork.Top > mi.rcMonitor.Top;
        int y = taskbarOnTop ? mi.rcWork.Top + margin : mi.rcWork.Bottom - h - margin;
        y = Math.Clamp(y, mi.rcWork.Top + margin,
            Math.Max(mi.rcWork.Top + margin, mi.rcWork.Bottom - h - margin));

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOZORDER | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
    }

    protected void CentreOnCursorMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Native.GetWindowRect(hwnd, out var wr)) return;

        int w = wr.Right - wr.Left;
        int h = wr.Bottom - wr.Top;

        Native.GetCursorPos(out var pt);
        IntPtr hmon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(), szDevice = "" };
        if (!Native.GetMonitorInfo(hmon, ref mi)) return;

        int x = mi.rcWork.Left + ((mi.rcWork.Right - mi.rcWork.Left) - w) / 2;
        int y = mi.rcWork.Top + ((mi.rcWork.Bottom - mi.rcWork.Top) - h) / 2;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOZORDER | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
    }
}
