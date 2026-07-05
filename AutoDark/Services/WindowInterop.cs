using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AutoDark.Services;

public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

public static class WindowInterop
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcDestroy = 0x0082;
    private const uint WmTimeChange = 0x001E;
    private const uint WmPowerBroadcast = 0x0218;
    private const nuint PbtApmResumeSuspend = 0x7;
    private const nuint PbtApmResumeAutomatic = 0x12;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly nuint MinimumSizeSubclassId = unchecked((nuint)0x4155444D494EUL);
    private static readonly ComCtl32.SubclassProc MinimumSizeSubclassProc = OnMinimumSizeSubclass;
    private static readonly Dictionary<nint, MinimumSize> MinimumSizes = [];
    private static readonly object MinimumSizesLock = new();
    private static readonly nuint ScheduleRefreshSubclassId = unchecked((nuint)0x415544524653UL);
    private static readonly ComCtl32.SubclassProc ScheduleRefreshSubclassProc = OnScheduleRefreshSubclass;
    private static readonly Dictionary<nint, Action> ScheduleRefreshCallbacks = [];
    private static readonly object ScheduleRefreshLock = new();

    public static nint GetWindowHandle(Window window) => WindowNative.GetWindowHandle(window);

    public static void Hide(Window window) => ShowWindow(GetWindowHandle(window), SwHide);

    public static void Show(Window window) => ShowWindow(GetWindowHandle(window), SwShow);

    public static void Restore(Window window) => ShowWindow(GetWindowHandle(window), SwRestore);

    public static bool IsMinimized(Window window) => IsIconic(GetWindowHandle(window));

    public static bool TryGetWindowRect(Window window, out WindowBounds bounds)
    {
        var hwnd = GetWindowHandle(window);
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect))
        {
            bounds = default;
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            bounds = default;
            return false;
        }

        bounds = new WindowBounds(rect.Left, rect.Top, width, height);
        return true;
    }

    public static WindowBounds ClampToVisibleWorkArea(WindowBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return bounds;
        }

        var rect = new Rect
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Left + bounds.Width,
            Bottom = bounds.Top + bounds.Height
        };
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return bounds;
        }

        var monitorInfo = MonitorInfo.Create();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return bounds;
        }

        var workArea = monitorInfo.WorkArea;
        var workWidth = workArea.Right - workArea.Left;
        var workHeight = workArea.Bottom - workArea.Top;
        if (workWidth <= 0 || workHeight <= 0)
        {
            return bounds;
        }

        var width = Math.Min(bounds.Width, workWidth);
        var height = Math.Min(bounds.Height, workHeight);
        var left = Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        var top = Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);

        return new WindowBounds(left, top, width, height);
    }

    public static bool TrySetWindowRect(Window window, WindowBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var hwnd = GetWindowHandle(window);
        return hwnd != 0
            && SetWindowPos(
                hwnd,
                0,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SwpNoZOrder | SwpNoActivate);
    }

    public static bool SetMinimumSize(Window window, int width, int height)
    {
        var hwnd = GetWindowHandle(window);
        var needsSubclass = false;

        lock (MinimumSizesLock)
        {
            needsSubclass = !MinimumSizes.ContainsKey(hwnd);
            MinimumSizes[hwnd] = new MinimumSize(width, height);
        }

        if (needsSubclass && !ComCtl32.SetWindowSubclass(hwnd, MinimumSizeSubclassProc, MinimumSizeSubclassId, nuint.Zero))
        {
            lock (MinimumSizesLock)
            {
                MinimumSizes.Remove(hwnd);
            }

            return false;
        }

        return true;
    }

    public static void ClearMinimumSize(Window window)
    {
        var hwnd = GetWindowHandle(window);

        lock (MinimumSizesLock)
        {
            MinimumSizes.Remove(hwnd);
        }

        ComCtl32.RemoveWindowSubclass(hwnd, MinimumSizeSubclassProc, MinimumSizeSubclassId);
    }

    public static double GetDpiScale(Window window)
    {
        var dpi = GetDpiForWindow(GetWindowHandle(window));
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>
    /// Invokes <paramref name="callback"/> on the window's thread when the
    /// system resumes from sleep or the clock/timezone changes, so the
    /// schedule can re-evaluate immediately instead of waiting for a tick.
    /// </summary>
    public static bool RegisterScheduleRefreshEvents(Window window, Action callback)
    {
        var hwnd = GetWindowHandle(window);
        bool needsSubclass;

        lock (ScheduleRefreshLock)
        {
            needsSubclass = !ScheduleRefreshCallbacks.ContainsKey(hwnd);
            ScheduleRefreshCallbacks[hwnd] = callback;
        }

        if (needsSubclass && !ComCtl32.SetWindowSubclass(hwnd, ScheduleRefreshSubclassProc, ScheduleRefreshSubclassId, nuint.Zero))
        {
            lock (ScheduleRefreshLock)
            {
                ScheduleRefreshCallbacks.Remove(hwnd);
            }

            return false;
        }

        return true;
    }

    public static void UnregisterScheduleRefreshEvents(Window window)
    {
        var hwnd = GetWindowHandle(window);

        lock (ScheduleRefreshLock)
        {
            ScheduleRefreshCallbacks.Remove(hwnd);
        }

        ComCtl32.RemoveWindowSubclass(hwnd, ScheduleRefreshSubclassProc, ScheduleRefreshSubclassId);
    }

    private static nint OnScheduleRefreshSubclass(
        nint hWnd,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint refData)
    {
        var refresh = message == WmTimeChange
            || (message == WmPowerBroadcast && (wParam == PbtApmResumeSuspend || wParam == PbtApmResumeAutomatic));

        if (refresh)
        {
            Action? callback;
            lock (ScheduleRefreshLock)
            {
                ScheduleRefreshCallbacks.TryGetValue(hWnd, out callback);
            }

            callback?.Invoke();
        }

        if (message == WmNcDestroy)
        {
            lock (ScheduleRefreshLock)
            {
                ScheduleRefreshCallbacks.Remove(hWnd);
            }

            ComCtl32.RemoveWindowSubclass(hWnd, ScheduleRefreshSubclassProc, ScheduleRefreshSubclassId);
        }

        return ComCtl32.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    public static void SetTitleBarTheme(Window window, bool isLightTheme)
    {
        var hwnd = GetWindowHandle(window);
        var useDarkMode = isLightTheme ? 0 : 1;

        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDarkMode, Marshal.SizeOf<int>());
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    private static nint OnMinimumSizeSubclass(
        nint hWnd,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint refData)
    {
        if (message == WmGetMinMaxInfo && lParam != 0)
        {
            MinimumSize minimumSize;
            var hasMinimumSize = false;
            lock (MinimumSizesLock)
            {
                hasMinimumSize = MinimumSizes.TryGetValue(hWnd, out minimumSize);
            }

            if (hasMinimumSize)
            {
                var dpi = GetDpiForWindow(hWnd);
                var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                minMaxInfo.MinTrackSize.X = ScaleForDpi(minimumSize.Width, dpi);
                minMaxInfo.MinTrackSize.Y = ScaleForDpi(minimumSize.Height, dpi);
                Marshal.StructureToPtr(minMaxInfo, lParam, false);
                return 0;
            }
        }

        if (message == WmNcDestroy)
        {
            lock (MinimumSizesLock)
            {
                MinimumSizes.Remove(hWnd);
            }

            ComCtl32.RemoveWindowSubclass(hWnd, MinimumSizeSubclassProc, MinimumSizeSubclassId);
        }

        return ComCtl32.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static int ScaleForDpi(int value, uint dpi) => (int)Math.Ceiling(value * dpi / 96.0);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int attributeValue, int attributeSize);

    private readonly record struct MinimumSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new() { Size = Marshal.SizeOf<MonitorInfo>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }
}
