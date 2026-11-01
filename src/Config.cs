using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace BatteryHud;

// ---------------------------------------------------------------------------
//  config.json drives everything visual and most behaviour. It is edited either
//  through the Settings window or by hand; both paths apply immediately.
// ---------------------------------------------------------------------------

public sealed class AppConfig
{
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public TraySettings Tray { get; set; } = new();
    public SectionSettings Sections { get; set; } = new();
    public ChargeLimitSettings ChargeLimit { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, ConfigStore.Options);
        return JsonSerializer.Deserialize<AppConfig>(json, ConfigStore.Options) ?? new AppConfig();
    }
}

public sealed class GeneralSettings
{
    public int PollIntervalMs { get; set; } = 1000;
    public int HistorySamples { get; set; } = 900;
    public int RateSmoothingSamples { get; set; } = 6;
    public int DerivedRateWindowSeconds { get; set; } = 45;
    public bool UseWatts { get; set; } = true;
    public int PowerDecimals { get; set; } = 1;
    public bool CloseOnFocusLost { get; set; } = true;
}

public sealed class AppearanceSettings
{
    public bool FollowSystemAccent { get; set; } = true;
    public string AccentColor { get; set; } = "#FF4CC2FF";

    /// <summary>Tint colour of the glass. Alpha is ignored - use Transparency instead.</summary>
    public string GlassColor { get; set; } = "#FF1B1B1B";

    /// <summary>0 = solid tint, 100 = tint invisible (pure blur).</summary>
    public double Transparency { get; set; } = 45;

    /// <summary>Extra frost drawn over the blur. 0 = none, 100 = milky.</summary>
    public double GlassEffect { get; set; } = 25;

    public string Foreground { get; set; } = "#FFFFFFFF";
    public string SubtleForeground { get; set; } = "#99FFFFFF";
    public string Separator { get; set; } = "#22FFFFFF";
    public string ControlFill { get; set; } = "#18FFFFFF";
    public string ControlFillHover { get; set; } = "#28FFFFFF";

    public string ChargingColor { get; set; } = "#FF4ADE80";
    /// <summary>Blank means "use the accent colour", which is the default.</summary>
    public string DischargingColor { get; set; } = "";
    public string LowColor { get; set; } = "#FFFFB020";
    public string CriticalColor { get; set; } = "#FFFF4D4F";

    public string FontFamily { get; set; } = "Segoe UI Variable Text, Segoe UI";
    public double FontSize { get; set; } = 12.5;
    public double HeadlineFontSize { get; set; } = 30;

    public double CornerRadius { get; set; } = 10;
    public double Width { get; set; } = 330;
    public double Padding { get; set; } = 14;
    public double RowSpacing { get; set; } = 8;
    public int EdgeMargin { get; set; } = 12;
    public string BorderColor { get; set; } = "none";

    public double SparklineHeight { get; set; } = 42;
}

public sealed class TraySettings
{
    /// <summary>battery | batteryvertical</summary>
    public string IconStyle { get; set; } = "battery";

    /// <summary>Tint the icon by charge state instead of using ForegroundColor.</summary>
    public bool ColorByState { get; set; } = true;

    /// <summary>"auto" follows the taskbar theme. Used when ColorByState is false.</summary>
    public string ForegroundColor { get; set; } = "auto";

    public int LowThreshold { get; set; } = 25;
    public int CriticalThreshold { get; set; } = 10;

    public bool ShowChargingGlyph { get; set; } = true;

    /// <summary>plug | bolt</summary>
    public string ChargingGlyph { get; set; } = "plug";

    /// <summary>Outline thickness multiplier for the battery shell.</summary>
    public double OutlineScale { get; set; } = 1.0;
}

public sealed class SectionSettings
{
    /// <summary>Percentage, state, time remaining, level bar, watts, voltage.</summary>
    public bool Battery { get; set; } = true;

    /// <summary>The power history graph, independent of the numbers above it.</summary>
    public bool Graph { get; set; } = true;

    public bool BatteryHealth { get; set; } = false;
    public bool Brightness { get; set; } = true;

    /// <summary>Night light switch, shown alongside the brightness slider.</summary>
    public bool NightLight { get; set; } = true;
    public bool RefreshRate { get; set; } = true;
    public bool PowerMode { get; set; } = true;
    public bool ChargerSession { get; set; } = true;
    public bool ExternalMonitorBrightness { get; set; } = false;

    /// <summary>Refresh rates to offer. Empty = everything the driver reports.</summary>
    public int[] PreferredRefreshRates { get; set; } = Array.Empty<int>();

    public int BrightnessStep { get; set; } = 5;
}

