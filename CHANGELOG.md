# Changelog

## Unreleased

## 2.0.0 - 2026-07-04

### Reliability
- Remove the legacy global hotkey/background shortcut path; theme switching now happens through the app and tray menu only.
- Extract schedule/apply state into a dedicated runtime coordinator so timer, settings, Night Light, tray, and manual theme events share one serialized state path.
- Queue the latest user-requested theme switch while a registry apply is still broadcasting, instead of silently dropping rapid tray or button actions.
- Queue schedule evaluations that arrive during a registry theme apply and drain them immediately after the apply completes, keeping timer, settings, and Night Light changes serialized like the LightSwitch state manager.
- Route Night Light, resume, settings, and first-frame schedule checks through a guarded dispatcher entry point so transient registry or scheduling failures surface in-app instead of escaping WinUI callbacks.
- Route the sunset-to-sunrise location permission prompt, location dialog, and location sync action through a guarded UI task runner so dialog or settings-launch failures surface in-app.
- Let the main window continue when the tray icon cannot initialize; close-to-tray and minimize-to-tray behavior are disabled until the tray path is available.
- Keep startup health warnings for native hook or tray failures visible across normal schedule status refreshes with structured issue tracking.

### Maintenance
- Commit NuGet package lock files for the app, core, and test projects so restore inputs stay reproducible.
- Queue settings writes off the UI path while retaining an explicit shutdown flush for restart durability.
- Bump app, assembly, file, package manifest, and application manifest versions to `2.0.0`.

## 1.7.0 - 2026-07-04

### Reliability
- Migrate legacy `%APPDATA%\AutoDark` settings to the executable directory (mapping `Enabled: false` to the `Off` mode) instead of deleting them unread; the legacy directory is only removed after a successful migration.
- Load partial or hand-edited settings files by filling missing fields with defaults instead of crashing at startup (including a `null` hotkey entry).
- Exit cleanly when startup or teardown fails: window-construction failures terminate the process instead of leaving a windowless tray zombie, and tray Exit always completes even if the final settings flush throws.
- Run tray, hotkey, and resume callbacks on the dispatcher with exception guards instead of inside the native subclass window procedure, where a throw would terminate the process.
- Re-evaluate the schedule immediately when the system resumes from sleep or the clock/time zone changes, and clear a stale manual override after a full day of missed ticks or a clock set backwards.
- Fix the manual override being cleared twice across midnight when a boundary sits at 23:59, and keep it cleared when coordinates change through the location dialog.
- Report polar day/night above the polar circles and force the matching theme instead of pinning high-latitude users to light year-round.
- Let a schedule boundary that lands on the same tick as an external theme change win, converging both managed targets like the original module.
- Rebuild the Night Light watcher on managed wait handles (no more closing an event another thread may still be waiting on) and restart it automatically if it dies.
- Re-add the tray icon when Explorer restarts, falling back to a modify when the add reports the icon still exists; create the second-instance activation event before the main window is constructed so a launch during startup still surfaces the window.
- Recover from stalled Windows location requests with a client-side timeout, surface hotkey-registration conflicts persistently, and detect stale start-with-Windows entries after the app folder moves.
- Parse `F1`-`F24` hotkey names correctly and tolerate registry theme values rewritten as strings by third-party tools.

### Performance
- Draw the sunset-to-sunrise timeline with the same midnight-wrap segments the scheduler uses, removing the inverted daylight band.
- Skip registry theme writes (and the accompanying system broadcasts) when the target values are already in place.
- Skip redundant window-theme applies, timeline redraws, and adaptive-layout passes when nothing changed; remove per-frame reposition animations during interactive resize.
- Defer hotkey registration, the Night Light watcher, and the first schedule evaluation until after the first frame so the window appears sooner.
- Write the settings backup with a buffered copy instead of a second write-through flush, and route TimePicker changes through the save debounce.

### UX & Accessibility
- Convert the initial window size from physical pixels to DIPs so the adaptive layout picks the correct breakpoint on scaled displays.
- Explain the disabled "Switch now" button with a tooltip while both theme targets are off, and restore the "no theme target" warning for hotkey and tray toggles.
- Convert sun times per instant through `TimeZoneInfo`, so the correct UTC offset is used on DST-transition days.

