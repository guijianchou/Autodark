# AutoDark

AutoDark is a standalone WinUI 3 desktop app that ports the PowerToys Light Switch behavior for local, unpackaged use.

## Current Version

2.0.0

## Features

- Switch Windows and app theme through `AppsUseLightTheme` and `SystemUsesLightTheme`.
- Schedule by fixed hours, sunset to sunrise, or Windows Night Light.
- Preserve Light Switch manual override behavior until the next schedule boundary.
- React immediately to system resume and clock or time-zone changes instead of waiting for the next minute tick.
- Handle polar day and night above the polar circles by holding the matching theme for the whole day.
- Use a PowerToys-style settings layout with compact location summaries, adaptive schedule rows, a minimum usable window width, and a light-range timeline.
- Show sun and moon markers on the schedule timeline to distinguish light and dark ranges.
- Edit sunset/sunrise coordinates through a location dialog with manual latitude/longitude fields and a current-location sync action.
- Choose whether the System and Apps themes follow the schedule.
- Toggle from the app or tray menu, and force light or dark from the tray menu.
- Start with Windows through the current user's Run key, detecting stale entries after the app folder moves.
- Minimize to the notification area, keep scheduling active, and re-add the tray icon after an Explorer restart.
- Store settings in `settings.json` beside `AutoDark.exe` (with a `.bak` fallback), migrating any legacy `%APPDATA%\AutoDark` settings on first launch.
- Restore the main window position and size across restarts.

## Release Notes

### 2.0.0

- Remove the legacy global hotkey path; theme switching now happens through the app surface and tray menu.
- Move schedule/apply coordination into a dedicated runtime coordinator so timer, settings, Night Light, tray, and manual theme events share one serialized state path.
- Queue settings writes off the UI path while keeping an explicit exit flush for restart durability.
- Structure startup health warnings by issue so native hook and tray failures remain visible without string-concatenation drift.
- Guard all location dialog and sync entry points through the same UI task runner.
- Bump app, assembly, file, package manifest, and application manifest versions to `2.0.0`.

### 1.7.0

- Migrate legacy `%APPDATA%\AutoDark` settings to the executable directory instead of deleting them, mapping the removed `Enabled: false` kill switch to the `Off` schedule mode; partial or hand-edited settings files load with defaults instead of crashing.
- Fix the sunset-to-sunrise timeline inverting the daylight band when offsets push a boundary past midnight, and report polar day/night at high latitudes instead of pinning the theme to light.
- Re-evaluate the schedule immediately on resume from sleep and on clock or time-zone changes; let a boundary that lands on the same minute as an external theme change win so both managed targets converge like the original Light Switch module.
- Harden startup, exit, tray, second-instance, and resume paths against transient failures (no more zombie processes, lost tray callbacks, or stuck location sync).
- Read the About version from the assembly, keep the tray icon resilient to Explorer restarts, and smooth interactive resize by removing per-frame reposition animations.

### 1.6.0

- Move runtime settings and crash logs beside `AutoDark.exe` and delete the legacy `%APPDATA%\AutoDark` settings directory on startup.
- Persist schedule mode, icon choice, and main window bounds synchronously so immediate restarts and machine reboots restore the selected app state.
- Bump app, assembly, file, package manifest, and application manifest versions to `1.6.0`.

### 1.5.1

- Start silently in the tray when launched through Auto start.

## Build

```powershell
dotnet build src\AutoDark.slnx -c Debug
dotnet build src\AutoDark\AutoDark.csproj -c Debug -p:Platform=x64
```

## Run

```powershell
# default (AnyCPU) build output
src\AutoDark\bin\Debug\net10.0-windows10.0.19041.0\AutoDark.exe

# platform-specific build output (-p:Platform=x64)
src\AutoDark\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\AutoDark.exe
```

The app is intentionally unpackaged for local use. MSIX publishing is not part of the current workflow.

## Test

```powershell
dotnet test src\AutoDark.slnx -c Debug
```

## Source Notes

PowerToys Light Switch source is used as the behavioral reference. PowerToys runner integration, GPO checks, telemetry, PowerDisplay profile switching, and MSIX release packaging are not included.