/// <summary>
/// Rescales the displayed charge when an OEM utility caps charging below 100%.
///
/// Expressed in percent, deliberately. OEM charge caps are configured in percent, and the
/// battery's FullChargedCapacity falls as the pack wears - so a threshold stored in Wh
/// would slowly drift away from whatever the OEM tool is actually enforcing, while a
/// percentage tracks it. Voltage would be worse still: the charge curve is nonlinear and
/// moves with load and temperature.
/// </summary>
/// <summary>
/// The charge limit. One switch, two effects, because wanting one without the other has
/// never come up:
///
///   - tells the ASUS firmware to stop charging at Percent;
///   - shows that as 100%, so the battery does not look permanently part-charged.
///
/// The second half works even when the first fails, which matters: if MyASUS already owns
/// the cap, or the firmware refuses our write, the display is still right.
/// </summary>
public sealed class ChargeLimitSettings
{
    /// <summary>Ask the ASUS firmware to stop charging at Percent. Off by default.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Show Percent as 100%. Independent of Enabled on purpose: the cap is often set by
    /// MyASUS rather than by us, and whether you want the display rescaled is a separate
    /// question from who set the cap.
    /// </summary>
    public bool ShowAsFull { get; set; } = false;

    /// <summary>
    /// Where charging stops, and what reads as 100%. 80 is the only value the UI offers -
    /// it is the one ASUS themselves recommend and the one their firmware is known to
    /// honour. Editable here for anyone who wants 60, which ASUS also offers.
    /// </summary>
    public int Percent { get; set; } = 80;

    /// <summary>Adds "78% actual" to the detail line, so nothing is hidden.</summary>
    public bool ShowActual { get; set; } = true;

    /// <summary>
    /// Re-push the limit on power-source changes and at startup. The BIOS drops it on
    /// every restart and the ASUS service can re-assert its own value, so this is what
    /// makes the setting stick.
    /// </summary>
    public bool ReapplyOnPowerChange { get; set; } = true;
}

public sealed class LoggingSettings
{
    public bool CsvEnabled { get; set; } = false;
    public string CsvPath { get; set; } = "";
    public bool CsvOnlyWhileCharging { get; set; } = true;
}

// ---------------------------------------------------------------------------

public static class ConfigStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory
    {
        get
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "portable.txt");
            if (File.Exists(portable)) return AppContext.BaseDirectory;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BatteryHud");
        }
    }

    public static string FilePath => Path.Combine(Directory, "config.json");

    /// <summary>Startup load: never fails, falls back to defaults.</summary>
    public static AppConfig Load() => TryLoad() ?? new AppConfig();

    /// <summary>
    /// Null when the file exists but cannot be read or parsed. The hot-reload path relies on
    /// that: a hand edit that is half-saved or has a typo must not swap the live config for
    /// defaults, because the next Settings change would then write those defaults over the
    /// user's file.
    /// </summary>
    public static AppConfig? TryLoad()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!File.Exists(FilePath))
            {
                var fresh = new AppConfig();
                Save(fresh);
                return fresh;
            }
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
            return cfg is null ? null : Normalize(cfg);
        }
        catch (Exception ex)
        {
            Log.Warn($"config load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// JSON can say "Appearance": null or "PreferredRefreshRates": null. The rest of the app
    /// assumes every section exists, so put the defaults back rather than crash later.
    /// </summary>
    private static AppConfig Normalize(AppConfig c)
    {
        c.General ??= new();
        c.Appearance ??= new();
        c.Tray ??= new();
        c.Sections ??= new();
        c.ChargeLimit ??= new();
        c.Logging ??= new();
        c.Sections.PreferredRefreshRates ??= Array.Empty<int>();
        return c;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"config save failed: {ex.Message}");
        }
    }

    public static Color ParseColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            if (ColorConverter.ConvertFromString(value.Trim()) is Color c) return c;
        }
        catch { /* fall through */ }
        return fallback;
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>Watches config.json so hand edits apply immediately, with a suppression
/// window used when the Settings window itself writes the file.</summary>
public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly System.Timers.Timer _debounce = new(350) { AutoReset = false };
    private DateTime _suppressUntil = DateTime.MinValue;

    public event Action? Changed;

    public ConfigWatcher()
    {
        System.IO.Directory.CreateDirectory(ConfigStore.Directory);
        _watcher = new FileSystemWatcher(ConfigStore.Directory, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Kick();
        _watcher.Created += (_, _) => Kick();
        _watcher.Renamed += (_, _) => Kick();
        _debounce.Elapsed += (_, _) =>
        {
            if (DateTime.UtcNow < _suppressUntil) return;
            Changed?.Invoke();
        };
    }

    /// <summary>Ignore file events for a moment (we just wrote the file ourselves).</summary>
    public void Suppress(int milliseconds = 1200)
        => _suppressUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);

    private void Kick()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(ConfigStore.Directory, "batteryhud.log");

    public static void Warn(string message) => Write("WARN", message);
    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(ConfigStore.Directory);
                var f = new FileInfo(FilePath);
                if (f.Exists && f.Length > 512 * 1024) f.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
