using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryHud;

/// <summary>
/// Hand-rolled controls. Everything is drawn in OnRender rather than templated, so the
/// whole UI can be recoloured/resized from config.json without touching XAML resources.
/// </summary>
public static class Ui
{
    public static FontFamily ResolveFont(string spec)
    {
        foreach (var name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var ff = new FontFamily(name);
                if (ff.FamilyNames.Count > 0 || ff.Source == name) return ff;
            }
            catch { /* try next */ }
        }
        return new FontFamily("Segoe UI");
    }

    public static TextBlock Text(string text, Brush brush, double size, FontFamily font,
        FontWeight? weight = null, double opacity = 1.0)
        => new()
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontFamily = font,
            FontWeight = weight ?? FontWeights.Normal,
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

    public static Border Separator(Brush brush, double margin)
        => new()
        {
            Height = 1,
            Background = brush,
            Margin = new Thickness(0, margin, 0, margin),
            SnapsToDevicePixels = true,
        };
}

// ---------------------------------------------------------------------------

/// <summary>Compact charge/discharge history graph.</summary>
public sealed class Sparkline : FrameworkElement
{
    private double[] _values = Array.Empty<double>();
    public Color PositiveColor { get; set; } = Colors.LimeGreen;
    public Color NegativeColor { get; set; } = Colors.DeepSkyBlue;
    public Color GridColor { get; set; } = Color.FromArgb(0x22, 0x80, 0x80, 0x80);
    public double CornerRadius { get; set; } = 6;

    public void SetValues(IEnumerable<double> values)
    {
        _values = values.ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var bg = new SolidColorBrush(GridColor);
        bg.Freeze();
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), CornerRadius, CornerRadius);

        if (_values.Length < 2) return;

        double max = Math.Max(1, _values.Select(Math.Abs).DefaultIfEmpty(1).Max());
        double mid = h / 2.0;
        double stepX = w / (_values.Length - 1);

        // Zero line
        var zeroPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88)), 1);
        zeroPen.Freeze();
        dc.DrawLine(zeroPen, new Point(0, mid), new Point(w, mid));

        double avg = _values.Average();
        var lineColor = avg >= 0 ? PositiveColor : NegativeColor;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, mid - _values[0] / max * (mid - 2)), true, false);
            for (int i = 1; i < _values.Length; i++)
                ctx.LineTo(new Point(i * stepX, mid - _values[i] / max * (mid - 2)), true, false);
            ctx.LineTo(new Point(w, mid), false, false);
            ctx.LineTo(new Point(0, mid), false, false);
        }
        geo.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(0x66, lineColor.R, lineColor.G, lineColor.B),
            Color.FromArgb(0x08, lineColor.R, lineColor.G, lineColor.B),
            new Point(0, 0), new Point(0, 1));
        fill.Freeze();

        var stroke = new Pen(new SolidColorBrush(lineColor), 1.6) { LineJoin = PenLineJoin.Round };
        stroke.Freeze();

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), CornerRadius, CornerRadius));
        dc.DrawGeometry(fill, stroke, geo);
        dc.Pop();
    }
}

// ---------------------------------------------------------------------------

/// <summary>Flat, fully drawn slider. Supports free or stepped values plus the mouse wheel.</summary>
public sealed class TrackSlider : FrameworkElement
{
    private bool _dragging;
    private bool _hover;

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public double Step { get; set; }
    public int[]? DiscreteValues { get; set; }

    public Color TrackColor { get; set; } = Color.FromArgb(0x30, 0x80, 0x80, 0x80);
    public Color FillColor { get; set; } = Colors.DodgerBlue;
    public Color ThumbColor { get; set; } = Colors.White;
    public double Thickness { get; set; } = 4;
    public double ThumbRadius { get; set; } = 7;

    private double _value;
    public double Value
    {
        get => _value;
        set { _value = Clamp(value); InvalidateVisual(); }
    }

    /// <summary>Raised continuously while dragging.</summary>
    public event Action<double>? Preview;

    /// <summary>Raised once the user releases / commits a value.</summary>
    public event Action<double>? Committed;

