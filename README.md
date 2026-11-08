# BatteryHud

A native **Windows on ARM64** battery tray app: a custom notification-area icon that opens a
themed, translucent flyout showing charge state, real charge/discharge wattage, and inline
controls for brightness, night light, refresh rate and Windows power mode.

Built specifically because nothing existing does this on ARM64 without x64 emulation.

---

## Why it runs natively (zero emulation)

| Layer | Choice | ARM64 story |
|---|---|---|
| Runtime | .NET 10 LTS (`net10.0-windows`) | Publishes to `win-arm64`; code JITs straight to AArch64. No WOW64, no ARM64EC, no x64 shim. |
| UI | WPF + WinForms `NotifyIcon` | Both ship as native ARM64 assemblies in the Windows Desktop runtime. |
| Battery data | Battery class driver IOCTLs | Pure P/Invoke into `kernel32`/`setupapi`. Architecture-neutral. |
| Power control | `powrprof.dll` | Same exports on ARM64 as x64. |
| Brightness | WMI `WmiMonitorBrightnessMethods` | The same path the OS brightness slider uses. |
| Refresh rate | `ChangeDisplaySettingsEx` | Classic GDI API, fully supported. |

There are **no** native dependencies outside Windows itself and no NuGet packages other than
`System.Management` (Microsoft-published, ships arm64).

To confirm after building: `Get-Item .\BatteryHud.exe | Format-List` then check with
`dumpbin /headers BatteryHud.exe | findstr machine` → should say `AA64`. Or just open Task
Manager → Details → add the *Architecture* column; it must read **ARM64**, not "x64 (emulated)".

---

## Build

### Prerequisites

Install the **ARM64** .NET 10 SDK (the x64 one works too, but then you are building through
emulation):

```powershell
winget install Microsoft.DotNet.SDK.10
dotnet --info        # should show "Architecture: arm64" and an SDK version 10.0.x
```

To *run* a framework-dependent build on another machine, that machine only needs the
**.NET 10 Desktop Runtime (arm64)**:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

### With the build script

```powershell
# framework-dependent single-file exe, ~0.7 MB, needs the .NET 10 Desktop Runtime (arm64)
.\build.ps1

# fully standalone single-file exe, ~165 MB, nothing else to install
.\build.ps1 -SelfContained

# x64 build, only for testing on an x64 box
.\build.ps1 -Rid win-x64
```

If script execution is blocked, run it as
`powershell -ExecutionPolicy Bypass -File .\build.ps1`.

### With plain `dotnet` (no script)

```powershell
dotnet publish src\BatteryHud.csproj -c Release -r win-arm64 --self-contained false `
  -p:PublishSingleFile=true -o publish\win-arm64
```

For a quick debug build without publishing: `dotnet build src\BatteryHud.csproj`, then run
`src\bin\Debug\net10.0-windows\BatteryHud.exe`.

Output lands in `publish\win-arm64\`. Run `BatteryHud.exe`. The `.pdb` next to it only adds
line numbers to the log and can be deleted.

Drop an empty `portable.txt` next to the exe to keep `config.json`, `samples.csv` and the log
beside the binary instead of in `%LOCALAPPDATA%\BatteryHud`.

### Why the TFM has no Windows version suffix

The project targets plain `net10.0-windows`, not `net10.0-windows10.0.19041.0`. A versioned
TFM pulls in the C#/WinRT projections (`Microsoft.Windows.SDK.NET.dll`, ~25 MB) and the
single-file publish bundles them. BatteryHud uses no WinRT at all — only Win32 P/Invoke, WMI
and the registry — so on .NET 8 that made the "small" exe about 26 MB. It is ~0.7 MB now.

The project also builds on Linux or macOS (`EnableWindowsTargeting` is set), which is handy
for CI. It only *runs* on Windows.

---

## Using it

- **Left-click** the tray icon → the flyout: charge, watts, graph, brightness, refresh rate,
  power mode. `Esc` or clicking away closes it.
- **Right-click** the tray icon → menu: Settings, *Windows Settings*, *Control Panel*,
  *System tray icons*, start with Windows, open the config folder, exit.
- **Settings** is a real window with toggles and sliders. Every change applies instantly —
  no OK button, no restart. Editing `config.json` by hand applies instantly too, and an open
  Settings window is rebuilt to show the edited values. If the file is half-saved or has a
  syntax error, the reload is skipped and the current settings stay in force. Your file is
  never replaced with defaults.

### Power mode

One control: Efficiency / Balanced / Performance, applying to whichever power source the
machine is on right now.

Windows keeps a separate mode for mains and battery and swaps them itself when the plug
moves, so the flyout reads the mode in force each tick and mirrors it: plugging in shows your
mains mode, unplugging shows your battery one, with no bookkeeping on our side.

Worth knowing about the underlying API, since it constrains what any app can do here:

- These are *overlay* schemes, not classic power plans. Windows stores them per source in
  `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes`
  (`ActiveOverlayAcPowerScheme` / `ActiveOverlayDcPowerScheme`). Readable by any user,
  **not writable** — the key is protected even from an elevated process.
- `PowerSetActiveOverlayScheme` only ever targets the source you are on. There is no public
  per-source setter, which is why a single control is the honest design.
- Overlays only apply on top of the **Balanced** base plan. Switch to High performance or
  Power saver in Control Panel and Windows hides its own selector; BatteryHud greys the
  section out and names the plan that is in the way.

`PowerSourceWatcher` registers for `GUID_ACDC_POWER_SOURCE` so the flyout reacts to the plug
moving from a `WM_POWERBROADCAST` handler rather than on the next poll.

## Night light

A switch next to the brightness slider. No public API exists, so the state is read and written
directly: a REG_BINARY blob under `CloudStore`, Microsoft Bond CompactBinary v1 inside a
CloudStore envelope.

The blob is **nested** — an outer envelope containing a second Bond blob, and both start with
the same `43 42 01 00` marker. Captured on Windows 11 25H2 ARM64 (build 26200):

```
OFF (41 bytes)
  43420100 0a0201002a06 f697bad306 2a2b0e 13 43420100      d00a02c614 <ts> 000000
