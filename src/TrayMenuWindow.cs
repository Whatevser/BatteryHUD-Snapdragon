using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatteryHud;

public sealed class TrayMenuActions
{
    public required Action OpenSettings { get; init; }
    public required Action ChargeToFullOnce { get; init; }
    public required Func<bool> ChargeToFullAvailable { get; init; }
    public required Func<bool> ChargeToFullActive { get; init; }
    public required Action OpenWindowsPowerSettings { get; init; }
    public required Action OpenControlPanelPowerOptions { get; init; }
    public required Action OpenConfigFolder { get; init; }
    public required Action OpenNotificationAreaIcons { get; init; }
    public required Action ToggleRunAtLogin { get; init; }
    public required Action Exit { get; init; }
    public required Func<bool> RunAtLoginEnabled { get; init; }
}

/// <summary>Right-click menu. Drawn with the same widgets as the flyout so it matches.</summary>
public sealed class TrayMenuWindow : HudWindow
{
    private readonly TrayMenuActions _actions;
    private readonly FontFamily _font;

    public DateTime HiddenAt { get; private set; } = DateTime.MinValue;

    public TrayMenuWindow(AppConfig cfg, Palette pal, TrayMenuActions actions)
        : base(cfg, pal, toolWindow: true)
    {
        _actions = actions;
        _font = Ui.ResolveFont(cfg.Appearance.FontFamily);

        SizeToContent = SizeToContent.Height;
        Width = 268;
        Content = Build();

        Deactivated += (_, _) => Dismiss();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Dismiss(); };
    }

    private UIElement Build()
    {
        double fs = Cfg.Appearance.FontSize;

        var root = new Border
        {
            CornerRadius = new CornerRadius(Cfg.Appearance.CornerRadius),
            Padding = new Thickness(6),
            Background = GlassBrush,
        };
        RootBorder = root;

        var stack = new StackPanel();
        root.Child = stack;

        stack.Children.Add(new MenuRow("", "Settings", Pal, _font, fs, () => Run(_actions.OpenSettings)));

        if (_actions.ChargeToFullAvailable())
        {
            stack.Children.Add(Ui.Separator(Pal.SeparatorBrush, 4));
            bool active = _actions.ChargeToFullActive();
            stack.Children.Add(new MenuRow(active ? "✓" : "",
                active ? "Charging to full (click to stop)" : "Charge to full once",
                Pal, _font, fs, () => Run(_actions.ChargeToFullOnce)));
        }

        stack.Children.Add(Ui.Separator(Pal.SeparatorBrush, 4));

        // Named for where they land rather than for the page title, which is the thing
        // you actually need to know when picking one.
        stack.Children.Add(new MenuRow("", "Windows Settings", Pal, _font, fs,
            () => Run(_actions.OpenWindowsPowerSettings), "↗"));
        stack.Children.Add(new MenuRow("", "Control Panel", Pal, _font, fs,
            () => Run(_actions.OpenControlPanelPowerOptions), "↗"));

        stack.Children.Add(Ui.Separator(Pal.SeparatorBrush, 4));

        stack.Children.Add(new MenuRow("", "System tray icons", Pal, _font, fs,
            () => Run(_actions.OpenNotificationAreaIcons), "↗"));
        stack.Children.Add(new MenuRow(_actions.RunAtLoginEnabled() ? "✓" : "", "Start with Windows",
            Pal, _font, fs, () => Run(_actions.ToggleRunAtLogin)));
        stack.Children.Add(new MenuRow("", "Open config folder", Pal, _font, fs,
            () => Run(_actions.OpenConfigFolder), "↗"));

        stack.Children.Add(Ui.Separator(Pal.SeparatorBrush, 4));

        stack.Children.Add(new MenuRow("", "Exit BatteryHud", Pal, _font, fs, () => Run(_actions.Exit)));

        return root;
    }

    private void Run(Action action)
    {
        Dismiss();
        try { action(); }
        catch (Exception ex) { Log.Warn($"menu action: {ex}"); }
    }

    public void ShowMenu()
    {
        Left = -20000;
        Top = -20000;
        Show();
        Activate();
        Dispatcher.BeginInvoke(new Action(() => PositionNearCursor(centreOnCursor: false)), DispatcherPriority.Loaded);
    }

    public void Dismiss()
    {
        if (!IsVisible) return;
        HiddenAt = DateTime.UtcNow;
        Hide();
    }
}