    public TrackSlider()
    {
        Height = 22;
        Focusable = false;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseEnter += (_, _) => { _hover = true; InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = false; InvalidateVisual(); };
    }

    private double Clamp(double v)
    {
        v = Math.Clamp(v, Minimum, Maximum);
        if (DiscreteValues is { Length: > 0 })
            return DiscreteValues.OrderBy(x => Math.Abs(x - v)).First();
        if (Step > 0)
            return Math.Clamp(Math.Round((v - Minimum) / Step) * Step + Minimum, Minimum, Maximum);
        return v;
    }

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        Apply(e.GetPosition(this).X, commit: false);
        e.Handled = true;
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        Apply(e.GetPosition(this).X, commit: false);
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Apply(e.GetPosition(this).X, commit: true);
        e.Handled = true;
    }

    private void OnWheel(object s, MouseWheelEventArgs e)
    {
        double delta = Step > 0 ? Step : (Maximum - Minimum) / 20.0;
        Value = Value + (e.Delta > 0 ? delta : -delta);
        Preview?.Invoke(Value);
        Committed?.Invoke(Value);
        e.Handled = true;
    }

    private void Apply(double x, bool commit)
    {
        double usable = Math.Max(1, ActualWidth - ThumbRadius * 2);
        double t = Math.Clamp((x - ThumbRadius) / usable, 0, 1);
        Value = Minimum + t * (Maximum - Minimum);
        Preview?.Invoke(Value);
        if (commit) Committed?.Invoke(Value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1) return;

        double cy = h / 2;
        double usable = Math.Max(1, w - ThumbRadius * 2);
        double t = (Maximum - Minimum) <= 0 ? 0 : (Value - Minimum) / (Maximum - Minimum);
        double cx = ThumbRadius + t * usable;

        var track = new SolidColorBrush(TrackColor); track.Freeze();
        var fill = new SolidColorBrush(FillColor); fill.Freeze();
        var thumb = new SolidColorBrush(ThumbColor); thumb.Freeze();

        dc.DrawRoundedRectangle(track, null,
            new Rect(ThumbRadius - Thickness, cy - Thickness / 2, usable + Thickness * 2, Thickness),
            Thickness / 2, Thickness / 2);

        dc.DrawRoundedRectangle(fill, null,
            new Rect(ThumbRadius - Thickness, cy - Thickness / 2, Math.Max(Thickness, cx - ThumbRadius + Thickness), Thickness),
            Thickness / 2, Thickness / 2);

        double r = _dragging ? ThumbRadius * 0.8 : (_hover ? ThumbRadius * 1.08 : ThumbRadius);
        var halo = new SolidColorBrush(Color.FromArgb(0x30, FillColor.R, FillColor.G, FillColor.B)); halo.Freeze();
        dc.DrawEllipse(halo, null, new Point(cx, cy), r + 4, r + 4);
        dc.DrawEllipse(thumb, null, new Point(cx, cy), r, r);
        var core = new SolidColorBrush(FillColor); core.Freeze();
        dc.DrawEllipse(core, null, new Point(cx, cy), r * 0.5, r * 0.5);
    }
}

// ---------------------------------------------------------------------------

