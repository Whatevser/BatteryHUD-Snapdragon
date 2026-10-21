using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using D = System.Drawing;

namespace BatteryHud;

/// <summary>
/// Draws the notification-area icon: a battery shell that fills and empties with the
/// charge level, with a power-plug glyph knocked through it while charging. No digits.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIconRenderer
{
    public static int ResolveSize(AppConfig cfg)
    {
        if (cfg.Tray.IconSize > 0) return Math.Clamp(cfg.Tray.IconSize, 16, 64);
        uint dpi;
        try { dpi = Native.GetDpiForSystem(); } catch { dpi = 96; }
        double scale = dpi / 96.0;
        return scale switch
        {
            >= 2.4 => 40,
            >= 1.9 => 32,
            >= 1.4 => 24,
            >= 1.2 => 20,
            _ => 16,
        };
    }

    private static D.Color Pick(AppConfig cfg, Palette pal, BatterySample s, double percent)
    {
        if (!cfg.Tray.ColorByState)
        {
            if (!cfg.Tray.ForegroundColor.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var custom = ConfigStore.ParseColor(cfg.Tray.ForegroundColor, System.Windows.Media.Colors.White);
                return D.Color.FromArgb(custom.A, custom.R, custom.G, custom.B);
            }
            return pal.TaskbarDark ? D.Color.White : D.Color.FromArgb(0x18, 0x18, 0x18);
        }

        System.Windows.Media.Color m;
        if (percent <= cfg.Tray.CriticalThreshold && !s.AcOnline) m = pal.Critical;
        else if (percent <= cfg.Tray.LowThreshold && !s.AcOnline) m = pal.Low;
        else if (s.Charging || s.AcOnline) m = pal.Charging;
        else m = pal.TaskbarDark ? System.Windows.Media.Colors.White : System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18);
        return D.Color.FromArgb(m.A, m.R, m.G, m.B);
    }

    /// <param name="percent">
    /// What to draw, which is not necessarily s.Percent: with a charge cap configured the
    /// icon fills to full at the cap rather than at a 100% the pack never reaches.
    /// </param>
    public static Icon Render(AppConfig cfg, Palette pal, BatterySample s, double percent)
    {
        int size = ResolveSize(cfg);
        var fg = Pick(cfg, pal, s, percent);
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(D.Color.Transparent);
            bool vertical = cfg.Tray.IconStyle.Equals("batteryvertical", StringComparison.OrdinalIgnoreCase);
            DrawBattery(g, size, fg, cfg, s, percent, vertical);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            Native.DestroyIcon(h);
            bmp.Dispose();
        }
    }

    // ------------------------------------------------------------------ battery

    private static void DrawBattery(Graphics g, int size, D.Color fg, AppConfig cfg, BatterySample s,
        double percent, bool vertical)
    {
        float stroke = Math.Max(1.0f, size * 0.072f * (float)Math.Clamp(cfg.Tray.OutlineScale, 0.5, 2.0));
        float capThickness = Math.Max(1.2f, size * 0.085f);
        float inset = stroke * 0.5f + size * 0.03f;
        float fraction = (float)Math.Clamp(percent, 0, 100) / 100f;

        RectangleF body, cap;
        if (!vertical)
        {
            float bodyH = size * 0.54f;
            float bodyW = size - inset * 2f - capThickness - size * 0.045f;
            body = new RectangleF(inset, (size - bodyH) / 2f, bodyW, bodyH);
            cap = new RectangleF(body.Right + size * 0.045f, size / 2f - bodyH * 0.20f, capThickness, bodyH * 0.40f);
        }
        else
        {
            float bodyW = size * 0.54f;
            float bodyH = size - inset * 2f - capThickness - size * 0.045f;
            body = new RectangleF((size - bodyW) / 2f, inset + capThickness + size * 0.045f, bodyW, bodyH);
            cap = new RectangleF(size / 2f - bodyW * 0.20f, inset, bodyW * 0.40f, capThickness);
        }

        // Shell
        using (var pen = new Pen(fg, stroke) { LineJoin = LineJoin.Round })
        using (var path = RoundedRect(body, size * 0.11f))
            g.DrawPath(pen, path);

        using (var brush = new SolidBrush(fg))
        using (var capPath = RoundedRect(cap, size * 0.035f))
            g.FillPath(brush, capPath);

        // Fill
        float pad = stroke * 1.05f;
        var inner = RectangleF.Inflate(body, -pad, -pad);
        if (inner.Width > 0 && inner.Height > 0 && fraction > 0.005f)
        {
            RectangleF fr = vertical
                ? new RectangleF(inner.X, inner.Bottom - inner.Height * fraction, inner.Width, inner.Height * fraction)
                : new RectangleF(inner.X, inner.Y, inner.Width * fraction, inner.Height);

            // Keep a sliver visible at very low charge instead of nothing at all.
            if (!vertical) fr.Width = Math.Max(fr.Width, stroke * 0.9f);
            else
            {
                float min = stroke * 0.9f;
                if (fr.Height < min) { fr.Y = inner.Bottom - min; fr.Height = min; }
            }

            using var brush = new SolidBrush(fg);
            using var fp = RoundedRect(fr, size * 0.045f);
            g.FillPath(brush, fp);
        }

        if (!cfg.Tray.ShowChargingGlyph) return;
        if (!(s.Charging || s.AcOnline)) return;

        var center = new PointF(body.X + body.Width / 2f, body.Y + body.Height / 2f);
        bool bolt = cfg.Tray.ChargingGlyph.Equals("bolt", StringComparison.OrdinalIgnoreCase);
        float glyphH = vertical ? body.Width * 1.15f : body.Height * 1.22f;

        // Punch a transparent hole slightly larger than the glyph so it stays legible on
        // top of the fill, then draw the glyph itself inside that hole.
        var previous = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var hole = bolt ? BoltPath(center, glyphH * 1.02f) : PlugPath(center, glyphH * 1.02f))
        using (var clear = new SolidBrush(D.Color.FromArgb(0, 0, 0, 0)))
        using (var clearPen = new Pen(D.Color.FromArgb(0, 0, 0, 0), stroke * 1.6f) { LineJoin = LineJoin.Round })
        {
            g.FillPath(clear, hole);
            g.DrawPath(clearPen, hole);
        }
        g.CompositingMode = previous;

        using (var glyphBrush = new SolidBrush(fg))
        using (var glyph = bolt ? BoltPath(center, glyphH * 0.82f) : PlugPath(center, glyphH * 0.82f))
            g.FillPath(glyphBrush, glyph);
    }

    // ------------------------------------------------------------------- glyphs

    /// <summary>Two-prong plug with a short cable, drawn to fit a box of the given height.</summary>
    private static GraphicsPath PlugPath(PointF center, float height)
    {
        float h = height, w = height * 0.66f;
        float x = center.X - w / 2f, y = center.Y - h / 2f;

        var p = new GraphicsPath { FillMode = FillMode.Winding };

        // prongs
        p.AddRectangle(new RectangleF(x + w * 0.20f, y, w * 0.15f, h * 0.30f));
        p.AddRectangle(new RectangleF(x + w * 0.65f, y, w * 0.15f, h * 0.30f));

        // body
        using (var body = RoundedRect(new RectangleF(x, y + h * 0.26f, w, h * 0.40f), w * 0.16f))
            p.AddPath(body, false);

        // cable
        p.AddRectangle(new RectangleF(x + w * 0.40f, y + h * 0.62f, w * 0.20f, h * 0.38f));

        return p;
    }

    private static GraphicsPath BoltPath(PointF center, float height)
    {
        float h = height, w = height * 0.52f;
        float x = center.X - w / 2f, y = center.Y - h / 2f;
        var pts = new[]
        {
            new PointF(x + w * 0.58f, y),
            new PointF(x + w * 0.02f, y + h * 0.56f),
            new PointF(x + w * 0.44f, y + h * 0.56f),
            new PointF(x + w * 0.34f, y + h),
            new PointF(x + w * 0.98f, y + h * 0.40f),
            new PointF(x + w * 0.54f, y + h * 0.40f),
        };
        var p = new GraphicsPath();
        p.AddPolygon(pts);
        return p;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        radius = Math.Max(0.1f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
        float d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
