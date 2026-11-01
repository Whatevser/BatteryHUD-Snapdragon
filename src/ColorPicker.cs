using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryHud;

/// <summary>HSV helpers. WPF has no colour-space conversion built in.</summary>
public static class Hsv
{
    public static (double H, double S, double V) FromColor(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        double h = 0;
        if (d > 0.00001)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;

        double s = max <= 0 ? 0 : d / max;
        return (h, s, max);
    }

    public static Color ToColor(double h, double s, double v, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        double m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromArgb(alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>Saturation / value square for the currently selected hue.</summary>
public sealed class SvSquare : FrameworkElement
{
    private bool _drag;

    public double Hue { get; set; }
    public double Sat { get; set; }
    public double Val { get; set; } = 1;

    public event Action? Changed;
    public event Action? Committed;

    public SvSquare()
    {
        Height = 140;
        Cursor = Cursors.Cross;
        MouseLeftButtonDown += (_, e) => { _drag = true; CaptureMouse(); Apply(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (_drag) Apply(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_drag) return;
            _drag = false;
            ReleaseMouseCapture();
            Apply(e.GetPosition(this));
            Committed?.Invoke();
            e.Handled = true;
        };
    }

    private void Apply(Point p)
    {
        Sat = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1);
        Val = 1 - Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        var rect = new Rect(0, 0, w, h);

        var pure = new SolidColorBrush(Hsv.ToColor(Hue, 1, 1));
        pure.Freeze();
        dc.DrawRoundedRectangle(pure, null, rect, 6, 6);

        var toWhite = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255),
            new Point(0, 0), new Point(1, 0));
        toWhite.Freeze();
        dc.DrawRoundedRectangle(toWhite, null, rect, 6, 6);

        var toBlack = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black,
            new Point(0, 0), new Point(0, 1));
        toBlack.Freeze();
        dc.DrawRoundedRectangle(toBlack, null, rect, 6, 6);

        double cx = Sat * w, cy = (1 - Val) * h;
        var ringOuter = new Pen(Brushes.White, 2);
        ringOuter.Freeze();
        var ringInner = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), 1);
        ringInner.Freeze();
        dc.DrawEllipse(null, ringOuter, new Point(cx, cy), 6, 6);
        dc.DrawEllipse(null, ringInner, new Point(cx, cy), 7.5, 7.5);
    }
}

/// <summary>Rainbow strip.</summary>
public sealed class HueBar : FrameworkElement
{
    private bool _drag;

    public double Hue { get; set; }

    public event Action? Changed;
    public event Action? Committed;

    public HueBar()
    {
        Height = 18;
        Cursor = Cursors.Hand;
        MouseLeftButtonDown += (_, e) => { _drag = true; CaptureMouse(); Apply(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (_drag) Apply(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_drag) return;
            _drag = false;
            ReleaseMouseCapture();
            Apply(e.GetPosition(this));
            Committed?.Invoke();
            e.Handled = true;
        };
    }

    private void Apply(Point p)
    {
        Hue = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1) * 360;
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++)
            g.GradientStops.Add(new GradientStop(Hsv.ToColor(i * 60, 1, 1), i / 6.0));
        g.Freeze();

        dc.DrawRoundedRectangle(g, null, new Rect(0, 0, w, h), h / 2, h / 2);

        double x = Math.Clamp(Hue / 360.0 * w, 3, w - 3);
        var pen = new Pen(Brushes.White, 2);
        pen.Freeze();
        dc.DrawEllipse(null, pen, new Point(x, h / 2), h / 2 - 2, h / 2 - 2);
    }
}

/// <summary>Square + hue strip + hex box. Emits live changes and a commit on release.</summary>
public sealed class ColorPickerPanel : StackPanel
{
    private readonly SvSquare _sv = new();
    private readonly HueBar _hue = new();
    private readonly Border _preview;
    private readonly TextBox _hex;
    private bool _suppress;

    public Color Value { get; private set; }