/// <summary>iOS-style segmented picker used for refresh rate and power mode.</summary>
public sealed class SegmentBar : Border
{
    private readonly List<Border> _cells = new();
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };

    public event Action<int>? Selected;
    public int SelectedIndex { get; private set; } = -1;

    public SegmentBar(IEnumerable<string> labels, int selectedIndex, Palette pal, FontFamily font, double fontSize, double radius)
    {
        Background = pal.ControlFillBrush;
        CornerRadius = new CornerRadius(radius);
        Padding = new Thickness(2);
        Child = _panel;
        SnapsToDevicePixels = true;

        int i = 0;
        foreach (var label in labels)
        {
            int index = i++;
            var cell = new Border
            {
                CornerRadius = new CornerRadius(Math.Max(2, radius - 2)),
                Padding = new Thickness(9, 4, 9, 5),
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = Ui.Text(label, pal.ForegroundBrush, fontSize, font),
            };
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(index, pal, raise: true); };
            cell.MouseEnter += (_, _) => { if (index != SelectedIndex) cell.Background = pal.ControlFillHoverBrush; };
            cell.MouseLeave += (_, _) => { if (index != SelectedIndex) cell.Background = Brushes.Transparent; };
            _cells.Add(cell);
            _panel.Children.Add(cell);
        }

        if (selectedIndex >= 0) Select(selectedIndex, pal, raise: false);
    }

    public void Select(int index, Palette pal, bool raise)
    {
        SelectedIndex = index;
        for (int i = 0; i < _cells.Count; i++)
        {
            bool on = i == index;
            _cells[i].Background = on ? pal.AccentBrush : Brushes.Transparent;
            if (_cells[i].Child is TextBlock tb)
            {
                tb.Foreground = on
                    ? new SolidColorBrush(Contrast(pal.Accent))
                    : pal.ForegroundBrush;
                tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        if (raise) Selected?.Invoke(index);
    }

    private static Color Contrast(Color c)
    {
        double l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return l > 0.6 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White;
    }
}

// ---------------------------------------------------------------------------

/// <summary>Small pill button used in the footer.</summary>
public sealed class PillButton : Border
{
    public PillButton(string label, Palette pal, FontFamily font, double fontSize, Action onClick, string? tooltip = null)
    {
        Background = pal.ControlFillBrush;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(9, 4, 9, 5);
        Cursor = Cursors.Hand;
        Child = Ui.Text(label, pal.ForegroundBrush, fontSize, font);
        if (tooltip is not null) ToolTip = tooltip;
        MouseEnter += (_, _) => Background = pal.ControlFillHoverBrush;
        MouseLeave += (_, _) => Background = pal.ControlFillBrush;

        // Mark the press handled as well as the release. Without this, a PillButton sitting
        // on a drag handle never fires: the parent's MouseLeftButtonDown calls DragMove(),
        // which enters a modal move loop and eats the MouseLeftButtonUp we are waiting for.
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
    }
}

// ---------------------------------------------------------------------------

/// <summary>Minimal drop-down for the power plan list.</summary>
public sealed class FlatDropDown : Border
{
    private readonly TextBlock _label;
    private readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };

    public event Action<int>? Selected;

    /// <summary>Number of dropdowns currently expanded anywhere in the app.</summary>
    public static int OpenCount { get; private set; }
    public static bool AnyOpen => OpenCount > 0;

    public FlatDropDown(IReadOnlyList<string> items, int selectedIndex, Palette pal, FontFamily font, double fontSize)
    {
        Background = pal.ControlFillBrush;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(9, 4, 7, 5);
        Cursor = Cursors.Hand;

        _label = Ui.Text(selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : "—",
            pal.ForegroundBrush, fontSize, font);
        _label.MaxWidth = 150;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_label);
        row.Children.Add(Ui.Text("  ⌄", pal.SubtleBrush, fontSize, font));

        // The popup is parented into the tree so PlacementTarget resolves reliably.
        var container = new Grid();
        container.Children.Add(row);
        container.Children.Add(_popup);
        Child = container;

        _popup.Opened += (_, _) => OpenCount++;
        _popup.Closed += (_, _) => OpenCount = Math.Max(0, OpenCount - 1);

        var list = new StackPanel();
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var item = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Child = Ui.Text(items[i], pal.ForegroundBrush, fontSize, font),
            };
            item.MouseEnter += (_, _) => item.Background = pal.ControlFillHoverBrush;
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _popup.IsOpen = false;
                _label.Text = items[index];
                Selected?.Invoke(index);
            };
            list.Children.Add(item);
        }

        var shellColor = Color.FromRgb(0x24, 0x24, 0x24);
        _popup.Child = new Border
        {
            Background = new SolidColorBrush(shellColor),
            BorderBrush = pal.SeparatorBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Child = new ScrollViewer { MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
        };
        _popup.PlacementTarget = this;

        MouseEnter += (_, _) => Background = pal.ControlFillHoverBrush;
        MouseLeave += (_, _) => Background = pal.ControlFillBrush;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; _popup.IsOpen = !_popup.IsOpen; };
    }

    public void SetLabel(string text) => _label.Text = text;
}

