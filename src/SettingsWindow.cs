using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryHud;

/// <summary>
/// The configuration UI. It mutates the live AppConfig instance and calls Apply() after
/// every interaction, so changes land on the tray icon and flyout immediately; Apply also
/// writes config.json (with the file watcher suppressed so it does not echo back).
/// </summary>
public sealed class SettingsWindow : HudWindow
{
    private readonly Action _apply;
    private readonly Action _resetDefaults;
    private readonly Action _openConfigFolder;
    private readonly Func<bool> _runAtLoginEnabled;
    private readonly Action _toggleRunAtLogin;
    private readonly FontFamily _font;
    private readonly double _fs;
    private StackPanel _body = null!;

    public SettingsWindow(AppConfig cfg, Palette pal, Action apply, Action resetDefaults,
        Action openConfigFolder, Func<bool> runAtLoginEnabled, Action toggleRunAtLogin)
        : base(cfg, pal, toolWindow: false)
    {
        _apply = apply;
        _resetDefaults = resetDefaults;
        _openConfigFolder = openConfigFolder;
        _runAtLoginEnabled = runAtLoginEnabled;
        _toggleRunAtLogin = toggleRunAtLogin;
        _font = Ui.ResolveFont(cfg.Appearance.FontFamily);
        _fs = cfg.Appearance.FontSize;

        Title = "BatteryHud settings";
        Width = 520;
        Height = 660;
        Topmost = false;
        Content = BuildRoot();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

    }