### Maintenance
- Read the About version from the assembly so the csproj `<Version>` is the single source of truth.
- Consolidate duplicated comctl32 subclass interop, coordinate parsing/formatting, and single-instance name literals; remove the unused WebView2 package reference and pin package versions for reproducible publishes.
- Cap `crash.log` growth at 1 MB so a recurring failure cannot fill the disk.

## 1.6.0 - 2026-06-26

### Reliability
- Move settings and crash logs beside `AutoDark.exe`, use trim-safe source-generated JSON, and save settings synchronously so mode and icon changes survive immediate restarts.
- Keep a `settings.json.bak` fallback and load it when the primary settings file is corrupted, preventing a damaged JSON file from resetting mode to defaults.
- Delete the legacy `%APPDATA%\AutoDark` settings directory on startup after moving runtime files beside the executable.
- Persist and restore the native main-window bounds so the window no longer reopens at the initial 1024x640 layout after restart.
- Clamp restored main-window bounds to the current monitor work area so display-layout changes do not reopen the window off-screen.
- Apply scheduled theme changes off the UI thread and guard them with exception handling so a failed registry write can no longer freeze or crash the window.
- Add a re-entrancy guard shared by manual and scheduled theme switching to keep rapid hotkey presses or timer/manual interleaving from corrupting override state.

### Performance
- Debounce latitude, longitude, and offset edits so settings are no longer written to disk (and the schedule re-evaluated) on every keystroke.
- Re-register the global hotkey and the Night Light watcher only when those settings actually change.
- Refresh the Night Light state on the minute tick in Follow Night Light mode so a missed watcher event self-heals.
- Remove the unused `LastEvaluatedDay` runtime state.

### UX & Accessibility
- Treat an active manual override as normal status information instead of a warning.
- Surface the existing AutoDark window when the app is launched a second time instead of exiting silently.
- Edit coordinates through the location dialog only, removing the inconsistent inline latitude/longitude fields.
- Remove the non-interactive expander chevron from the "Apply dark mode to" header.
- Add accessible names to the icon selection buttons.
- Bump app, assembly, file, package manifest, and application manifest versions to `1.6.0`.

## 1.0.1 - 2026-06-13

- Reworked the main WinUI layout to more closely follow the original PowerToys Light Switch settings page structure.
- Added grouped Schedule, Behavior, Desktop, and Status sections.
- Added a Light Switch-style schedule timeline with effective light and dark boundary labels.
- Removed the visible Shortcut settings block while keeping the saved/default hotkey registered in the background.
- Added adaptive breakpoints for schedule mode, time, location, offset, timeline, and header rows so narrow windows and high-DPI layouts no longer collapse labels into vertical text.
- Added a minimum window width so the Schedule section keeps the PowerToys-style helper text and control layout while resizing.
- Kept Schedule mode, location, and offset controls horizontally aligned at the minimum supported width.
- Synchronized the native title bar with the current light or dark app theme.
- Set the main window title-bar icon from the LightSwitch icon asset.
- Changed `Set Location` to open a PowerToys-style location editor with manual coordinates and a separate sync-current-location action.
- Expanded Behavior to match the LightSwitch structure with System, Apps, and PowerDisplay profile rows.
- Added sun and moon markers to the Schedule timeline so light and dark ranges are visually labeled in both app themes.
- Matched the PowerToys schedule view more closely by showing configured coordinates as a compact location summary and positioning timeline boundary labels at their real times.
- Updated timeline rendering so fixed-hour schedules use the light-segment model while solar schedules highlight the visible daylight range.
- Added project README documentation for local build, run, and test commands.
- Bumped app, assembly, file, package manifest, and application manifest versions to `1.0.1`.

## 1.0.0 - 2026-06-12

- Initial standalone AutoDark WinUI 3 implementation.
- Ported Light Switch scheduling, theme registry writes, Night Light registry watching, settings persistence, tray icon, startup, and hotkey behavior.
