using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatteryHud;

public sealed class FlyoutActions
{
    public required Action ResetSession { get; init; }
    /// <summary>Persist the config and apply it across the app immediately.</summary>
    public required Action SaveConfig { get; init; }
}

public sealed class FlyoutWindow : HudWindow
{
    private readonly BatteryService _battery;
    private readonly FlyoutActions _actions;
    private readonly FontFamily _font;

    private TextBlock? _percentText, _stateText, _remainingText, _rateText, _detailText, _sessionText, _healthText;
    private LevelBar? _level;
    private Sparkline? _spark;
    private TextBlock? _brightnessValue;
    private TrackSlider? _brightnessSlider;
    private BrightnessService.BrightnessWatcher? _brightnessWatcher;
    private bool _suppressBrightnessEcho;
    private ToggleSwitch? _nightLightToggle;
    private bool? _nightLightKnown;
    private TextBlock? _powerModeNote;
    private SegmentBar? _modeBar;
    private PowerMode? _shownMode;
    private bool? _shownAc;
    private bool _modesSupported;

    public DateTime HiddenAt { get; private set; } = DateTime.MinValue;

    public FlyoutWindow(AppConfig cfg, Palette pal, BatteryService battery, FlyoutActions actions)
        : base(cfg, pal, toolWindow: true)
    {
        _battery = battery;
        _actions = actions;
        _font = Ui.ResolveFont(cfg.Appearance.FontFamily);

        SizeToContent = SizeToContent.Height;
        Width = cfg.Appearance.Width;
        Content = BuildRoot();

        Deactivated += (_, _) =>
        {
            if (FlatDropDown.AnyOpen) return;
            if (Cfg.General.CloseOnFocusLost) HideFlyout();
        };
        SizeChanged += (_, _) => { if (IsVisible) PositionNearCursor(centreOnCursor: true); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) HideFlyout(); };
    }

    // ------------------------------------------------------------------ layout

    private UIElement BuildRoot()
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(Cfg.Appearance.CornerRadius),
            Padding = new Thickness(Cfg.Appearance.Padding),
            Background = GlassBrush,
        };
        RootBorder = root;

        var stack = new StackPanel();
        root.Child = stack;

        double gap = Cfg.Appearance.RowSpacing;
        double fs = Cfg.Appearance.FontSize;

        if (Cfg.Sections.Battery) BuildBatterySection(stack, fs, gap);
        if (Cfg.Sections.Graph) BuildGraph(stack, gap);
        if (Cfg.Sections.BatteryHealth) BuildHealth(stack, fs);
        if (Cfg.Sections.ChargerSession) BuildSessionSection(stack, fs, gap);

        bool anyControl = Cfg.Sections.Brightness || Cfg.Sections.RefreshRate || Cfg.Sections.PowerMode;
        if (anyControl) stack.Children.Add(Ui.Separator(Pal.SeparatorBrush, gap));

        if (Cfg.Sections.Brightness) SafeBuild(() => BuildBrightness(stack, fs, gap), "brightness");
        if (Cfg.Sections.NightLight) SafeBuild(() => BuildNightLight(stack, fs, gap), "night light");
        if (Cfg.Sections.RefreshRate) SafeBuild(() => BuildRefreshRate(stack, fs, gap), "refresh rate");
        if (Cfg.Sections.PowerMode) SafeBuild(() => BuildPowerMode(stack, fs, gap), "power mode");

        return root;
    }

    /// <summary>A misbehaving driver or power API must degrade the section, not the app.</summary>
    private void SafeBuild(Action build, string what)
    {
        try { build(); }
        catch (Exception ex) { Log.Warn($"build {what}: {ex}"); }
    }

    private static DockPanel Row(double bottomMargin)
        => new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, bottomMargin) };

    private void BuildBatterySection(Panel host, double fs, double gap)
    {
        var head = Row(2);
        _percentText = Ui.Text("--%", Pal.ForegroundBrush, Cfg.Appearance.HeadlineFontSize, _font, FontWeights.SemiBold);
        DockPanel.SetDock(_percentText, Dock.Left);
        head.Children.Add(_percentText);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _stateText = Ui.Text("", Pal.SubtleBrush, fs, _font);
        _stateText.HorizontalAlignment = HorizontalAlignment.Right;
        _remainingText = Ui.Text("", Pal.SubtleBrush, fs, _font);
        _remainingText.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(_stateText);
        right.Children.Add(_remainingText);
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        host.Children.Add(head);

        _level = new LevelBar { Margin = new Thickness(0, 4, 0, gap), TrackColor = Pal.ControlFill };
        host.Children.Add(_level);

        _rateText = Ui.Text("", Pal.ForegroundBrush, fs + 2, _font, FontWeights.SemiBold);
        _rateText.Margin = new Thickness(0, 0, 0, 2);
        host.Children.Add(_rateText);

        _detailText = Ui.Text("", Pal.SubtleBrush, fs - 0.5, _font);
        _detailText.Margin = new Thickness(0, 0, 0, gap * 0.4);
        host.Children.Add(_detailText);
    }

    /// <summary>The history graph, independent of the numbers above it.</summary>
    private void BuildGraph(Panel host, double gap)
    {
        _spark = new Sparkline
        {
            Height = Cfg.Appearance.SparklineHeight,
            Margin = new Thickness(0, Cfg.Sections.Battery ? 2 : 0, 0, gap * 0.6),
            PositiveColor = Pal.Charging,
            NegativeColor = Pal.Discharging,
            GridColor = Pal.ControlFill,
            CornerRadius = 6,
        };
        host.Children.Add(_spark);
    }

    private void BuildHealth(Panel host, double fs)
    {
        _healthText = Ui.Text("", Pal.SubtleBrush, fs - 0.5, _font);
        host.Children.Add(_healthText);
    }

    private void BuildSessionSection(Panel host, double fs, double gap)
    {
        host.Children.Add(Ui.Separator(Pal.SeparatorBrush, gap * 0.8));
        var row = Row(0);

        var reset = new PillButton("Reset", Pal, _font, fs - 1, () => _actions.ResetSession(),
            "Restart the charge-session measurement (use this when you swap chargers)");
        reset.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(reset, Dock.Right);
        row.Children.Add(reset);

        _sessionText = Ui.Text("", Pal.SubtleBrush, fs - 0.5, _font);
        _sessionText.TextWrapping = TextWrapping.Wrap;
        _sessionText.TextTrimming = TextTrimming.None;
        _sessionText.MaxWidth = Cfg.Appearance.Width - Cfg.Appearance.Padding * 2 - 64;
        DockPanel.SetDock(_sessionText, Dock.Left);
        row.Children.Add(_sessionText);

        host.Children.Add(row);
    }

    private void BuildBrightness(Panel host, double fs, double gap)
    {
        int current = BrightnessService.GetInternal();
        if (current < 0)
        {
            host.Children.Add(Ui.Text("Brightness: not exposed by this panel", Pal.SubtleBrush, fs - 0.5, _font));
            return;
        }

        var header = Row(2);
        var label = Ui.Text("Brightness", Pal.ForegroundBrush, fs, _font);
        DockPanel.SetDock(label, Dock.Left);
        header.Children.Add(label);
        _brightnessValue = Ui.Text($"{current}%", Pal.SubtleBrush, fs, _font);
        DockPanel.SetDock(_brightnessValue, Dock.Right);
        header.Children.Add(_brightnessValue);
        host.Children.Add(header);

        var levels = BrightnessService.GetInternalLevels();
        var slider = new TrackSlider
        {
            Minimum = levels.First(),
            Maximum = levels.Last(),
            DiscreteValues = levels.Length is > 0 and < 128 ? levels : null,
            Step = Cfg.Sections.BrightnessStep,
            Value = current,
            FillColor = Pal.Accent,
            TrackColor = Pal.ControlFill,
            ThumbColor = Colors.White,
            Margin = new Thickness(0, 0, 0, gap),
        };

        DateTime last = DateTime.MinValue;
        void Push(double v)
        {
            int target = (int)Math.Round(v);
            _suppressBrightnessEcho = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                BrightnessService.SetInternal(target);
                // Let the WMI event for our own write pass before listening again.
                System.Threading.Tasks.Task.Delay(400).ContinueWith(_ => _suppressBrightnessEcho = false);
            });
        }

        slider.Preview += v =>
        {
            if (_brightnessValue is not null) _brightnessValue.Text = $"{(int)Math.Round(v)}%";
            if ((DateTime.UtcNow - last).TotalMilliseconds < 90) return;
            last = DateTime.UtcNow;
            Push(v);
        };
        slider.Committed += Push;

        host.Children.Add(slider);
        _brightnessSlider = slider;

        // Track changes made anywhere else: F-keys, Action Center, adaptive brightness.
        _brightnessWatcher?.Dispose();
        _brightnessWatcher = new BrightnessService.BrightnessWatcher();
        _brightnessWatcher.Changed += level => Dispatcher.BeginInvoke(new Action(() =>
        {
            // Ignore the echo of our own write, or the slider fights the user mid-drag.
            if (_suppressBrightnessEcho) return;
            if (_brightnessSlider is null) return;
            if (Math.Abs(_brightnessSlider.Value - level) < 0.5) return;
            _brightnessSlider.Value = level;
            if (_brightnessValue is not null) _brightnessValue.Text = $"{level}%";
        }));
    }

    private void BuildNightLight(Panel host, double fs, double gap)
    {
        var state = NightLightService.IsEnabled();

        if (state is null)
        {
            // The blob only exists once night light has been switched on at least once.
            var row = Row(gap);
            var label = Ui.Text("Night light", Pal.ForegroundBrush, fs, _font);
            label.Opacity = 0.5;
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            var open = new PillButton("Set up", Pal, _font, fs - 1,
                ShellIntegration.OpenNightLightSettings,
                "Night light has to be enabled once in Settings before it can be toggled here");
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            host.Children.Add(row);
            return;
        }

        var toggleRow = Row(gap);
        var text = Ui.Text("Night light", Pal.ForegroundBrush, fs, _font);
        DockPanel.SetDock(text, Dock.Left);
        toggleRow.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsOn = state.Value,
            OnColor = Pal.Accent,
            OffColor = Pal.ControlFill,
        };
        toggle.Toggled += v =>
        {
            if (!NightLightService.SetEnabled(v))
            {
                // Put the switch back rather than leaving it lying about the real state.
                toggle.IsOn = !v;
                Log.Warn("night light toggle failed");
            }
            _nightLightKnown = NightLightService.IsEnabled();
        };
        DockPanel.SetDock(toggle, Dock.Right);
        toggleRow.Children.Add(toggle);

        host.Children.Add(toggleRow);

        _nightLightToggle = toggle;
        _nightLightKnown = state;
    }

    private void BuildRefreshRate(Panel host, double fs, double gap)
    {
        var displays = DisplayService.Enumerate();

        foreach (var d in displays)
        {
            var rates = d.AvailableHz.ToList();
            if (Cfg.Sections.PreferredRefreshRates.Length > 0)
                rates = rates.Where(r => Cfg.Sections.PreferredRefreshRates.Contains((int)r)).ToList();
            if (rates.Count == 0) rates.Add(d.CurrentHz);
            if (rates.Count < 2 && displays.Count > 1) continue;

            var row = Row(gap);
            string name = displays.Count > 1
                ? (d.IsPrimary ? "Refresh (main)" : $"Refresh ({Trim(d.FriendlyName)})")
                : "Refresh rate";
            var label = Ui.Text(name, Pal.ForegroundBrush, fs, _font);
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            int selected = rates.FindIndex(r => r == d.CurrentHz);
            var bar = new SegmentBar(rates.Select(r => $"{r}"), selected, Pal, _font, fs - 0.5, 7);
            string device = d.DeviceName;
            bar.Selected += i =>
            {
                try
                {
                    // Track the rate actually in force, so a later rejected change reverts
                    // to it rather than to whatever was active when the flyout was built.
                    if (DisplayService.SetRefreshRate(device, rates[i])) selected = i;
                    else bar.Select(selected, Pal, raise: false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"set refresh: {ex.Message}");
                    bar.Select(selected, Pal, raise: false);
                }
            };
            bar.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(bar, Dock.Right);
            row.Children.Add(bar);

            host.Children.Add(row);
        }
    }

    private static string Trim(string s) => s.Length <= 14 ? s : s[..13] + "…";

    // ------------------------------------------------------------- power mode

    private void BuildPowerMode(Panel host, double fs, double gap)
    {
        if (!PowerModeService.ApiAvailable)
        {
            host.Children.Add(Ui.Text("Power mode: not available on this Windows build", Pal.SubtleBrush, fs - 0.5, _font));
            return;
        }

        bool supported = PowerModeService.BasePlanSupportsModes();
        _modesSupported = supported;
        var current = PowerModeService.ReadEffective();

        var row = Row(supported ? 2 : gap);
        var label = Ui.Text("Power mode", Pal.ForegroundBrush, fs, _font);
        label.Opacity = supported ? 1 : 0.5;
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);

        var order = PowerModeService.Order;
        int selected = current is null ? -1 : order.ToList().IndexOf(current.Value);

        var bar = new SegmentBar(new[] { "Eff", "Bal", "Perf" }, selected, Pal, _font, fs - 1.5, 7)
        {
            IsEnabled = supported,
            Opacity = supported ? 1 : 0.4,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        bar.Selected += i =>
        {
            try
            {
                var mode = order[i];
                _shownMode = mode;
                if (!PowerModeService.ApplyToCurrentSource(mode) && _powerModeNote is not null)
                    _powerModeNote.Text = "Windows refused the change — see batteryhud.log.";
            }
            catch (Exception ex)
            {
                Log.Warn($"power mode select: {ex}");
                if (_powerModeNote is not null) _powerModeNote.Text = "Could not change the power mode.";
            }
        };
        DockPanel.SetDock(bar, Dock.Right);
        row.Children.Add(bar);
        host.Children.Add(row);

        _modeBar = bar;
        _shownMode = current;

        _powerModeNote = Ui.Text("", Pal.SubtleBrush, fs - 2, _font);
        _powerModeNote.TextWrapping = TextWrapping.Wrap;
        _powerModeNote.Opacity = 0.75;
        _powerModeNote.Margin = new Thickness(0, 0, 0, gap);
        if (!supported)
            _powerModeNote.Text = $"Windows only applies power modes on top of the Balanced plan. "
                                + $"Active plan is \"{PowerModeService.ActivePlanName()}\" — switch it back in Control Panel.";
        host.Children.Add(_powerModeNote);
    }

    /// <summary>
    /// Re-reads everything that something else can change behind our back, and pushes it
    /// into the controls. Called every tick and again on open, so the panel never shows a
    /// stale value after night light fires on schedule or the power mode is changed in
    /// Settings. Brightness is not in here: it arrives by WMI event instead of polling.
    /// </summary>
    private void RefreshLiveState()
    {
        SyncNightLight();
        SyncPowerMode();
        SyncBrightness();
    }

    /// <summary>
    /// A cheap catch-up read. The WMI event covers changes while the flyout is open; this
    /// covers the gap where it was closed and no event was being listened for.
    /// </summary>
    private void SyncBrightness()
    {
        if (_brightnessSlider is null || _suppressBrightnessEcho) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            int level = BrightnessService.GetInternal();
            if (level < 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_brightnessSlider is null || _suppressBrightnessEcho) return;
                if (Math.Abs(_brightnessSlider.Value - level) < 0.5) return;
                _brightnessSlider.Value = level;
                if (_brightnessValue is not null) _brightnessValue.Text = $"{level}%";
            }));
        });
    }

    /// <summary>Keeps the switch honest when night light is changed elsewhere, or by schedule.</summary>
    private void SyncNightLight()
    {
        if (_nightLightToggle is null) return;
        var live = NightLightService.IsEnabled();
        if (live is null || live == _nightLightKnown) return;
        _nightLightKnown = live;
        _nightLightToggle.IsOn = live.Value;
    }

    /// <summary>
    /// Windows keeps a separate mode for mains and battery and swaps them itself when the
    /// plug moves, so the control just mirrors whatever is in force right now.
    /// </summary>
    private void SyncPowerMode()
    {
        if (_modeBar is null) return;

        var live = PowerModeService.ReadEffective();
        if (live is not null && live != _shownMode)
        {
            _shownMode = live;
            int idx = PowerModeService.Order.ToList().IndexOf(live.Value);
            if (idx >= 0) _modeBar.Select(idx, Pal, raise: false);
        }

        bool ac = _battery.Latest?.AcOnline ?? true;
        if (_shownAc != ac)
        {
            _shownAc = ac;
            if (_powerModeNote is not null && _modesSupported)
                _powerModeNote.Text = ac ? "Applies while plugged in" : "Applies while on battery";
        }
    }

    // ------------------------------------------------------------------ updates

    public void Tick()
    {
        if (!IsVisible) return;
        var s = _battery.Latest;
        if (s is null) return;

        double rate = _battery.SmoothedRateMw();
        var info = _battery.Info;

        // With a charge cap configured the cap reads as 100%; the raw value is still shown
        // in the detail line so nothing is hidden.
        double shown = _battery.DisplayPercent(s);

        if (_percentText is not null) _percentText.Text = $"{shown:0}%";

        string state = s.Charging ? "Charging"
            : s.AcOnline && shown >= 99 ? (_battery.CeilingActive ? "At limit" : "Full")
            : s.AcOnline ? "Plugged in"
            : "On battery";
        if (_stateText is not null) _stateText.Text = state;

        var remaining = _battery.TimeRemaining();
        if (_remainingText is not null)
            _remainingText.Text = remaining is null ? "" :
                (rate > 0 ? $"{Fmt.Duration(remaining.Value)} to full" : $"{Fmt.Duration(remaining.Value)} left");

        var color = shown <= Cfg.Tray.CriticalThreshold && !s.AcOnline ? Pal.Critical
                  : shown <= Cfg.Tray.LowThreshold && !s.AcOnline ? Pal.Low
                  : rate > 0 ? Pal.Charging : Pal.Discharging;

        if (_level is not null)
        {
            _level.Fraction = shown / 100.0;
            _level.FillColor = color;
            _level.InvalidateVisual();
        }

        if (_rateText is not null)
        {
            _rateText.Text = Math.Abs(rate) < 50 ? "idle" : Fmt.Power(rate, Cfg);
            _rateText.Foreground = new SolidColorBrush(color);
        }

        if (_detailText is not null)
        {
            var bits = new List<string>();
            if (s.VoltageMv > 0) bits.Add($"{s.VoltageMv / 1000.0:0.00} V");
            if (s.CapacityMwh > 0 && info is { FullChargedCapacity: > 0 })
                bits.Add($"{Fmt.Energy(s.CapacityMwh)} / {Fmt.Energy(info.FullChargedCapacity)}");
            if (s.VoltageMv > 0 && Math.Abs(rate) > 50)
                bits.Add($"{Math.Abs(rate) / (s.VoltageMv / 1000.0) / 1000.0:0.00} A");
            if (_battery.CeilingActive && Cfg.ChargeLimit.ShowActual)
                bits.Add($"{s.Percent:0}% actual · {Cfg.ChargeLimit.Percent}% cap");
            _detailText.Text = string.Join("  ·  ", bits);
        }

        if (_healthText is not null && info is not null)
        {
            var bits = new List<string>();
            double health = _battery.HealthPercent();
            if (health > 0) bits.Add($"health {health:0}%");
            if (info.CycleCount > 0) bits.Add($"{info.CycleCount} cycles");
            if (!string.IsNullOrWhiteSpace(info.Chemistry)) bits.Add(info.Chemistry);
            if (!string.IsNullOrWhiteSpace(info.Manufacturer)) bits.Add(info.Manufacturer);
            _healthText.Text = string.Join("  ·  ", bits);
        }

        if (_spark is not null)
        {
            var hist = _battery.History;
            var values = hist.Select(x => x.RateKnown ? (double)x.RateMw : 0).ToArray();
            if (values.All(v => Math.Abs(v) < 1))
            {
                values = hist.Select(x => (double)x.CapacityMwh).ToArray();
                if (values.Length > 1)
                {
                    double baseline = values.Average();
                    values = values.Select(v => v - baseline).ToArray();
                }
            }
            _spark.SetValues(values.TakeLast(240));
        }

        RefreshLiveState();

        if (_sessionText is not null)
        {
            var (dur, dMwh, dPct, avg) = _battery.SessionStats();
            dPct /= _battery.Ceiling;   // report the delta on the same scale as the headline
            string verb = _battery.SessionIsCharging ? "Charging" : "Discharging";
            _sessionText.Text = dur.TotalSeconds < 5
                ? $"{verb} session starting…"
                : $"{verb} session: {Fmt.Duration(dur)}  ·  {dMwh / 1000.0:+0.00;-0.00} Wh  ·  {dPct:+0.0;-0.0} %  ·  avg {Fmt.Power(avg, Cfg)}";
        }
    }

    // --------------------------------------------------------------- show / hide

    public void ShowFlyout()
    {
        Left = -20000;
        Top = -20000;
        RefreshLiveState();
        Show();
        Activate();
        Dispatcher.BeginInvoke(new Action(() => PositionNearCursor(centreOnCursor: true)), DispatcherPriority.Loaded);
    }

    public void HideFlyout()
    {
        HiddenAt = DateTime.UtcNow;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _brightnessWatcher?.Dispose();
        _brightnessWatcher = null;
        base.OnClosed(e);
    }
}