    public void ShowCentred()
    {
        Show();
        Activate();
        Dispatcher.BeginInvoke(new Action(CentreOnCursorMonitor), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ layout

    private UIElement BuildRoot()
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(Cfg.Appearance.CornerRadius),
            Background = GlassBrush,
        };
        RootBorder = root;

        var grid = new DockPanel { LastChildFill = true, Margin = new Thickness(0) };
        root.Child = grid;

        // ---- title bar (the window is borderless, so this is also the drag handle)
        var bar = new Border
        {
            Padding = new Thickness(16, 12, 10, 10),
            Background = Brushes.Transparent,
        };
        var barRow = new DockPanel { LastChildFill = true };

        var close = new PillButton("✕", Pal, _font, _fs, Close, "Close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(close, Dock.Right);
        barRow.Children.Add(close);

        barRow.Children.Add(Ui.Text("BatteryHud settings", Pal.ForegroundBrush, _fs + 3, _font, FontWeights.SemiBold));
        bar.Child = barRow;
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* not actually dragging */ }
            }
        };
        DockPanel.SetDock(bar, Dock.Top);
        grid.Children.Add(bar);

        // ---- footer
        var footer = new Border { Padding = new Thickness(16, 8, 16, 14) };
        var footRow = new WrapPanel();
        void AddFoot(UIElement e) { ((FrameworkElement)e).Margin = new Thickness(0, 0, 8, 0); footRow.Children.Add(e); }
        AddFoot(new PillButton("Open config folder", Pal, _font, _fs - 1, _openConfigFolder,
            "Shows config.json, samples.csv and the log"));
        AddFoot(new PillButton("Reset to defaults", Pal, _font, _fs - 1, () =>
        {
            // Close first: the reset opens a fresh window bound to the new config, and
            // closing afterwards would shut that one instead.
            Close();
            _resetDefaults();
        }, "Restores every setting and reopens this window"));
        footer.Child = footRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        grid.Children.Add(footer);

        // ---- scrollable body
        _body = new StackPanel { Margin = new Thickness(16, 0, 16, 8) };
        BuildSections();

        grid.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _body,
            Padding = new Thickness(0, 0, 6, 0),
        });

        return root;
    }

    private void BuildSections()
    {
        var a = Cfg.Appearance;
        var t = Cfg.Tray;
        var s = Cfg.Sections;
        var g = Cfg.General;

        // ---------------------------------------------------------- appearance
        Header("Appearance");

        ColorRow("Glass colour", "Tint blended over the blurred wallpaper",
            ConfigStore.ParseColor(a.GlassColor, Colors.Black),
            c => { a.GlassColor = ConfigStore.ToHex(c); Apply(); });

        ToggleRow("Blur behind the glass",
            "Off makes the window really see-through. With blur on, Windows fixes the blur "
            + "strength, so Transparency only swaps tint for blur.",
            a.Blur, v => { a.Blur = v; Apply(); });

        SliderRow("Transparency", 0, 100, 1, a.Transparency,
            v => { a.Transparency = v; Apply(); }, v => $"{v:0}%");

        SliderRow("Glass effect", 0, 100, 1, a.GlassEffect,
            v => { a.GlassEffect = v; Apply(); }, v => $"{v:0}%");

        ToggleRow("Follow Windows accent colour", null, a.FollowSystemAccent,
            v => { a.FollowSystemAccent = v; Apply(); });

        ColorRow("Accent colour", "Used for sliders, selected buttons and the level bar",
            ConfigStore.ParseColor(a.AccentColor, Pal.Accent),
            c =>
            {
                a.AccentColor = ConfigStore.ToHex(c);
                a.FollowSystemAccent = false;
                Apply();
            });

        ColorRow("Battery level colour", "Blank follows the accent. Only used while discharging.",
            ConfigStore.ParseColor(a.DischargingColor, Pal.Accent),
            c => { a.DischargingColor = ConfigStore.ToHex(c); Apply(); });

        ColorRow("Charging colour", null,
            ConfigStore.ParseColor(a.ChargingColor, Pal.Charging),
            c => { a.ChargingColor = ConfigStore.ToHex(c); Apply(); });

        SliderRow("Corner radius", 0, 20, 1, a.CornerRadius, v => { a.CornerRadius = v; Apply(); }, v => $"{v:0}");
        SliderRow("Flyout width", 260, 480, 10, a.Width, v => { a.Width = v; Apply(); }, v => $"{v:0} px");
        SliderRow("Text size", 10, 17, 0.5, a.FontSize, v => { a.FontSize = v; Apply(); }, v => $"{v:0.0}");
        SliderRow("Big percentage size", 18, 48, 1, a.HeadlineFontSize, v => { a.HeadlineFontSize = v; Apply(); }, v => $"{v:0}");
        SliderRow("Graph height", 24, 90, 2, a.SparklineHeight, v => { a.SparklineHeight = v; Apply(); }, v => $"{v:0} px");

        // ---------------------------------------------------------- tray icon
        Header("Tray icon");

        SegmentRow("Shape", new[] { "Horizontal", "Vertical" },
            t.IconStyle.Equals("batteryvertical", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            i => { t.IconStyle = i == 1 ? "batteryvertical" : "battery"; Apply(); });

        SegmentRow("Charging glyph", new[] { "Plug", "Bolt", "None" },
            !t.ShowChargingGlyph ? 2 : (t.ChargingGlyph.Equals("bolt", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
            i =>
            {
                t.ShowChargingGlyph = i != 2;
                if (i == 0) t.ChargingGlyph = "plug";
                if (i == 1) t.ChargingGlyph = "bolt";
                Apply();
            });

        ToggleRow("Colour icon by charge state", "Green charging, amber low, red critical",
            t.ColorByState, v => { t.ColorByState = v; Apply(); });

        SliderRow("Low threshold", 5, 50, 1, t.LowThreshold, v => { t.LowThreshold = (int)Math.Round(v); Apply(); }, v => $"{v:0}%");
        SliderRow("Critical threshold", 1, 30, 1, t.CriticalThreshold, v => { t.CriticalThreshold = (int)Math.Round(v); Apply(); }, v => $"{v:0}%");
        SliderRow("Outline thickness", 0.6, 1.6, 0.05, t.OutlineScale, v => { t.OutlineScale = v; Apply(); }, v => $"{v:0.00}×");

        // ------------------------------------------------------------ sections
        Header("What the flyout shows");

        ToggleRow("Charge info", "Percentage, state, time left, watts, voltage",
            s.Battery, v => { s.Battery = v; Apply(); });
        ToggleRow("Power graph", "The history sparkline, on its own",
            s.Graph, v => { s.Graph = v; Apply(); });
        ToggleRow("Health, cycles, chemistry", null, s.BatteryHealth, v => { s.BatteryHealth = v; Apply(); });
        ToggleRow("Charger session", "Wh gained and average watts since you plugged in",
            s.ChargerSession, v => { s.ChargerSession = v; Apply(); });
        ToggleRow("Brightness slider", null, s.Brightness, v => { s.Brightness = v; Apply(); });
        ToggleRow("Night light switch", null, s.NightLight, v => { s.NightLight = v; Apply(); });
        ToggleRow("Refresh rate", null, s.RefreshRate, v => { s.RefreshRate = v; Apply(); });
        ToggleRow("Power mode", "Efficiency / Balanced / Performance for the current power source",
            s.PowerMode, v => { s.PowerMode = v; Apply(); });

        // ----------------------------------------------------------- behaviour
        Header("Behaviour");

        var intervals = new[] { 500, 1000, 2000, 5000 };
        SegmentRow("Update every", new[] { "0.5 s", "1 s", "2 s", "5 s" },
            Math.Max(0, Array.IndexOf(intervals, g.PollIntervalMs)),
            i => { g.PollIntervalMs = intervals[i]; Apply(); });

        SliderRow("Rate smoothing", 1, 20, 1, g.RateSmoothingSamples,
            v => { g.RateSmoothingSamples = (int)Math.Round(v); Apply(); }, v => $"{v:0} samples");

        ToggleRow("Show watts instead of milliwatts", null, g.UseWatts, v => { g.UseWatts = v; Apply(); });
        ToggleRow("Close flyout when it loses focus", null, g.CloseOnFocusLost, v => { g.CloseOnFocusLost = v; Apply(); });

        ToggleRow("Start with Windows",
            ShellIntegration.LegacyElevatedTaskPresent()
                ? "An old elevated startup task remains. It is migrated automatically if BatteryHud "
                  + "runs as administrator once; otherwise remove the BatteryHud task in Task Scheduler."
                : "Starts with Windows for this user, without administrator rights.",
            _runAtLoginEnabled(), _ => _toggleRunAtLogin());

        ToggleRow("Log samples to CSV", "For comparing chargers in a spreadsheet afterwards",
            Cfg.Logging.CsvEnabled, v => { Cfg.Logging.CsvEnabled = v; Apply(); });
        ToggleRow("Only log while charging", null, Cfg.Logging.CsvOnlyWhileCharging,
            v => { Cfg.Logging.CsvOnlyWhileCharging = v; Apply(); });

        var note = Ui.Text("Everything here lives in config.json — it has a few extra knobs (individual colours, "
                           + "legacy blur tint, refresh-rate whitelist) and edits to the file apply immediately too.",
            Pal.SubtleBrush, _fs - 2, _font);
        note.TextWrapping = TextWrapping.Wrap;
        note.Opacity = 0.75;
        note.Margin = new Thickness(0, 14, 0, 4);
        _body.Children.Add(note);
    }

    private void Apply()
    {
        try { _apply(); }
        catch (Exception ex) { Log.Warn($"apply settings: {ex}"); }
    }

    // ------------------------------------------------------------- row helpers

    private void Header(string text)
    {
        var t = Ui.Text(text, Pal.ForegroundBrush, _fs + 1, _font, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 18, 0, 6);
        t.Opacity = 0.9;
        _body.Children.Add(t);
        _body.Children.Add(Ui.Separator(Pal.SeparatorBrush, 0));
    }

    private StackPanel LabelBlock(string label, string? hint)
    {
        var block = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        block.Children.Add(Ui.Text(label, Pal.ForegroundBrush, _fs, _font));
        if (hint is not null)
        {
            var h = Ui.Text(hint, Pal.SubtleBrush, _fs - 2, _font);
            h.TextWrapping = TextWrapping.Wrap;
            h.Opacity = 0.75;
            h.MaxWidth = 300;
            block.Children.Add(h);
        }
        return block;
    }

    private DockPanel NewRow()
        => new() { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };

    private void ToggleRow(string label, string? hint, bool value, Action<bool> set)
        => ToggleRowReturning(label, hint, value, set);

    /// <summary>ToggleRow, but hands the row back so the caller can dim it.</summary>
    private DockPanel ToggleRowReturning(string label, string? hint, bool value, Action<bool> set)
    {
        var row = NewRow();
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnColor = Pal.Accent,
            OffColor = Pal.ControlFill,
            Margin = new Thickness(12, 0, 0, 0),
        };
        toggle.Toggled += v => set(v);
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(LabelBlock(label, hint));
        _body.Children.Add(row);
        return row;
    }

    private void SliderRow(string label, double min, double max, double step, double value,
        Action<double> set, Func<double, string> format)
    {
        var head = NewRow();
        var valueText = Ui.Text(format(value), Pal.SubtleBrush, _fs, _font);
        DockPanel.SetDock(valueText, Dock.Right);
        head.Children.Add(valueText);
        head.Children.Add(LabelBlock(label, null));
        _body.Children.Add(head);

        var slider = new TrackSlider
        {
            Minimum = min,
            Maximum = max,
            Step = step,
            Value = value,
            FillColor = Pal.Accent,
            TrackColor = Pal.ControlFill,
            ThumbColor = Colors.White,
            Margin = new Thickness(0, 2, 0, 0),
        };
        slider.Preview += v => valueText.Text = format(v);
        slider.Committed += v => set(v);
        _body.Children.Add(slider);
    }

    private void SegmentRow(string label, IReadOnlyList<string> options, int selected, Action<int> set)
    {
        var row = NewRow();
        var bar = new SegmentBar(options, selected, Pal, _font, _fs - 1.5, 7)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };
        bar.Selected += set;
        DockPanel.SetDock(bar, Dock.Right);
        row.Children.Add(bar);
        row.Children.Add(LabelBlock(label, null));
        _body.Children.Add(row);
    }

    private void DropRow(string label, IReadOnlyList<string> options, int selected, Action<int> set, string? hint = null)
    {
        var row = NewRow();
        var drop = new FlatDropDown(options, selected, Pal, _font, _fs - 1)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };
        drop.Selected += set;
        DockPanel.SetDock(drop, Dock.Right);
        row.Children.Add(drop);
        row.Children.Add(LabelBlock(label, hint));
        _body.Children.Add(row);
    }

    /// <summary>
    /// A swatch that expands an inline HSV picker. The picker is inserted into the body
    /// straight after its row, so it pushes the rest down rather than floating over it.
    /// </summary>
    private void ColorRow(string label, string? hint, Color current, Action<Color> set)
    {
        var row = NewRow();

        var swatch = new Border
        {
            Width = 46,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(current),
            BorderBrush = Pal.SeparatorBrush,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(swatch, Dock.Right);
        row.Children.Add(swatch);
        row.Children.Add(LabelBlock(label, hint));
        _body.Children.Add(row);

        var picker = new ColorPickerPanel(current, Pal, _font, _fs)
        {
            Visibility = Visibility.Collapsed,
        };
        picker.Changed += c => swatch.Background = new SolidColorBrush(c);
        picker.Committed += c => { swatch.Background = new SolidColorBrush(c); set(c); };
        _body.Children.Add(picker);

        swatch.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            picker.Visibility = picker.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };
    }
}