    /// <summary>Live, while dragging.</summary>
    public event Action<Color>? Changed;

    /// <summary>On release, or when the hex box is confirmed.</summary>
    public event Action<Color>? Committed;

    public ColorPickerPanel(Color initial, Palette pal, FontFamily font, double fontSize)
    {
        Value = initial;
        Margin = new Thickness(0, 6, 0, 4);

        var (h, s, v) = Hsv.FromColor(initial);
        _sv.Hue = h; _sv.Sat = s; _sv.Val = v;
        _hue.Hue = h;

        _sv.Margin = new Thickness(0, 0, 0, 6);
        _hue.Margin = new Thickness(0, 0, 0, 6);

        _preview = new Border
        {
            Width = 34,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(initial),
            BorderBrush = pal.SeparatorBrush,
            BorderThickness = new Thickness(1),
        };

        _hex = new TextBox
        {
            Text = ConfigStore.ToHex(initial),
            Width = 96,
            Background = pal.ControlFillBrush,
            Foreground = pal.ForegroundBrush,
            CaretBrush = pal.ForegroundBrush,
            BorderThickness = new Thickness(0),
            FontFamily = font,
            FontSize = fontSize - 1,
            Padding = new Thickness(7, 3, 7, 4),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _hex.LostFocus += (_, _) => ApplyHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };

        var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var preset in new[]
                 {
                     "#FF4CC2FF", "#FF4ADE80", "#FFFFB020", "#FFFF4D4F", "#FFB48EFF",
                     "#FFFF7AB6", "#FF00C2A8", "#FFFFFFFF", "#FF808080", "#FF1B1B1B",
                 })
        {
            var c = ConfigStore.ParseColor(preset, Colors.Gray);
            var chip = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 5, 5),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderBrush = pal.SeparatorBrush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetValue(c, commit: true); };
            swatches.Children.Add(chip);
        }

        var bottom = new StackPanel { Orientation = Orientation.Horizontal };
        bottom.Children.Add(_preview);
        bottom.Children.Add(_hex);

        Children.Add(_sv);
        Children.Add(_hue);
        Children.Add(bottom);
        Children.Add(swatches);

        _sv.Changed += () => Emit(false);
        _sv.Committed += () => Emit(true);
        _hue.Changed += () => { _sv.Hue = _hue.Hue; _sv.InvalidateVisual(); Emit(false); };
        _hue.Committed += () => { _sv.Hue = _hue.Hue; _sv.InvalidateVisual(); Emit(true); };
    }

    private void Emit(bool commit)
    {
        if (_suppress) return;
        Value = Hsv.ToColor(_sv.Hue, _sv.Sat, _sv.Val);
        _preview.Background = new SolidColorBrush(Value);
        _hex.Text = ConfigStore.ToHex(Value);
        Changed?.Invoke(Value);
        if (commit) Committed?.Invoke(Value);
    }

    private void ApplyHex()
    {
        string t = _hex.Text.Trim();
        if (!t.StartsWith("#", StringComparison.Ordinal)) t = "#" + t;
        var parsed = ConfigStore.ParseColor(t, Value);
        // LostFocus runs this too. Committing an unchanged value is not harmless: the accent
        // row's commit also turns off "Follow Windows accent colour", so merely clicking into
        // the hex box and away again would flip that setting.
        if (parsed == Value)
        {
            _hex.Text = ConfigStore.ToHex(Value);
            return;
        }
        SetValue(parsed, commit: true);
    }

    private void SetValue(Color c, bool commit)
    {
        _suppress = true;
        var (h, s, v) = Hsv.FromColor(c);
        _sv.Hue = h; _sv.Sat = s; _sv.Val = v;
        _hue.Hue = h;
        _sv.InvalidateVisual();
        _hue.InvalidateVisual();
        _suppress = false;

        Value = c;
        _preview.Background = new SolidColorBrush(c);
        _hex.Text = ConfigStore.ToHex(c);
        Changed?.Invoke(c);
        if (commit) Committed?.Invoke(c);
    }
}