// ---------------------------------------------------------------------------

/// <summary>Battery level bar with rounded ends.</summary>
public sealed class LevelBar : FrameworkElement
{
    public double Fraction { get; set; }
    public Color TrackColor { get; set; } = Color.FromArgb(0x28, 0x80, 0x80, 0x80);
    public Color FillColor { get; set; } = Colors.DodgerBlue;

    public LevelBar() => Height = 8;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1) return;
        double r = h / 2;

        var track = new SolidColorBrush(TrackColor); track.Freeze();
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);

        double fw = Math.Clamp(Fraction, 0, 1) * w;
        if (fw <= 0.5) return;
        fw = Math.Max(fw, h);
        var fill = new SolidColorBrush(FillColor); fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, fw, h), r, r);
    }
}

// ---------------------------------------------------------------------------

public static class Fmt
{
    public static string Power(double mw, AppConfig cfg)
    {
        if (cfg.General.UseWatts)
        {
            int decimals = Math.Clamp(cfg.General.PowerDecimals, 0, 3);
            string frac = decimals > 0 ? "." + new string('0', decimals) : "";
            string spec = $"+0{frac};-0{frac};0";
            return (mw / 1000.0).ToString(spec, CultureInfo.InvariantCulture) + " W";
        }
        return mw.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " mW";
    }

    public static string Energy(double mwh) => $"{mwh / 1000.0:0.00} Wh";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }
}

// ---------------------------------------------------------------------------

/// <summary>Pill toggle used throughout the Settings window.</summary>
public sealed class ToggleSwitch : FrameworkElement
{
    private bool _hover;
    private bool _on;

    public Color OnColor { get; set; } = Colors.DodgerBlue;
    public Color OffColor { get; set; } = Color.FromArgb(0x40, 0x80, 0x80, 0x80);
    public Color KnobColor { get; set; } = Colors.White;

    public event Action<bool>? Toggled;

    public bool IsOn
    {
        get => _on;
        set { _on = value; InvalidateVisual(); }
    }

    public ToggleSwitch()
    {
        Width = 38;
        Height = 20;
        Cursor = Cursors.Hand;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            IsOn = !IsOn;
            Toggled?.Invoke(IsOn);
        };
        MouseEnter += (_, _) => { _hover = true; InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = false; InvalidateVisual(); };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        double r = h / 2;

        var track = new SolidColorBrush(_on ? OnColor : OffColor);
        track.Freeze();
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);

        double knobR = r - 3.5 + (_hover ? 0.6 : 0);
        double cx = _on ? w - r : r;
        var knob = new SolidColorBrush(_on ? KnobColor : Color.FromArgb(0xCC, KnobColor.R, KnobColor.G, KnobColor.B));
        knob.Freeze();
        dc.DrawEllipse(knob, null, new Point(cx, h / 2), knobR, knobR);
    }
}

// ---------------------------------------------------------------------------

/// <summary>A single row in the tray context menu.</summary>
public sealed class MenuRow : Border
{
    public MenuRow(string glyph, string label, Palette pal, FontFamily font, double fontSize,
        Action onClick, string? trailing = null)
    {
        Background = Brushes.Transparent;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10, 7, 12, 8);
        Cursor = Cursors.Hand;

        var grid = new DockPanel { LastChildFill = true };

        var icon = Ui.Text(glyph, pal.SubtleBrush, fontSize + 1, font);
        icon.Width = 20;
        icon.TextAlignment = TextAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        grid.Children.Add(icon);

        if (trailing is not null)
        {
            var t = Ui.Text(trailing, pal.SubtleBrush, fontSize - 1, font);
            t.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(t, Dock.Right);
            grid.Children.Add(t);
        }

        var text = Ui.Text(label, pal.ForegroundBrush, fontSize, font);
        text.Margin = new Thickness(6, 0, 0, 0);
        grid.Children.Add(text);

        Child = grid;

        MouseEnter += (_, _) => Background = pal.ControlFillHoverBrush;
        MouseLeave += (_, _) => Background = Brushes.Transparent;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
    }
}
