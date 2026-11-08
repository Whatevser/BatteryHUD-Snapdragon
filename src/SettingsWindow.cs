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
    private readonly Func<bool> _acOnline;
    private readonly FontFamily _font;
    private readonly double _fs;
    private StackPanel _body = null!;

    public SettingsWindow(AppConfig cfg, Palette pal, Action apply, Action resetDefaults,
        Action openConfigFolder, Func<bool> runAtLoginEnabled, Action toggleRunAtLogin,
        Func<bool> acOnline)
        : base(cfg, pal, toolWindow: false)
    {
        _acOnline = acOnline;
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

        // The interface probe and DSTS scan finish in the background after startup, and
        // charge-to-full ends on its own. Follow all of it instead of showing whatever was
        // true when the window was built.
        AsusService.StateChanged += OnAsusStateChanged;
        Closed += (_, _) => AsusService.StateChanged -= OnAsusStateChanged;
    }

    private void OnAsusStateChanged()
        => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) RebuildChargeLimit(); }));

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

        // --------------------------------------------------------- charge limit
        BuildChargeLimitSection();

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
            AsusService.IsElevated
                ? "Turned on from here, it starts with administrator rights (no UAC prompt), "
                  + "so the charge limit works after a reboot."
                : ShellIntegration.RunAtLoginElevated()
                    ? "Starts with administrator rights."
                    : "Starts without administrator rights. To start elevated, restart as "
                      + "administrator and turn this off and on again.",
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

    // ------------------------------------------------------------ charge limit

    private StackPanel _chargeHost = null!;
    private string _chargeStatus = "";
    private bool _chargeBusy;

    /// <summary>
    /// The charge limit, as two switches that do not depend on each other: one sets the
    /// limit, one rescales the display. Built into its own panel so it can be rebuilt in
    /// place whenever anything about it changes.
    /// </summary>
    private void BuildChargeLimitSection()
    {
        Header("Charge limit");
        _chargeHost = new StackPanel();
        _body.Children.Add(_chargeHost);
        RebuildChargeLimit();
    }

    /// <summary>Re-renders the section from current state. Safe to call at any time.</summary>
    private void RebuildChargeLimit()
    {
        // The row helpers all append to _body; point it at the section while building.
        var outer = _body;
        _body = _chargeHost;
        _chargeHost.Children.Clear();
        try { BuildChargeLimitRows(); }
        catch (Exception ex) { Log.Warn($"charge limit section: {ex}"); }
        finally { _body = outer; }
    }

    private void BuildChargeLimitRows()
    {
        var cl = Cfg.ChargeLimit;
        bool canSet = AsusService.CanSetLimit;
        bool elevated = AsusService.IsElevated;

        int? stored = AsusService.ReadLimit();
        if (stored.HasValue)
            AddNote($"The ASUS service currently has the limit at {stored.Value}%.", 2, 4, 1.0, -1.5);

        // ---- hardware
        string hint = !canSet ? "Native ASUS ATKACPI unavailable; see below."
                    : $"Experimental: applies {cl.Percent}% directly through ATKACPI (no WMI fallback).";
        var hw = ToggleRowReturning($"Limit charging to {cl.Percent}%", hint, cl.Enabled, v =>
        {
            if (!v && AsusService.BypassUntil.HasValue)
                AsusService.CancelChargingCapBypass();
            cl.Enabled = v;
            Apply();
            if (!canSet)
            {
                _chargeStatus = "Saved. It will be applied once BatteryHud can reach the ASUS limit.";
                RebuildChargeLimit();
                return;
            }
            RunChargeAction(() =>
            {
                var r = AsusService.SetLimitSerialized(v ? cl.Percent : AsusService.MaxLimit);
                return (v ? "" : "Limit removed. ") + r.Detail;
            });
        });
        if (!canSet || _chargeBusy) hw.Opacity = 0.5;

        // ---- display, deliberately independent of the above
        ToggleRow($"Show {cl.Percent}% as 100%",
            "Display only. Use it when something else caps the battery and you would "
            + "rather not stare at a number that never reaches full.",
            cl.ShowAsFull, v => { cl.ShowAsFull = v; Apply(); });

        ToggleRow("Show the true percentage too", "Adds \"78% actual\" to the detail line",
            cl.ShowActual, v => { cl.ShowActual = v; Apply(); });

        // ---- timed bypass (no battery percentage or charger checks)
        SegmentRow("Charging Cap Bypass duration",
            new[] { "1 h", "2 h", "4 h", "6 h", "8 h", "12 h", "24 h" },
            Math.Max(0, Array.IndexOf(new double[] { 1, 2, 4, 6, 8, 12, 24 }, cl.BypassHours)),
            i => { cl.BypassHours = new double[] { 1, 2, 4, 6, 8, 12, 24 }[i]; Apply(); });
        if (canSet && cl.Enabled)
        {
            ToggleRow("Charging Cap Bypass",
                AsusService.BypassUntil.HasValue
                    ? $"Temporarily lifted until {AsusService.BypassUntil.Value.ToLocalTime():g}; click to restore now."
                    : "Temporarily lift the cap to 100% for the selected number of hours.",
                AsusService.BypassUntil.HasValue, v =>
                {
                    RunChargeAction(() =>
                    {
                        if (v)
                        {
                            var r = AsusService.BeginChargingCapBypass(cl.Percent, cl.BypassHours);
                            return r.Detail;
                        }
                        AsusService.CancelChargingCapBypass();
                        return "Charging Cap Bypass cancelled; restoring the normal limit.";
                    });
                });

            ToggleRow("Re-apply after plug/unplug and restart",
                "The BIOS drops the limit on restart and the ASUS service can overwrite it.",
                cl.ReapplyOnPowerChange, v => { cl.ReapplyOnPowerChange = v; Apply(); });
        }

        if (_chargeBusy) AddNote("Applying…", 6, 0, 1.0, -1.5);
        else if (_chargeStatus.Length > 0) AddNote(_chargeStatus, 6, 0, 1.0, -1.5);

        // ---- say plainly why the hardware half is off, when it is
        string? why = AsusService.WhyCannotSetLimit();
        if (why is not null) AddNote(why, 8, 0, 0.8, -2);

        if (!elevated)
        {
            var row = NewRow();
            var restart = new PillButton("Restart as administrator", Pal, _font, _fs - 1, () =>
            {
                if (ShellIntegration.RestartAsAdministrator(openSettings: true))
                {
                    _chargeStatus = "Restarting as administrator…";
                }
                else
                {
                    _chargeStatus = "Restart cancelled - the UAC prompt was declined or failed.";
                }
                RebuildChargeLimit();
            }, "Starts BatteryHud elevated (UAC prompt) and closes this instance");
            restart.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(restart);
            _body.Children.Add(row);
        }
    }

    /// <summary>
    /// WMI and the registry write-and-verify can take a second or more, so they run off the
    /// UI thread; the section shows "Applying…" and rebuilds with the outcome.
    /// </summary>
    private void RunChargeAction(Func<string> action)
    {
        if (_chargeBusy) return;
        _chargeBusy = true;
        RebuildChargeLimit();
        System.Threading.Tasks.Task.Run(() =>
        {
            string text;
            try { text = action(); }
            catch (Exception ex)
            {
                Log.Warn($"charge limit: {ex}");
                text = "Could not change the limit - see batteryhud.log.";
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _chargeBusy = false;
                _chargeStatus = text;
                if (IsVisible) RebuildChargeLimit();
            }));
        });
    }

    private void AddNote(string text, double top, double bottom, double opacity, double sizeDelta)
    {
        var t = Ui.Text(text, Pal.SubtleBrush, _fs + sizeDelta, _font);
        t.TextWrapping = TextWrapping.Wrap;
        t.Opacity = opacity;
        t.Margin = new Thickness(0, top, 0, bottom);
        _body.Children.Add(t);
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
