using System.Globalization;
using AutoDark.Core.Models;
using AutoDark.Core.Services;
using AutoDark.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace AutoDark;

public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1024;
    private const int DefaultWindowHeight = 640;
    private const int SaveDebounceMilliseconds = 400;

    private readonly SettingsStore _settingsStore = new(SettingsStore.DefaultPath);
    private readonly SettingsSaveQueue _settingsSaveQueue;
    private readonly ThemeRegistryService _themeService = new();
    private readonly ScheduleController _scheduleController = new(new SystemClock());
    private readonly NightLightService _nightLightService = new();
    private readonly LocationService _locationService = new();
    private readonly StartupService _startupService = new();
    private readonly ThemeRuntimeCoordinator _runtime;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _saveDebounceTimer = new();
    private readonly DispatcherTimer _windowBoundsSaveTimer = new();
    private readonly TrayService? _trayService;
    private readonly SystemHealthState _systemHealth = new();
    private AutoDarkSettings _settings;
    private ScheduleRuntimeState _state;
    private bool _loading = true;
    private bool _exitRequested;
    private bool _disposed;
    private bool _startupEnabled;
    private bool _trayAvailable;
    private bool? _lastWindowThemeLight;
    private bool _dialogOpen;
    private (bool Narrow, bool Compact)? _lastLayoutBucket;
    private (int Light, int Dark, double Width)? _lastTimelineKey;
    private string? _lastTimelineHelpText;
    // Matches the Source MainWindow.xaml assigns to HeaderIconImage so the
    // startup pass skips a redundant decode for the default variant.
    private AppIconVariant? _lastHeaderIconVariant = AppIconVariant.LightSwitch;
    private EventWaitHandle? _showRequestEvent;
    private RegisteredWaitHandle? _showRequestRegistration;

    public MainWindow()
    {
        InitializeComponent();

        Title = "AutoDark";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        // Single source of truth for the displayed version is the csproj
        // <Version>; the About card reads it from the assembly at runtime.
        AppVersionText.Text = $"Version {FormatAppVersion()}";

        _settings = _settingsStore.Load();
        _settingsSaveQueue = new SettingsSaveQueue(_settingsStore.Save);
        // The night-light blob read is only meaningful in FollowNightLight
        // mode; the deferred first evaluation refreshes it either way.
        _state = ScheduleRuntimeState.CreateInitial(
            _themeService.ReadThemeState(),
            _settings.ScheduleMode == ScheduleMode.FollowNightLight && _nightLightService.IsNightLightEnabled());
        _runtime = new ThemeRuntimeCoordinator(_settings, _state, _scheduleController);

        var hwnd = WindowInterop.GetWindowHandle(this);
        if (!WindowInterop.SetMinimumSize(
                this,
                AdaptiveLayoutRules.MinimumWindowWidth,
                AdaptiveLayoutRules.MinimumWindowHeight))
        {
            ShowSystemHealthWarning(
                SystemHealthIssue.WindowMinimumSizeHookUnavailable,
                "Window minimum-size hook could not be installed.");
        }

        RestoreWindowBounds();
        ApplyWindowTheme();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LightSwitch.ico");
        AppWindow.SetIcon(iconPath);
        _trayService = TryCreateTrayService(hwnd, iconPath);
        _trayAvailable = _trayService is not null;
        if (_trayService is not null)
        {
            _trayService.OpenRequested += (_, _) => DispatchSafe(ShowFromTray);
            _trayService.ToggleRequested += (_, _) => DispatchSafe(ToggleTheme);
            _trayService.ForceLightRequested += (_, _) => DispatchSafe(() => ForceTheme(isLight: true));
            _trayService.ForceDarkRequested += (_, _) => DispatchSafe(() => ForceTheme(isLight: false));
            _trayService.ExitRequested += (_, _) => DispatchSafe(ExitApplication);
        }

        _nightLightService.Changed += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RunScheduleCheckSafely("Night Light schedule check failed", RefreshNightLightState);
            });
        };

        _timer.Tick += OnTimerTick;
        ScheduleNextMinuteTick();

        _saveDebounceTimer.Interval = TimeSpan.FromMilliseconds(SaveDebounceMilliseconds);
        _saveDebounceTimer.Tick += OnSaveDebounceTick;
        _windowBoundsSaveTimer.Interval = TimeSpan.FromMilliseconds(SaveDebounceMilliseconds);
        _windowBoundsSaveTimer.Tick += OnWindowBoundsSaveTick;

        _showRequestEvent = App.ShowRequestEvent ?? CreateShowRequestEvent();
        if (_showRequestEvent is not null)
        {
            _showRequestRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showRequestEvent,
                (_, _) => DispatcherQueue.TryEnqueue(ShowFromTray),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        Closed += OnClosed;
        SizeChanged += OnWindowSizeChanged;
        AppWindow.Changed += OnAppWindowChanged;
        // AppWindow.Size is physical pixels while SizeChanged reports DIPs;
        // convert so the breakpoints see the same unit from the start.
        UpdateAdaptiveLayout(AppWindow.Size.Width / WindowInterop.GetDpiScale(this));

        ApplySettingsToUi();
        if (!WindowInterop.RegisterScheduleRefreshEvents(this, OnSystemScheduleRefresh))
        {
            ShowSystemHealthWarning(
                SystemHealthIssue.ScheduleRefreshHookUnavailable,
                "System resume/time-change hook could not be installed.");
        }

        // Nothing below shapes the first frame; run it after the window is up.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed)
            {
                return;
            }

            RunScheduleCheckSafely("Initial schedule check failed", UpdateNightLightWatcher);
        });
    }

    private void OnSystemScheduleRefresh()
    {
        // The callback fires inside the native subclass window-proc frame;
        // defer real work out of it and never let an exception unwind across
        // the reverse-P/Invoke boundary (that would fail-fast the process).
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            RunScheduleCheckSafely("Schedule check failed", () =>
            {
                TimeZoneInfo.ClearCachedData();
                ScheduleNextMinuteTick();
            });
        });
    }

    private TrayService? TryCreateTrayService(nint hwnd, string iconPath)
    {
        try
        {
            return new TrayService(hwnd, iconPath);
        }
        catch (Exception ex)
        {
            ShowSystemHealthWarning(
                SystemHealthIssue.TrayUnavailable,
                $"Tray icon could not be initialized: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Runs an action on the dispatcher instead of inside the native
    /// subclass window-proc frame that raised a native callback; an
    /// exception unwinding across that reverse-P/Invoke boundary would
    /// fail-fast the process.
    /// </summary>
    private void DispatchSafe(Action action)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                ShowStatus($"Action failed: {ex.Message}", InfoBarSeverity.Error);
            }
        });
    }

    private static EventWaitHandle? CreateShowRequestEvent()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, App.ShowRequestEventName);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateRuntimeSettings(AutoDarkSettings settings, bool clearManualOverrideIfScheduleChanged)
    {
        _runtime.UpdateSettings(settings, clearManualOverrideIfScheduleChanged);
        SyncRuntimeSnapshot();
    }

    private void SyncRuntimeSnapshot()
    {
        _settings = _runtime.Settings;
        _state = _runtime.State;
    }

    private void OnTimerTick(object? sender, object e)
    {
        ScheduleNextMinuteTick();

        RunScheduleCheckSafely("Schedule check failed", () =>
        {
            if (_settings.ScheduleMode == ScheduleMode.FollowNightLight && !_nightLightService.IsWatching)
            {
                // The push watcher died (missing CloudStore key, transient
                // notify failure); self-heal it and fall back to polling.
                _nightLightService.Start();
                RefreshNightLightState();
            }
        });
    }

    private void ScheduleNextMinuteTick()
    {
        var now = DateTimeOffset.Now;
        var msToNextMinute = ((60 - now.Second) * 1000) - now.Millisecond;
        if (msToNextMinute < 50)
        {
            msToNextMinute = 50;
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(msToNextMinute);
        _timer.Start();
    }

    private void ApplySettingsToUi()
    {
        _loading = true;

        ChangeSystemToggle.IsOn = _settings.ChangeSystem;
        ChangeAppsToggle.IsOn = _settings.ChangeApps;
        _startupEnabled = ReadStartupEnabled();
        StartWithWindowsToggle.IsOn = _startupEnabled;
        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTray;
        SelectScheduleMode(_settings.ScheduleMode);
        LightTimePicker.Time = TimeSpan.FromMinutes(_settings.LightTime);
        DarkTimePicker.Time = TimeSpan.FromMinutes(_settings.DarkTime);
        SunriseOffsetBox.Value = _settings.SunriseOffset;
        SunsetOffsetBox.Value = _settings.SunsetOffset;
        UpdateIconSelection(_settings.IconVariant);
        UpdateControlState();

        _loading = false;
    }

    private void SelectScheduleMode(ScheduleMode mode)
    {
        foreach (var item in ScheduleModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal))
            {
                ScheduleModeComboBox.SelectedItem = item;
                return;
            }
        }

        ScheduleModeComboBox.SelectedIndex = 1;
    }

    private AutoDarkSettings ReadSettingsFromUi()
    {
        var selectedMode = ScheduleMode.FixedHours;
        if (ScheduleModeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            selectedMode = Enum.Parse<ScheduleMode>(tag);
        }

        return _settings with
        {
            ChangeSystem = ChangeSystemToggle.IsOn,
            ChangeApps = ChangeAppsToggle.IsOn,
            ScheduleMode = selectedMode,
            LightTime = (int)LightTimePicker.Time.TotalMinutes,
            DarkTime = (int)DarkTimePicker.Time.TotalMinutes,
            SunriseOffset = NumberBoxToInt(SunriseOffsetBox),
            SunsetOffset = NumberBoxToInt(SunsetOffsetBox),
            // OnStartupToggleChanged is the sole writer of StartWithWindows;
            // reading the toggle here could persist a mid-rollback value.
            StartWithWindows = _settings.StartWithWindows,
            MinimizeToTray = MinimizeToTrayToggle.IsOn,
            IconVariant = CurrentSelectedIconVariant()
        };
    }

    private void RequestSaveSettingsFromUi()
    {
        if (_loading)
        {
            return;
        }

        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void OnSaveDebounceTick(object? sender, object e)
    {
        _saveDebounceTimer.Stop();
        SaveSettingsFromUi();
    }

    private void SaveSettingsFromUi(bool deferEvaluation = false)
    {
        if (_loading)
        {
            return;
        }

        _saveDebounceTimer.Stop();

        var previous = _settings;
        UpdateRuntimeSettings(ReadSettingsFromUi(), clearManualOverrideIfScheduleChanged: true);

        PersistSettings();
        UpdateControlState();
        if (previous.ScheduleMode != _settings.ScheduleMode)
        {
            UpdateNightLightWatcher();
        }

        if (deferEvaluation)
        {
            DispatcherQueue.TryEnqueue(() => RunScheduleCheckSafely("Schedule check failed"));
            return;
        }

        RunScheduleCheckSafely("Schedule check failed");
    }

    private void PersistSettings()
    {
        var snapshot = _settings;
        try
        {
            _settingsSaveQueue.QueueSave(snapshot);
            if (_settingsSaveQueue.LastError is { } lastError)
            {
                ShowStatus($"Settings could not be saved: {lastError.Message}", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Settings could not be saved: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void FlushSettingsSaveQueue()
    {
        if (_settingsSaveQueue.Flush() is { } error)
        {
            ShowStatus($"Settings could not be saved: {error.Message}", InfoBarSeverity.Warning);
        }
    }

    private void RestoreWindowBounds()
    {
        if (TryReadSavedWindowBounds(out var bounds)
            && WindowInterop.TrySetWindowRect(this, WindowInterop.ClampToVisibleWorkArea(bounds)))
        {
            return;
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
    }

    private bool TryReadSavedWindowBounds(out WindowBounds bounds)
    {
        if (_settings.WindowLeft is int left
            && _settings.WindowTop is int top
            && _settings.WindowWidth is int width
            && _settings.WindowHeight is int height)
        {
            bounds = new WindowBounds(left, top, width, height);
            return IsValidWindowBounds(bounds);
        }

        bounds = default;
        return false;
    }

    private static bool IsValidWindowBounds(WindowBounds bounds)
    {
        return bounds.Width >= AdaptiveLayoutRules.MinimumWindowWidth
            && bounds.Height >= AdaptiveLayoutRules.MinimumWindowHeight;
    }

    private void RequestSaveWindowBounds()
    {
        if (_loading || _disposed || WindowInterop.IsMinimized(this))
        {
            return;
        }

        _windowBoundsSaveTimer.Stop();
        _windowBoundsSaveTimer.Start();
    }

    private void OnWindowBoundsSaveTick(object? sender, object e)
    {
        _windowBoundsSaveTimer.Stop();
        SaveWindowBounds();
    }

    private void SaveWindowBounds()
    {
        if (_loading || _disposed || WindowInterop.IsMinimized(this))
        {
            return;
        }

        if (!WindowInterop.TryGetWindowRect(this, out var bounds) || !IsValidWindowBounds(bounds))
        {
            return;
        }

        var updated = _settings with
        {
            WindowLeft = bounds.Left,
            WindowTop = bounds.Top,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height
        };

        if (updated == _settings)
        {
            return;
        }

        UpdateRuntimeSettings(updated, clearManualOverrideIfScheduleChanged: false);
        PersistSettings();
    }

    private bool ReadStartupEnabled()
    {
        try
        {
            return _startupService.IsEnabled();
        }
        catch (Exception ex)
        {
            ShowStatus($"Startup setting could not be read: {ex.Message}", InfoBarSeverity.Warning);
            return _settings.StartWithWindows;
        }
    }

    private void EvaluateAndApply()
    {
        var evaluation = RequestScheduleEvaluationCore("Schedule check failed");
        if (evaluation is null)
        {
            return;
        }

        ApplyScheduleEvaluation(evaluation);
    }

    private void RequestScheduleEvaluation(string failurePrefix)
    {
        var evaluation = RequestScheduleEvaluationCore(failurePrefix);
        if (evaluation is null)
        {
            return;
        }

        ApplyScheduleEvaluation(evaluation);
    }

    private void RunScheduleCheckSafely(string failurePrefix)
    {
        RunScheduleCheckSafely(failurePrefix, beforeEvaluation: null);
    }

    private void RunScheduleCheckSafely(string failurePrefix, Action? beforeEvaluation)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            beforeEvaluation?.Invoke();
            RequestScheduleEvaluation(failurePrefix);
        }
        catch (Exception ex)
        {
            ShowStatus($"{failurePrefix}: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void RefreshNightLightState()
    {
        var nightLightActive = _nightLightService.IsNightLightEnabled();
        _runtime.RefreshNightLightState(nightLightActive);
        SyncRuntimeSnapshot();
    }

    private ScheduleEvaluation? RequestScheduleEvaluationCore(string failurePrefix)
    {
        var evaluation = _runtime.RequestScheduleEvaluation(_themeService.ReadThemeState(), failurePrefix);
        SyncRuntimeSnapshot();
        return evaluation;
    }

    private void ApplyScheduleEvaluation(ScheduleEvaluation evaluation)
    {
        if (evaluation.ShouldApply)
        {
            var shouldBeLight = evaluation.ShouldBeLight;
            var changeSystem = evaluation.ChangeSystem;
            var changeApps = evaluation.ChangeApps;
            _ = ApplyPlanAsync(
                current => ThemeSwitchPlanner.ForceSelectedTargets(shouldBeLight, current, changeSystem, changeApps),
                ThemeOverrideTransition.None,
                reevaluateAfter: false);
        }

        UpdateStatus(evaluation);
    }

    private void ToggleTheme() => _ = ApplyPlanAsync(
        current => ThemeSwitchPlanner.ToggleSelectedTargets(current, _settings.ChangeSystem, _settings.ChangeApps),
        ThemeOverrideTransition.Toggle,
        reevaluateAfter: true);

    private void ForceTheme(bool isLight) => _ = ApplyPlanAsync(
        _ => ThemeSwitchPlanner.ForceAllTargets(isLight),
        ThemeOverrideTransition.Enter,
        reevaluateAfter: true);

    private async Task ApplyPlanAsync(
        Func<ThemeState, ThemeApplyPlan> createPlan,
        ThemeOverrideTransition overrideTransition,
        bool reevaluateAfter)
    {
        if (_runtime.ApplyInProgress)
        {
            _runtime.QueueApply(new ThemeApplyRequest(createPlan, overrideTransition, reevaluateAfter));
            ShowStatus("Theme switch queued.", InfoBarSeverity.Informational);
            return;
        }

        // Callers discard this Task, so a throw before the main try would
        // vanish as an unobserved exception with no user feedback.
        ThemeState current;
        ThemeApplyPlan plan;
        try
        {
            current = _themeService.ReadThemeState();
            plan = createPlan(current);
        }
        catch (Exception ex)
        {
            ShowStatus($"Theme switch failed: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        if (!plan.HasTargets)
        {
            ShowStatus("No theme target is selected.", InfoBarSeverity.Warning);
            return;
        }

        var predictedTheme = plan.ToThemeState(current);
        var applySucceeded = false;
        _runtime.BeginApply(predictedTheme, overrideTransition);
        SyncRuntimeSnapshot();
        try
        {
            ApplyWindowTheme(ResolveWindowThemeLight(predictedTheme));

            await Task.Run(() => _themeService.Apply(plan));
            applySucceeded = true;
        }
        catch (Exception ex)
        {
            ShowStatus($"Theme switch failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            // Re-sync from the registry even on failure so cached state never
            // claims a switch that did not land; nothing here may throw past
            // the flag reset or theme switching would be wedged for good.
            try
            {
                var appliedTheme = _themeService.ReadThemeState();
                _runtime.CompleteApply(appliedTheme, predictedTheme, plan, applySucceeded);
            }
            catch
            {
                _runtime.FinishApplyWithoutReadBack();
            }

            SyncRuntimeSnapshot();
        }

        if (_runtime.TakePendingApplyRequest() is { } pendingApplyRequest)
        {
            await RunPendingApplyRequestAsync(pendingApplyRequest);
            return;
        }

        if (reevaluateAfter)
        {
            _runtime.QueueScheduleEvaluation("Schedule check failed");
        }

        DrainPendingScheduleEvaluation();
    }

    private async Task RunPendingApplyRequestAsync(ThemeApplyRequest request)
    {
        await ApplyPlanAsync(request.CreatePlan, request.OverrideTransition, request.ReevaluateAfter);
    }

    private void DrainPendingScheduleEvaluation()
    {
        var pending = _runtime.TakePendingScheduleEvaluation();
        if (pending is null)
        {
            return;
        }

        RunScheduleCheckSafely(pending.Value.FailurePrefix);
    }

    private void UpdateStatus(ScheduleEvaluation evaluation)
    {
        var solarInvalid = _settings.ScheduleMode == ScheduleMode.SunsetToSunrise
            && !SunTimesCalculator.CoordinatesAreValid(_settings.Latitude, _settings.Longitude);
        var currentMode = _settings.ScheduleMode switch
        {
            ScheduleMode.Off => "Off",
            ScheduleMode.FixedHours => "Fixed hours",
            ScheduleMode.SunsetToSunrise => "Sunset to sunrise",
            ScheduleMode.FollowNightLight => "Follow Windows Night Light",
            _ => _settings.ScheduleMode.ToString()
        };
        ApplyWindowTheme();
        UpdateManualThemePresentation();
        var lightText = FormatMinute(_state.EffectiveLightMinutes);
        var darkText = FormatMinute(_state.EffectiveDarkMinutes);
        if (LightBoundaryText.Text != lightText)
        {
            LightBoundaryText.Text = lightText;
        }

        if (DarkBoundaryText.Text != darkText)
        {
            DarkBoundaryText.Text = darkText;
        }

        var helpText = $"Start: {lightText}; End: {darkText}; Mode: {currentMode}";
        if (_lastTimelineHelpText != helpText)
        {
            _lastTimelineHelpText = helpText;
            AutomationProperties.SetHelpText(TimelinePanel, helpText);
        }

        UpdateLocationPresentation();
        UpdateTimeline();
        if (solarInvalid)
        {
            ShowStatus(
                $"{evaluation.Status} Coordinates are invalid; fixed boundaries are in use.",
                InfoBarSeverity.Warning);
            return;
        }

        if (_state.IsManualOverride)
        {
            ShowStatus("Manual override active until the next scheduled switch.", InfoBarSeverity.Informational);
            return;
        }

        // No transient status to show; keep any startup health warning visible.
        ClearTransientStatus();
    }

    private void UpdateManualThemePresentation()
    {
        var hasTarget = _settings.ChangeSystem || _settings.ChangeApps;
        ManualSwitchButton.IsEnabled = hasTarget;
        // Explain the disabled state where the user is looking (tooltips do
        // show on disabled WinUI buttons).
        ToolTipService.SetToolTip(
            ManualSwitchButton,
            hasTarget ? null : "Enable at least one theme target (System or Apps) to switch manually.");
        var currentText = $"Current: {FormatManualThemeState()}";
        if (ManualCurrentThemeText.Text != currentText)
        {
            ManualCurrentThemeText.Text = currentText;
        }
    }

    private string FormatManualThemeState()
    {
        bool? selectedLightTheme = null;

        if (_settings.ChangeSystem)
        {
            selectedLightTheme = _state.IsSystemLightActive;
        }

        if (_settings.ChangeApps)
        {
            if (selectedLightTheme.HasValue && selectedLightTheme.Value != _state.IsAppsLightActive)
            {
                return "Mixed";
            }

            selectedLightTheme = _state.IsAppsLightActive;
        }

        return selectedLightTheme.HasValue ? FormatTheme(selectedLightTheme.Value) : "No target";
    }

    private void ApplyWindowTheme()
    {
        var isLightTheme = ResolveWindowThemeLight();
        ApplyWindowTheme(isLightTheme);
    }

    private bool ResolveWindowThemeLight()
    {
        return ResolveWindowThemeLight(new ThemeState(_state.IsSystemLightActive, _state.IsAppsLightActive));
    }

    private bool ResolveWindowThemeLight(ThemeState themeState)
    {
        return _settings.ChangeSystem ? themeState.SystemLight : themeState.AppsLight;
    }

    private void ApplyWindowTheme(bool isLightTheme)
    {
        // Called from every timer tick via UpdateStatus; skipping unchanged
        // themes avoids a DWM frame-change repaint each minute.
        if (_lastWindowThemeLight == isLightTheme)
        {
            return;
        }

        _lastWindowThemeLight = isLightTheme;
        RootGrid.RequestedTheme = isLightTheme ? ElementTheme.Light : ElementTheme.Dark;
        ApplyCaptionButtonTheme(isLightTheme);
        WindowInterop.SetTitleBarTheme(this, isLightTheme);
    }

    private void ApplyCaptionButtonTheme(bool isLightTheme)
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = isLightTheme
            ? Windows.UI.Color.FromArgb(0x0F, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedBackgroundColor = isLightTheme
            ? Windows.UI.Color.FromArgb(0x18, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonForegroundColor = isLightTheme
            ? Microsoft.UI.Colors.Black
            : Microsoft.UI.Colors.White;
        titleBar.ButtonInactiveForegroundColor = isLightTheme
            ? Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        InlineStatusInfo.Message = message;
        InlineStatusInfo.Severity = severity;
        InlineStatusInfo.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    private void ShowSystemHealthWarning(SystemHealthIssue issue, string message)
    {
        _systemHealth.AddWarning(issue, message);
        ShowStatus(_systemHealth.WarningText, InfoBarSeverity.Warning);
    }

    private void ClearTransientStatus()
    {
        if (_systemHealth.HasWarnings)
        {
            ShowStatus(_systemHealth.WarningText, InfoBarSeverity.Warning);
            return;
        }

        ShowStatus(string.Empty, InfoBarSeverity.Informational);
    }

    private void UpdateControlState()
    {
        var mode = CurrentSelectedMode();
        var fixedMode = mode == ScheduleMode.FixedHours;
        var solarMode = mode == ScheduleMode.SunsetToSunrise;
        var timelineMode = fixedMode || solarMode;

        LightTimePicker.IsEnabled = fixedMode;
        DarkTimePicker.IsEnabled = fixedMode;
        FixedTimeRow.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        SolarLocationRow.Visibility = solarMode ? Visibility.Visible : Visibility.Collapsed;
        SolarOffsetRow.Visibility = solarMode ? Visibility.Visible : Visibility.Collapsed;
        TimelinePanel.Visibility = timelineMode ? Visibility.Visible : Visibility.Collapsed;
        SunriseOffsetBox.IsEnabled = solarMode;
        SunsetOffsetBox.IsEnabled = solarMode;
        MinimizeToTrayToggle.IsEnabled = _trayAvailable;
        ToolTipService.SetToolTip(
            MinimizeToTrayToggle,
            _trayAvailable ? null : "Tray icon is unavailable; closing the window will exit AutoDark.");
        UpdateLocationPresentation();
    }

    private void UpdateTimeline()
    {
        var width = TimelineCanvas.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        // Redrawing is pure function of the boundaries and the width; the
        // minute timer calls this every tick with unchanged inputs.
        var key = (_state.EffectiveLightMinutes, _state.EffectiveDarkMinutes, width);
        if (_lastTimelineKey == key)
        {
            return;
        }

        _lastTimelineKey = key;
        TimelineTrack.Width = width;
        SetCanvasLeft(TimelineSixAmLabel, width * 0.25);
        SetCanvasLeft(TimelineNoonLabel, width * 0.50);
        SetCanvasLeft(TimelineSixPmLabel, width * 0.75);
        SetCanvasLeft(TimelineEndLabel, Math.Max(0, width - 58));
        TimelineLabelCanvas.Width = width;

        PositionBoundaryLabel(LightBoundaryText, _state.EffectiveLightMinutes, width);
        PositionBoundaryLabel(DarkBoundaryText, _state.EffectiveDarkMinutes, width);

        var segments = ScheduleMath.GetLightSegments(_state.EffectiveLightMinutes, _state.EffectiveDarkMinutes);
        DrawTimelineSegment(TimelineLightSegment, segments[0], width);

        if (segments.Count > 1)
        {
            DrawTimelineSegment(TimelineLightSegmentWrap, segments[1], width);
        }
        else
        {
            TimelineLightSegmentWrap.Visibility = Visibility.Collapsed;
        }
        DrawTimelineIcons(
            segments,
            [TimelineLightIcon, TimelineLightIconWrap],
            width);
        DrawTimelineIcons(
            ScheduleMath.GetComplementSegments(segments),
            [TimelineDarkIcon, TimelineDarkIconWrap],
            width);
    }

    private static void DrawTimelineSegment(FrameworkElement element, TimelineSegment segment, double width)
    {
        var start = segment.StartMinute / 1440.0 * width;
        var end = segment.EndMinute / 1440.0 * width;

        element.Visibility = Visibility.Visible;
        SetCanvasLeft(element, start);
        element.Width = Math.Max(4, end - start);
    }

    private static void PositionBoundaryLabel(TextBlock label, int minute, double width)
    {
        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var x = ScheduleMath.NormalizeMinute(minute) / 1440.0 * width;
        var left = Math.Clamp(x - (label.DesiredSize.Width / 2), 0, Math.Max(0, width - label.DesiredSize.Width));
        SetCanvasLeft(label, left);
    }

    private static void DrawTimelineIcons(
        IReadOnlyList<TimelineSegment> segments,
        IReadOnlyList<FrameworkElement> icons,
        double width)
    {
        for (var i = 0; i < icons.Count; i++)
        {
            if (i >= segments.Count)
            {
                icons[i].Visibility = Visibility.Collapsed;
                continue;
            }

            PositionTimelineIcon(icons[i], segments[i], width);
        }
    }

    private static void PositionTimelineIcon(FrameworkElement icon, TimelineSegment segment, double width)
    {
        var segmentWidth = (segment.EndMinute - segment.StartMinute) / 1440.0 * width;
        if (segmentWidth < 28)
        {
            icon.Visibility = Visibility.Collapsed;
            return;
        }

        icon.Visibility = Visibility.Visible;
        icon.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var iconWidth = icon.DesiredSize.Width > 0 ? icon.DesiredSize.Width : 16;
        var center = ((segment.StartMinute + segment.EndMinute) / 2.0) / 1440.0 * width;
        SetCanvasLeft(icon, Math.Clamp(center - (iconWidth / 2), 0, Math.Max(0, width - iconWidth)));
    }

    private static void SetCanvasLeft(UIElement element, double left)
    {
        Canvas.SetLeft(element, left);
    }

    private void UpdateLocationPresentation()
    {
        var coordinatesValid = SunTimesCalculator.CoordinatesAreValid(_settings.Latitude, _settings.Longitude);

        var summary = coordinatesValid
            ? DisplayFormatters.FormatCoordinateSummary(_settings.Latitude, _settings.Longitude)
            : "Not set";
        if (LocationSummaryText.Text != summary)
        {
            LocationSummaryText.Text = summary;
        }
    }

    private ScheduleMode CurrentSelectedMode()
    {
        if (ScheduleModeComboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse<ScheduleMode>(tag, out var mode))
        {
            return mode;
        }

        return _settings.ScheduleMode;
    }

    private void UpdateNightLightWatcher()
    {
        if (_settings.ScheduleMode == ScheduleMode.FollowNightLight)
        {
            _nightLightService.Start();
            // Toggles that happened while the watcher was stopped were never
            // observed; refresh before the caller evaluates. Only a real
            // change may go through OnNightLightChanged (it clears overrides).
            var nightLightActive = _nightLightService.IsNightLightEnabled();
            if (nightLightActive != _state.IsNightLightActive)
            {
                _runtime.RefreshNightLightState(nightLightActive);
                SyncRuntimeSnapshot();
            }

            return;
        }

        _nightLightService.Stop();
    }

    private void ShowFromTray()
    {
        if (_disposed || _exitRequested)
        {
            return;
        }

        WindowInterop.Show(this);
        WindowInterop.Restore(this);
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;

        // Exit is best-effort: a failed settings flush or teardown must not
        // leave a process the user asked to close (ShowFromTray refuses to
        // reopen once _exitRequested is set, so there is no way back).
        try
        {
            DisposeServices();
        }
        catch
        {
        }

        try
        {
            Close();
        }
        catch
        {
        }

        Application.Current.Exit();
    }

    private void DisposeServices()
    {
        if (_disposed)
        {
            return;
        }

        FlushPendingSettingsSave();
        FlushPendingWindowBoundsSave();
        FlushSettingsSaveQueue();
        _timer.Stop();
        _saveDebounceTimer.Stop();
        _windowBoundsSaveTimer.Stop();
        AppWindow.Changed -= OnAppWindowChanged;
        WindowInterop.UnregisterScheduleRefreshEvents(this);
        _showRequestRegistration?.Unregister(null);
        _showRequestEvent?.Dispose();
        WindowInterop.ClearMinimumSize(this);
        _nightLightService.Dispose();
        _trayService?.Dispose();
        _settingsSaveQueue.Dispose();
        _disposed = true;
    }

    private void FlushPendingSettingsSave()
    {
        if (_loading || !_saveDebounceTimer.IsEnabled)
        {
            return;
        }

        SaveSettingsFromUi();
    }

    private void FlushPendingWindowBoundsSave()
    {
        if (_loading)
        {
            return;
        }

        _windowBoundsSaveTimer.Stop();
        SaveWindowBounds();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (!_exitRequested && _settings.MinimizeToTray && _trayAvailable)
        {
            FlushPendingSettingsSave();
            FlushPendingWindowBoundsSave();
            args.Handled = true;
            WindowInterop.Hide(this);
            return;
        }

        DisposeServices();
    }

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        UpdateAdaptiveLayout(args.Size.Width);
        RequestSaveWindowBounds();

        if (_settings.MinimizeToTray && _trayAvailable && WindowInterop.IsMinimized(this))
        {
            WindowInterop.Hide(this);
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            RequestSaveWindowBounds();
        }
    }

    private void OnPageScrollViewerSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateContentHostWidth(args.NewSize.Width);
    }

    private void UpdateAdaptiveLayout(double width)
    {
        var narrow = AdaptiveLayoutRules.IsNarrow(width);
        var compact = AdaptiveLayoutRules.IsCompact(width);

        // Per-pixel work: only the content width tracks the exact size.
        UpdateContentHostWidth(PageScrollViewer.ActualWidth > 0 ? PageScrollViewer.ActualWidth : width);

        // Everything else depends solely on the breakpoint bucket; skipping
        // it keeps interactive resize from re-invalidating the whole tree.
        if (_lastLayoutBucket == (narrow, compact))
        {
            return;
        }

        _lastLayoutBucket = (narrow, compact);
        ContentStack.Padding = compact
            ? new Thickness(20, 16, 20, 28)
            : narrow
                ? new Thickness(32, 18, 32, 30)
                : new Thickness(48, 20, 48, 32);
        ContentStack.Spacing = 22;

        ApplyScheduleModeLayout(narrow);
        ApplySettingRowsLayout(narrow, compact);
        ApplyTimePickerLayout(compact);
        UpdateLocationPresentation();
        ApplyLocationInputLayout(compact);
        ApplyOffsetInputLayout(compact);
    }

    private void UpdateContentHostWidth(double viewportWidth)
    {
        if (viewportWidth <= 0)
        {
            return;
        }

        ContentHost.Width = viewportWidth;
        ContentStack.Width = AdaptiveLayoutRules.GetContentWidth(viewportWidth);
    }

    private void ApplyScheduleModeLayout(bool narrow)
    {
        if (narrow)
        {
            ScheduleModeComboBox.Width = double.NaN;
            ScheduleModeComboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            SetGridPosition(ScheduleModeComboBox, 1, 1);
            Grid.SetColumnSpan(ScheduleModeComboBox, 2);
            return;
        }

        ScheduleModeComboBox.Width = 220;
        ScheduleModeComboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        SetGridPosition(ScheduleModeComboBox, 0, 2);
        Grid.SetColumnSpan(ScheduleModeComboBox, 1);
    }

    private void ApplySettingRowsLayout(bool narrow, bool compact)
    {
        var rowPadding = compact
            ? new Thickness(20, 10, 20, 10)
            : narrow
                ? new Thickness(68, 10, 28, 10)
                : new Thickness(58, 8, 44, 8);

        ApplyDescriptionControlLayout(
            FixedTimeRow,
            FixedTimeDescriptionPanel,
            FixedTimePickerGrid,
            FixedTimeDescriptionColumn,
            FixedTimeControlsColumn,
            rowPadding,
            narrow);
        ApplyDescriptionControlLayout(
            SolarLocationRow,
            SolarLocationDescriptionPanel,
            SolarLocationInputGrid,
            SolarLocationDescriptionColumn,
            SolarLocationControlsColumn,
            rowPadding,
            narrow);
        ApplyDescriptionControlLayout(
            SolarOffsetRow,
            SolarOffsetDescriptionPanel,
            SolarOffsetInputGrid,
            SolarOffsetDescriptionColumn,
            SolarOffsetControlsColumn,
            rowPadding,
            narrow);

        TimelinePanel.Padding = compact
            ? new Thickness(20, 18, 20, 28)
            : narrow
                ? new Thickness(64, 18, 28, 28)
                : new Thickness(58, 12, 44, 18);
    }

    private static void ApplyDescriptionControlLayout(
        Grid row,
        FrameworkElement description,
        FrameworkElement controls,
        ColumnDefinition descriptionColumn,
        ColumnDefinition controlsColumn,
        Thickness padding,
        bool narrow)
    {
        row.Padding = padding;

        if (narrow)
        {
            descriptionColumn.Width = Star();
            controlsColumn.Width = Zero();
            SetGridPosition(description, 0, 0);
            SetGridPosition(controls, 1, 0);
            return;
        }

        descriptionColumn.Width = Star();
        controlsColumn.Width = GridLength.Auto;
        SetGridPosition(description, 0, 0);
        SetGridPosition(controls, 0, 1);
    }

    private void ApplyTimePickerLayout(bool compact)
    {
        if (compact)
        {
            FixedTimePickerGrid.ColumnSpacing = 0;
            LightTimeColumn.Width = Star();
            DarkTimeColumn.Width = Zero();
            SetGridPosition(LightTimePicker, 0, 0);
            SetGridPosition(DarkTimePicker, 1, 0);
            return;
        }

        FixedTimePickerGrid.ColumnSpacing = 16;
        LightTimeColumn.Width = Star();
        DarkTimeColumn.Width = Star();
        SetGridPosition(LightTimePicker, 0, 0);
        SetGridPosition(DarkTimePicker, 0, 1);
    }

    private void ApplyLocationInputLayout(bool compact)
    {
        if (compact)
        {
            SolarLocationInputGrid.ColumnSpacing = 0;
            SolarLocationInputGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            LocationSummaryColumn.Width = Star();
            SetLocationColumn.Width = Zero();
            SyncLocationColumn.Width = Zero();
            SetGridPosition(LocationSummaryText, 0, 0);
            SetGridPosition(SetLocationButton, 1, 0);
            SetGridPosition(SyncLocationButton, 2, 0);
            SetLocationButton.HorizontalAlignment = HorizontalAlignment.Left;
            SyncLocationButton.HorizontalAlignment = HorizontalAlignment.Left;
            return;
        }

        SolarLocationInputGrid.ColumnSpacing = 12;
        SolarLocationInputGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        LocationSummaryColumn.Width = Star();
        SetLocationColumn.Width = GridLength.Auto;
        SyncLocationColumn.Width = GridLength.Auto;
        SetGridPosition(LocationSummaryText, 0, 0);
        SetGridPosition(SetLocationButton, 0, 1);
        SetGridPosition(SyncLocationButton, 0, 2);
        SetLocationButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        SyncLocationButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void ApplyOffsetInputLayout(bool compact)
    {
        if (compact)
        {
            SolarOffsetInputGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            SunriseOffsetLabelColumn.Width = GridLength.Auto;
            SunriseOffsetBoxColumn.Width = Star();
            SunsetOffsetLabelColumn.Width = Zero();
            SunsetOffsetBoxColumn.Width = Zero();
            SetGridPosition(SunriseOffsetLabel, 0, 0);
            SetGridPosition(SunriseOffsetBox, 0, 1);
            SetGridPosition(SunsetOffsetLabel, 1, 0);
            SetGridPosition(SunsetOffsetBox, 1, 1);
            return;
        }

        SolarOffsetInputGrid.HorizontalAlignment = HorizontalAlignment.Left;
        SunriseOffsetLabelColumn.Width = GridLength.Auto;
        SunriseOffsetBoxColumn.Width = new GridLength(110);
        SunsetOffsetLabelColumn.Width = GridLength.Auto;
        SunsetOffsetBoxColumn.Width = new GridLength(110);
        SetGridPosition(SunriseOffsetLabel, 0, 0);
        SetGridPosition(SunriseOffsetBox, 0, 1);
        SetGridPosition(SunsetOffsetLabel, 0, 2);
        SetGridPosition(SunsetOffsetBox, 0, 3);
    }

    private static GridLength Star() => new(1, GridUnitType.Star);

    private static GridLength Zero() => new(0);

    private static void SetGridPosition(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private AppIconVariant CurrentSelectedIconVariant()
    {
        if (LightSwitchIconOption.IsChecked == true)
        {
            return AppIconVariant.LightSwitch;
        }

        if (DawnIconOption.IsChecked == true)
        {
            return AppIconVariant.Dawn;
        }

        if (OrbitIconOption.IsChecked == true)
        {
            return AppIconVariant.Orbit;
        }

        return _settings.IconVariant;
    }

    private void UpdateIconSelection(AppIconVariant variant)
    {
        LightSwitchIconOption.IsChecked = variant == AppIconVariant.LightSwitch;
        DawnIconOption.IsChecked = variant == AppIconVariant.Dawn;
        OrbitIconOption.IsChecked = variant == AppIconVariant.Orbit;
        if (_lastHeaderIconVariant != variant)
        {
            _lastHeaderIconVariant = variant;
            HeaderIconImage.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, IconAssetPath(variant))));
        }
    }

    private static string IconAssetPath(AppIconVariant variant)
    {
        return variant switch
        {
            AppIconVariant.Dawn => Path.Combine("Assets", "AutoDarkLogoDawn.scale-200.png"),
            AppIconVariant.Orbit => Path.Combine("Assets", "AutoDarkLogoOrbit.scale-200.png"),
            _ => Path.Combine("Assets", "LightSwitch.scale-200.png")
        };
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs args) => UpdateTimeline();

    private void OnToggleSettingChanged(object sender, RoutedEventArgs args) => SaveSettingsFromUi();

    private void OnStartupToggleChanged(object sender, RoutedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        var requestedStartWithWindows = StartWithWindowsToggle.IsOn;
        if (requestedStartWithWindows == _startupEnabled)
        {
            UpdateRuntimeSettings(
                _settings with { StartWithWindows = requestedStartWithWindows },
                clearManualOverrideIfScheduleChanged: false);
            PersistSettings();
            return;
        }

        try
        {
            _startupService.SetEnabled(requestedStartWithWindows);
            _startupEnabled = requestedStartWithWindows;
            UpdateRuntimeSettings(
                _settings with { StartWithWindows = requestedStartWithWindows },
                clearManualOverrideIfScheduleChanged: false);
            PersistSettings();
        }
        catch (Exception ex)
        {
            _loading = true;
            StartWithWindowsToggle.IsOn = _startupEnabled;
            _loading = false;
            ShowStatus($"Startup setting failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnScheduleModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        var previousMode = _settings.ScheduleMode;
        SaveSettingsFromUi(deferEvaluation: true);

        if (_settings.ScheduleMode == ScheduleMode.SunsetToSunrise
            && previousMode != ScheduleMode.SunsetToSunrise)
        {
            RunUiTaskSafely(RequestLocationPermissionForSolarModeAsync, "Location permission request failed");
        }
    }

    private void RunUiTaskSafely(Func<Task> taskFactory, string failurePrefix)
    {
        _ = RunUiTaskSafelyAsync(taskFactory, failurePrefix);
    }

    private async Task RunUiTaskSafelyAsync(Func<Task> taskFactory, string failurePrefix)
    {
        try
        {
            await taskFactory();
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                ShowStatus($"{failurePrefix}: {ex.Message}", InfoBarSeverity.Warning);
            }
        }
    }

    private void OnTimeChanged(object sender, TimePickerValueChangedEventArgs args) => RequestSaveSettingsFromUi();

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => RequestSaveSettingsFromUi();

    private void OnManualSwitchClicked(object sender, RoutedEventArgs args) => ToggleTheme();

    private void OnIconOptionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse<AppIconVariant>(tag, out var variant))
        {
            UpdateIconSelection(_settings.IconVariant);
            return;
        }

        UpdateIconSelection(variant);
        SaveSettingsFromUi();
    }

    private void OnSetLocationClicked(object sender, RoutedEventArgs args)
    {
        RunUiTaskSafely(ShowLocationDialogAsync, "Location dialog failed");
    }

    private void OnSyncLocationClicked(object sender, RoutedEventArgs args)
    {
        RunUiTaskSafely(SyncLocationAsync, "Location sync failed");
    }

    private async Task RequestLocationPermissionForSolarModeAsync()
    {
        var result = await _locationService.RequestAccessAsync();
        if (_disposed || _settings.ScheduleMode != ScheduleMode.SunsetToSunrise)
        {
            return;
        }

        ShowStatus(result.Message, result.Success ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        if (!result.Success)
        {
            await ShowLocationAccessDialogAsync(result.Message);
        }
    }

    private async Task ShowLocationAccessDialogAsync(string message)
    {
        // WinUI throws if two ContentDialogs share a XamlRoot; the deferred
        // permission dialog can race the Set-location dialog.
        if (_dialogOpen)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Location access",
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Open settings",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = RootGrid.RequestedTheme
        };

        ContentDialogResult dialogResult;
        _dialogOpen = true;
        try
        {
            dialogResult = await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }

        if (dialogResult == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-location"));
        }
    }

    private async Task SyncLocationAsync()
    {
        SetLocationButton.IsEnabled = false;
        SyncLocationButton.IsEnabled = false;
        ShowStatus("Requesting location.", InfoBarSeverity.Informational);

        try
        {
            var result = await _locationService.TryGetLocationAsync();
            ShowStatus(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

            if (result.Success)
            {
                ApplyLocationCoordinates(result.Latitude, result.Longitude);
            }
        }
        finally
        {
            SetLocationButton.IsEnabled = true;
            SyncLocationButton.IsEnabled = true;
        }
    }

    private async Task ShowLocationDialogAsync()
    {
        if (_dialogOpen)
        {
            return;
        }

        var latitudeEditor = CreateCoordinateEditor(
            "Latitude",
            "LatitudeBox_LightSwitch",
            _settings.Latitude,
            CoordinateFormat.LatitudeMin,
            CoordinateFormat.LatitudeMax);
        var longitudeEditor = CreateCoordinateEditor(
            "Longitude",
            "LongitudeBox_LightSwitch",
            _settings.Longitude,
            CoordinateFormat.LongitudeMin,
            CoordinateFormat.LongitudeMax);

        var inputGrid = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 8
        };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
        inputGrid.Children.Add(latitudeEditor);
        inputGrid.Children.Add(longitudeEditor);
        Grid.SetColumn(longitudeEditor, 1);

        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Set the coordinates used to calculate sunrise and sunset times.",
                    TextWrapping = TextWrapping.Wrap
                },
                inputGrid
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Location",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = RootGrid.RequestedTheme
        };

        ContentDialogResult dialogResult;
        _dialogOpen = true;
        try
        {
            dialogResult = await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }

        if (dialogResult != ContentDialogResult.Primary)
        {
            return;
        }

        if (!TryFormatCoordinate(latitudeEditor.Value, CoordinateFormat.LatitudeMin, CoordinateFormat.LatitudeMax, out var latitude)
            || !TryFormatCoordinate(longitudeEditor.Value, CoordinateFormat.LongitudeMin, CoordinateFormat.LongitudeMax, out var longitude))
        {
            ShowStatus("Location coordinates are invalid.", InfoBarSeverity.Warning);
            return;
        }

        ApplyLocationCoordinates(latitude, longitude);
    }

    private static NumberBox CreateCoordinateEditor(
        string header,
        string automationId,
        string currentValue,
        double minimum,
        double maximum)
    {
        var editor = new NumberBox
        {
            Header = header,
            Value = ParseCoordinateOrDefault(currentValue),
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = 0.1,
            LargeChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        AutomationProperties.SetAutomationId(editor, automationId);
        return editor;
    }

    private void ApplyLocationCoordinates(string latitude, string longitude)
    {
        // Classify against the pre-change snapshot here: by the time
        // SaveSettingsFromUi captures `previous`, _settings already carries
        // the new coordinates and the change would look like a no-op.
        UpdateRuntimeSettings(
            _settings with { Latitude = latitude, Longitude = longitude },
            clearManualOverrideIfScheduleChanged: true);

        SaveSettingsFromUi();
    }

    private static double ParseCoordinateOrDefault(string value)
    {
        return CoordinateFormat.TryParse(value, out var coordinate) ? coordinate : 0;
    }

    private static bool TryFormatCoordinate(double value, double minimum, double maximum, out string coordinate)
    {
        if (double.IsNaN(value) || value < minimum || value > maximum)
        {
            coordinate = string.Empty;
            return false;
        }

        coordinate = CoordinateFormat.Format(value);
        return true;
    }

    private static int NumberBoxToInt(NumberBox numberBox)
    {
        return double.IsNaN(numberBox.Value) ? 0 : (int)Math.Round(numberBox.Value);
    }

    private static string FormatAppVersion()
    {
        var version = typeof(App).Assembly.GetName().Version;
        return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string FormatMinute(int minute)
    {
        var normalized = ScheduleMath.NormalizeMinute(minute);
        return $"{normalized / 60:00}:{normalized % 60:00}";
    }

    private static string FormatTheme(bool isLight) => isLight ? "Light" : "Dark";
}