ON  (43 bytes)
  43420100 0a0201002a06 f997bad306 2a2b0e 15 43420100 1000 d00a02c614 <ts> 000000
  |outer                |ts varint         |len|inner    |ON flag
```

Three things move together:

1. the `10 00` pair after the **inner** marker, present only when on;
2. the length prefix in the byte before that marker, `0x13` → `0x15`;
3. the outer timestamp varint, which must increase or Windows ignores the write.

Two traps worth recording, both of which this code hit:

- **Match the inner marker, not the outer one.** Searching from offset 0 finds the envelope
  header, whose following bytes are `0a 02 01 00` — never the flag. State then always reads
  as "off", and enabling splices the flag into the envelope.
- **The timestamp is a varint, not a number.** `0xF6` is a continuation byte carrying the
  value `0x76`; incrementing the byte as an integer walks past `0x7F`, changes the encoding
  length, and corrupts everything after it. Only the low 7 bits may be touched.

Everything after the flag is copied across untouched, so colour temperature and schedule
survive a toggle — unlike the fixed-hex-blob recipes, which flatten both.

If night light has never been switched on there is no blob. BatteryHud does not fabricate one
and shows a *Set up* button opening `ms-settings:nightlight` instead. The switch re-reads state
each tick, so it stays honest when the schedule fires or you toggle from Action Center.

`diagnose-nightlight.ps1` dumps the blob before and after a manual toggle and diffs them. It is
read-only, and it is how the layout above was established.

## Hiding the built-in battery icon

Do this from Windows. The tray menu has *System tray icons*, which opens the classic page at
`shell:::{05d7b0f4-2121-4eff-bf6b-ed3f69b894d9}` — Windows 11 removed every link to it but the
shell folder is still registered. Turn Power off there and it stays off.

## Battery percentage and charging

BatteryHud always displays the **actual battery percentage** reported by Windows.
It does not set charging limits, override them, or rescale an OEM-capped 80% into 100%.
Use **MyASUS** to configure the charge limit and any temporary charge-to-full bypass.

Old `ChargeLimit` settings are removed from `config.json` when the new version first
loads it. If you previously activated BatteryHud's bypass, check your desired cap in
MyASUS when upgrading: BatteryHud deliberately makes **no** ASUS firmware or registry
changes, including attempts to restore an old bypass.

### Administrator permissions

BatteryHud's remaining features run without requiring administrator elevation.
Autostart uses `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` for the current
user. Existing elevated `BatteryHud` logon tasks from old versions are migrated to
ordinary autostart automatically when launched elevated once; if the app is running
without administrator rights, remove the legacy task from Task Scheduler manually.

## Appearance

The backdrop is the Win10-era acrylic from `SetWindowCompositionAttribute`, which takes a
single ARGB tint. Three controls drive it:

- **Glass colour** — the tint blended over the blurred wallpaper. Full HSV picker.
- **Transparency** — the alpha of that tint. 0 is a solid colour, 100 is pure blur with no tint.
- **Blur behind the glass** — on by default. Windows fixes the acrylic blur strength, so with
  blur on, *Transparency* only trades tint for blur, and that looks like a small colour
  change rather than "more transparent". Turn blur off for genuinely see-through glass;
  *Transparency* then controls exactly how see-through it is.
- **Glass effect** — a separate white wash WPF paints *on top of* the blur. This is the only
  one of the three that is not the compositor: there is no blur-radius knob in the accent API,
  so "frostier" is achieved by adding a veil.

If the compositor refuses to blur, the window falls back to an opaque `GlassColor` rather
than rendering transparent-black.

The app is **dark only**. The tray icon follows the *taskbar* theme, which is a separate
Windows setting, so it stays legible on a light taskbar.

**Accent colour** has the same picker and drives sliders, selected segments and the battery
level bar. Picking one clears *Follow Windows accent colour* automatically. The level bar
follows it because `DischargingColor` defaults to blank, which means "use the accent"; set it
explicitly if you want the bar to differ from everything else.

**Live state.** Brightness, night light and power mode all track changes made elsewhere.
Brightness comes from the `WmiMonitorBrightnessEvent` WMI event rather than polling, so the
slider follows the laptop's F-keys and adaptive brightness as they happen; night light and
power mode are re-read each tick and again whenever the flyout opens.

**Charge info and the graph are separate toggles** under *What the flyout shows* — you can
have the numbers without the sparkline, or the sparkline on its own.

## The ARM64 power-mode trap (worth reading if you touch powrprof)

`PowerSetActiveOverlayScheme` takes its GUID **by pointer**. Every published C# sample
declares it by value:

```csharp
// WRONG on ARM64 - crashes or fails depending on the GUID
[DllImport("powrprof.dll")]
static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
```

Those samples work because they are all x64, where the CLR passes a 16-byte struct by hidden
reference — so by-value and by-pointer compile to the same call and nobody ever finds out
which the export really wants. On ARM64 a 16-byte struct is passed in `x0`/`x1`, so the
by-value declaration puts the GUID's first 8 bytes where the function expects an address.

Measured on Windows 11 25H2 ARM64 (build 26200.8894), Snapdragon:

| GUID | First 8 bytes land in x0 as | Result |
|---|---|---|
| Balanced `00000000-…` | `0` | function sees a null pointer, returns **87** |
| Efficiency `961cc777-…` | `0x4F9D2547961CC777` | wild pointer, dereferenced → **0xC0000005**, process dies silently |

The correct declaration:

```csharp
[DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
static extern uint PowerSetActiveOverlayScheme(ref Guid overlaySchemeGuid);
```

`PowerGetEffectiveOverlayScheme` / `PowerGetActualOverlayScheme` were never affected: their
`out Guid` parameter is a pointer under both ABIs.

Run `diagnose-power.ps1` to confirm on your own machine. It only ever calls by reference.

### Overlays present on a Snapdragon 25H2 image

For reference, `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes` on that
device contains four overlays, not three:

```
961cc777-2547-4f9d-8174-7d86181b8a7a   Better Battery-life Overlay
3af9b8d9-7c97-431d-ad78-34a8bfea439f   High Performance Overlay
ded574b5-45a0-4f42-8737-46345c09c238   Max Performance Overlay
(the empty GUID means "no overlay" and has no subkey)
```

Settings only surfaces three, and `3af9b8d9` is the leftover middle notch from the old
Windows 10 four-position slider. Adding it as a fourth segment would be a small change in
`PowerModeService.Order`.

## Notes and caveats

- **Charge rate on ARM64.** Snapdragon-class laptops report rate through ACPI `_BST` like any
  other machine, but a few firmwares return 0 or "unknown". BatteryHud detects that and falls
  back to differentiating remaining capacity over `DerivedRateWindowSeconds` — slower to react,
  still accurate for charger comparison.
- **Power mode overlay** uses `PowerSetActiveOverlayScheme`, undocumented but stable since
  Windows 10 1709 and still how every third-party tool does this today. If a future build
  removes it, the section reports itself unavailable rather than failing.
- Every section builds inside a `try`, and the app installs a `DispatcherUnhandledException`
  handler, so a power API or display driver misbehaving degrades one row instead of killing
  the tray icon.
- **Refresh rate** changes are validated with `CDS_TEST` first and reverted in the UI if the
  driver rejects them. On panels with dynamic refresh (DRR) Windows may re-arbitrate the rate
  on its own afterwards.
- **Brightness** requires the panel to expose WMI brightness control. If not, the section says
  so rather than showing a dead slider. External monitors over DDC/CI are stubbed in
  `BrightnessService.EnumerateExternal` and gated behind
  `Sections.ExternalMonitorBrightness` — wire-up in the flyout is left as an easy first PR.
  Heads-up for whoever picks it up: the stub finds each monitor with `MonitorFromPoint` at
  the display's width/2, height/2 *without its desktop position*, so every display resolves to
  the primary. Carry `dmPosition` through `DisplayTarget` first.
- **CSV logging** always writes `.` as the decimal separator, whatever the Windows locale,
  so the file opens correctly in any spreadsheet.

---

## Source layout

```
src/
  Program.cs           entry point, tray icon, poll loop, config/theme reload
  Native.cs            every P/Invoke and native struct, in one place
  Config.cs            config model, JSON load/save, hot-reload watcher, logging
  Theme.cs             accent + taskbar-theme detection, Palette resolution
  BatteryService.cs    battery IOCTLs, sampling, rate smoothing, session stats, CSV
  PowerModeService.cs  Windows 11 power mode overlay, per power source
  DisplayService.cs    display enumeration and refresh-rate switching
  BrightnessService.cs WMI panel brightness + DDC/CI scaffolding
  TrayIconRenderer.cs  GDI+ battery icon with fill level and plug glyph
  Widgets.cs           hand-drawn WPF controls (sparkline, slider, segments, toggles)
  ColorPicker.cs       HSV square + hue strip + presets, all drawn in OnRender
  PowerSourceWatcher.cs  WM_POWERBROADCAST listener so the UI tracks the plug instantly
  NightLightService.cs   parses and rewrites the CloudStore night light blob
  HudWindow.cs         shared backdrop / rounded corners / cursor positioning
  FlyoutWindow.cs      the left-click panel
  TrayMenuWindow.cs    the right-click menu
  SettingsWindow.cs    the settings GUI
  ShellIntegration.cs  autostart and links to the Windows pages
```

Nothing is templated in XAML: every control draws itself in `OnRender`, so the whole look is
driven from `config.json` without resource dictionaries to fight.

### Good places to extend

- **More telemetry.** `BatteryService.Poll` already has voltage; add temperature via
  `BATTERY_QUERY_INFORMATION_LEVEL.BatteryTemperature`.
- **Battery saver toggle.** `SYSTEM_POWER_STATUS.SystemStatusFlag` already reports it; there
  is no public setter, but `powercfg` can be shelled out to.
- **Per-app power draw** would need `Windows.System.Power` / EnergyEstimationEngine — a real
  project, but the flyout has room for it.
- **Charger fingerprinting.** USB-PD contract details are exposed by some vendor WMI providers;
  logging the negotiated voltage alongside `rate_mw` would make charger comparison exact.
- **A second flyout page** for history/graphs. `Sparkline` already takes arbitrary series.
- **Hotkeys.** `RegisterHotKey` on the flyout's HWND; the message loop is already there.

---

## Changelog

### 0.3.4

- Removed ASUS charging-limit control, timed Charging Cap Bypass, and display-only 80%-as-100% rescaling.
- Battery percentages, charging estimates and session statistics use the true Windows readings.
- Dropped elevated startup-task creation and added migration to normal user autostart.
- Fixed charging-rate smoothing across AC/discharge transitions, and opened the battery
  interface read-only to avoid unnecessary privileges.
- Preserved the small, framework-dependent Windows ARM64 .NET 10 build.

### 0.3.2

- Added *Blur behind the glass*. With blur off, *Transparency* really makes the window
  see-through; with blur on it can only trade tint for Windows' fixed-strength blur.

### 0.3.1 (historical)

- Earlier ASUS charge-cap and elevated-autostart fixes, subsequently retired in 0.3.4.
- Improved Settings updates and live appearance changes.

### 0.3.0 — .NET 10

- Moved to **.NET 10 LTS** (`net10.0-windows`) and `System.Management` 10.0.14.
- The framework-dependent exe went from **~26 MB to ~0.7 MB**: the WinRT projections the old
  versioned TFM bundled are gone (see *Why the TFM has no Windows version suffix*).
- Fixed: after a hand edit of `config.json`, an open Settings window kept editing the old
  config, so its changes were silently dropped.
- Fixed: *Reset to defaults* closed the Settings window instead of reopening it on the new
  defaults.
- Fixed: a half-saved or invalid `config.json` reset everything to defaults on hot-reload,
  and the next change then wrote those defaults over your file. `null` sections in the JSON
  no longer crash the app either.
- Fixed: CSV decimals followed the Windows locale, which broke the columns on comma-decimal
  locales.
- Fixed: on a machine with no battery the device was re-enumerated and a warning logged
  every poll. Now it retries every 30 s and logs once.
- Fixed: clicking into a colour's hex box and away again counted as a colour change, which
  for the accent colour turned *Follow Windows accent colour* off.
- Fixed: a rejected refresh-rate change reverted to the rate active when the flyout opened,
  not to the current one.
- `build.ps1` checks for SDK 10+, no longer reassigns PowerShell's `$args`, and no longer
  embeds the PDB in the exe.

---

## Licence

MIT. Do whatever you like with it.
