using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace BatteryHud;

[SupportedOSPlatform("windows")]
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\BatteryHud.SingleInstance", out bool created);
        if (!created)
        {
            WinForms.MessageBox.Show("BatteryHud is already running (look in the notification area).",
                "BatteryHud", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
            return 1;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Warn($"unhandled: {(e.ExceptionObject as Exception)?.ToString() ?? "?"}");

        var app = new HudApp(args);
        try { return app.Run(); }
        catch (Exception ex) { Log.Warn($"fatal: {ex}"); throw; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class HudApp : Application
{
    private AppConfig _cfg = null!;
    private Palette _pal = null!;
    private ThemeWatcher _themeWatcher = null!;
    private ConfigWatcher _configWatcher = null!;
    private PowerSourceWatcher? _powerWatcher;
    private BatteryService _battery = null!;
    private WinForms.NotifyIcon _tray = null!;
    private DispatcherTimer _timer = null!;

    private FlyoutWindow? _flyout;
    private TrayMenuWindow? _menu;
    private SettingsWindow? _settings;

    private System.Drawing.Icon? _currentIcon;
    private string _iconSignature = "";
    private string _appearanceSignature = "";
    private bool? _lastAcOnline;
    private readonly string[] _args;

    public HudApp(string[] args)
    {
        _args = args;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A driver hiccup or a power API returning something unexpected must never take
        // the tray icon down with it.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Warn($"dispatcher: {ex.Exception}");
            ex.Handled = true;
        };

        _cfg = ConfigStore.Load();
        _themeWatcher = new ThemeWatcher();
        _pal = Palette.Build(_cfg, _themeWatcher.Current);
        _appearanceSignature = AppearanceSignature();
        _battery = new BatteryService(_cfg);

        _tray = new WinForms.NotifyIcon { Visible = true, Text = "BatteryHud" };
        _tray.MouseClick += OnTrayClick;

        // Windows swaps the power mode itself when the plug moves; this just makes the
        // flyout reflect it immediately instead of on the next poll.
        try
        {
            _powerWatcher = new PowerSourceWatcher();
            _powerWatcher.SourceChanged += ac => Dispatcher.Invoke(() => OnPowerSourceChanged(ac));
        }
        catch (Exception ex) { Log.Warn($"power watcher: {ex.Message}"); }

        _configWatcher = new ConfigWatcher();
        _configWatcher.Changed += () => Dispatcher.Invoke(ReloadConfigFromDisk);
        _themeWatcher.Changed += _ => Dispatcher.Invoke(RebuildVisuals);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(_cfg.General.PollIntervalMs, 250, 30000)),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Tick();

        System.Threading.Tasks.Task.Run(() =>
        {
            try { BrightnessService.GetInternal(); } catch { /* warm-up only */ }
            try { _ = PowerModeService.ApiAvailable; } catch { /* warm-up only */ }
            try
            {
                // The BIOS clears the charge limit on every restart, so it has to be set
                // again each run. Also logs which ACPI devices this firmware implements.
                if (_cfg.ChargeLimit.Enabled) AsusService.Reapply(_cfg);
                else AsusService.ScanCapabilitiesOnce();
            }
            catch (Exception ex) { Log.Warn($"asus startup: {ex.Message}"); }
        });

        if (_args.Contains("--settings")) OpenSettings();
        Log.Info("started");
    }

    /// <summary>Driven by WM_POWERBROADCAST, so this runs the instant the plug moves.</summary>
    private void OnPowerSourceChanged(bool acOnline)
    {
        _lastAcOnline = acOnline;

        // The ASUS service re-asserts its own stored limit across power transitions, so
        // ours has to be pushed again or it quietly reverts.
        if (_cfg.ChargeLimit.ReapplyOnPowerChange)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { AsusService.Reapply(_cfg); }
                catch (Exception ex) { Log.Warn($"asus reapply: {ex.Message}"); }
            });
        }

        Tick();
    }

    // -------------------------------------------------------------------- loop

    private void Tick()
    {
        BatterySample sample;
        try { sample = _battery.Poll(); }
        catch (Exception ex) { Log.Warn($"poll: {ex.Message}"); return; }

        _lastAcOnline = sample.AcOnline;

        // Ends a one-time charge-to-full once the pack is actually full. Uses the true
        // percentage, never the rescaled one, or a display cap would end it early.
        if (AsusService.BypassActive)
        {
            // Cheap flag logic here; the restore write itself is handed to a background
            // task inside the service, so the poll tick never blocks on WMI.
            try { AsusService.OnBatterySample(sample.Percent, sample.AcOnline); }
            catch (Exception ex) { Log.Warn($"asus bypass: {ex.Message}"); }
        }

        UpdateTrayIcon(sample);
        _flyout?.Tick();
    }

    private void UpdateTrayIcon(BatterySample s)
    {
        double shown = _battery.DisplayPercent(s);
        string sig = $"{(int)Math.Round(shown)}|{s.Charging}|{s.AcOnline}|{_pal.TaskbarDark}|" +
                     $"{_cfg.Tray.IconStyle}|{_cfg.Tray.ChargingGlyph}|" +
                     $"{_cfg.Tray.ShowChargingGlyph}|{_cfg.Tray.ColorByState}|{_cfg.Tray.OutlineScale}|" +
                     $"{_cfg.Tray.LowThreshold}|{_cfg.Tray.CriticalThreshold}";
        if (sig != _iconSignature)
        {
            _iconSignature = sig;
            try
            {
                var icon = TrayIconRenderer.Render(_cfg, _pal, s, shown);
                _tray.Icon = icon;
                _currentIcon?.Dispose();
                _currentIcon = icon;
            }
            catch (Exception ex) { Log.Warn($"icon render: {ex.Message}"); }
        }

        try { _tray.Text = BuildTooltip(s); }
        catch { /* NotifyIcon.Text is capped at 127 chars on some builds */ }
    }

    /// <summary>"72% · 3h 50m left" / "72% · charging · 1h 12m to full" / "100% · plugged in".</summary>
    private string BuildTooltip(BatterySample s)
    {
        int pct = (int)Math.Round(_battery.DisplayPercent(s));
        var remaining = _battery.TimeRemaining();
        double rate = _battery.SmoothedRateMw();

        string text;
        if (s.Charging && pct < 100)
        {
            text = remaining is not null && rate > 0
                ? $"{pct}% · charging · {Fmt.Duration(remaining.Value)} to full"
                : $"{pct}% · charging";
        }
        else if (s.AcOnline)
        {
            text = pct >= 99
                ? (_battery.CeilingActive ? $"{pct}% · charged to limit" : $"{pct}% · fully charged")
                : $"{pct}% · plugged in";
        }
        else
        {
            text = remaining is not null
                ? $"{pct}% · {Fmt.Duration(remaining.Value)} left"
                : $"{pct}% · on battery";
        }

        return text.Length > 127 ? text[..127] : text;
    }

    // ------------------------------------------------------------------- tray

    private void OnTrayClick(object? sender, WinForms.MouseEventArgs e)
    {
        try
        {
            if (e.Button == WinForms.MouseButtons.Right) ToggleMenu();
            else if (e.Button == WinForms.MouseButtons.Left) ToggleFlyout();
        }
        catch (Exception ex) { Log.Warn($"tray click: {ex}"); }
    }

    private void ToggleFlyout()
    {
        _menu?.Dismiss();

        if (_flyout is { IsVisible: true }) { _flyout.HideFlyout(); return; }
        if (_flyout is not null && (DateTime.UtcNow - _flyout.HiddenAt).TotalMilliseconds < 250) return;

        EnsureFlyout();
        _flyout!.ShowFlyout();
        _flyout.Tick();
    }

    private void ToggleMenu()
    {
        if (_flyout is { IsVisible: true }) _flyout.HideFlyout();

        if (_menu is { IsVisible: true }) { _menu.Dismiss(); return; }
        if (_menu is not null && (DateTime.UtcNow - _menu.HiddenAt).TotalMilliseconds < 250) return;

        // Rebuilt every time so the checkmarks reflect current state.
        DestroyMenu();
        _menu = new TrayMenuWindow(_cfg, _pal, new TrayMenuActions
        {
            OpenSettings = OpenSettings,
            ChargeToFullOnce = ToggleChargeToFull,
            ChargeToFullAvailable = () => AsusService.Available
                                          && AsusService.FirmwareImplementsLimit != false
                                          && _cfg.ChargeLimit.Enabled,
            ChargeToFullActive = () => AsusService.BypassActive,
            OpenWindowsPowerSettings = ShellIntegration.OpenWindowsPowerSettings,
            OpenControlPanelPowerOptions = ShellIntegration.OpenControlPanelPowerOptions,
            OpenConfigFolder = ShellIntegration.OpenConfigFolder,
            OpenNotificationAreaIcons = ShellIntegration.OpenNotificationAreaIcons,
            ToggleRunAtLogin = () => ShellIntegration.SetRunAtLogin(!ShellIntegration.RunAtLoginEnabled()),
            Exit = ExitApp,
            RunAtLoginEnabled = ShellIntegration.RunAtLoginEnabled,
        });
        _menu.ShowMenu();
    }

    private void EnsureFlyout()
    {
        if (_flyout is not null) return;
        _flyout = new FlyoutWindow(_cfg, _pal, _battery, new FlyoutActions
        {
            ResetSession = () => { _battery.ResetSession(); _flyout?.Tick(); },
            SaveConfig = SaveConfigOnly,
        });
    }

    private void DestroyFlyout()
    {
        if (_flyout is null) return;
        try { _flyout.Close(); } catch { /* already gone */ }
        _flyout = null;
    }

    private void DestroyMenu()
    {
        if (_menu is null) return;
        try { _menu.Close(); } catch { /* already gone */ }
        _menu = null;
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Tray-menu entry: start a one-time charge to full, or stop one in progress.</summary>
    private void ToggleChargeToFull()
    {
        try
        {
            if (AsusService.BypassActive) AsusService.CancelChargeToFull();
            else AsusService.BeginChargeToFull(_cfg.ChargeLimit.Percent);
        }
        catch (Exception ex) { Log.Warn($"charge to full: {ex.Message}"); }
    }

    private void OpenSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }
        DestroySettings();
        var window = new SettingsWindow(_cfg, _pal, SaveAndApply, ResetToDefaults,
            ShellIntegration.OpenConfigFolder,
            ShellIntegration.RunAtLoginEnabled,
            () => ShellIntegration.SetRunAtLogin(!ShellIntegration.RunAtLoginEnabled()));
        // Only forget the window if it is still the current one; an older instance closing
        // late must not orphan its replacement.
        window.Closed += (_, _) => { if (ReferenceEquals(_settings, window)) _settings = null; };
        _settings = window;
        window.ShowCentred();
    }

    private void DestroySettings()
    {
        if (_settings is null) return;
        try { _settings.Close(); } catch { /* already gone */ }
        _settings = null;
    }

    private void ResetToDefaults()
    {
        // The open Settings window edits the old AppConfig instance. It has to go before the
        // config is replaced, or OpenSettings below just re-activates it and every further
        // change lands on an object nothing reads any more.
        DestroySettings();
        _cfg = new AppConfig();
        SaveAndApply();
        OpenSettings();
    }

    /// <summary>Persist the live config and push it through the whole app right away.</summary>
    private void SaveAndApply()
    {
        SaveConfigOnly();
        ApplyConfig();
    }

    /// <summary>
    /// Write config.json without rebuilding anything. Used by the flyout, which changes
    /// only non-visual values (the power-mode choices) and must not tear itself down
    /// from inside its own click handler.
    /// </summary>
    private void SaveConfigOnly()
    {
        _configWatcher.Suppress();
        ConfigStore.Save(_cfg);
    }

    private void ReloadConfigFromDisk()
    {
        var loaded = ConfigStore.TryLoad();
        if (loaded is null)
        {
            // Half-saved or invalid JSON. Keep running on what we have; the next good save
            // of the file triggers another reload.
            Log.Warn("config.json could not be parsed; keeping the current settings");
            return;
        }

        _cfg = loaded;
        ApplyConfig();

        // The Settings window mutates the AppConfig it was built with. After a reload that is
        // no longer _cfg, so without a rebuild its edits would be saved nowhere.
        if (_settings is { IsVisible: true })
        {
            DestroySettings();
            OpenSettings();
        }
        Log.Info("config reloaded from disk");
    }

    private void ApplyConfig()
    {
        _battery.UpdateConfig(_cfg);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_cfg.General.PollIntervalMs, 250, 30000));
        RebuildVisuals();
    }

    private string AppearanceSignature()
    {
        var a = _cfg.Appearance;
        // Deliberately narrow. Colours are excluded on purpose: rebuilding the Settings
        // window mid-drag would tear down the colour picker the user is dragging.
        _ = a;
        return $"{_pal.Accent}|{_pal.TaskbarDark}";
    }

    private void RebuildVisuals()
    {
        _pal = Palette.Build(_cfg, _themeWatcher.Current);

        bool flyoutWasVisible = _flyout is { IsVisible: true };
        DestroyFlyout();
        DestroyMenu();

        _iconSignature = "";
        if (_battery.Latest is { } s) UpdateTrayIcon(s);

        if (flyoutWasVisible)
        {
            EnsureFlyout();
            _flyout!.ShowFlyout();
            _flyout.Tick();
        }

        // The Settings window is built against a palette snapshot, so it only needs
        // rebuilding when something it is actually drawn from changed.
        string sig = AppearanceSignature();
        if (sig != _appearanceSignature)
        {
            _appearanceSignature = sig;
            if (_settings is { IsVisible: true })
            {
                DestroySettings();
                OpenSettings();
            }
        }
    }


    private void ExitApp()
    {
        try
        {
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _currentIcon?.Dispose();
            _configWatcher.Dispose();
            _powerWatcher?.Dispose();
            _themeWatcher.Dispose();
            _battery.Dispose();
            DestroyFlyout();
            DestroyMenu();
            DestroySettings();
        }
        catch (Exception ex) { Log.Warn($"shutdown: {ex.Message}"); }
        Shutdown();
    }
}
