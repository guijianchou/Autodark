using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutoDark.Services;

public sealed class TrayService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint WmAppTray = 0x8000 + 0x421;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNull = 0x0000;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint OpenCommand = 1001;
    private const uint ToggleCommand = 1002;
    private const uint ForceLightCommand = 1003;
    private const uint ForceDarkCommand = 1004;
    private const uint ExitCommand = 1005;
    private const uint MsgFltAllow = 1;
    private static readonly nuint SubclassId = unchecked((nuint)0x41554454524159UL);
    private const uint IconId = 1;

    private readonly nint _hwnd;
    private readonly nint _iconHandle;
    private readonly bool _ownsIcon;
    private readonly ComCtl32.SubclassProc _subclassProc;
    private readonly uint _taskbarCreatedMessage;
    private bool _disposed;
    private bool _subclassed;
    private bool _iconAdded;

    public TrayService(nint hwnd, string iconPath)
    {
        if (hwnd == nint.Zero)
        {
            throw new ArgumentException("A valid owner window handle is required.", nameof(hwnd));
        }

        _hwnd = hwnd;
        _subclassProc = WndProc;
        _subclassed = ComCtl32.SetWindowSubclass(_hwnd, _subclassProc, SubclassId, nuint.Zero);
        if (!_subclassed)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to subclass the AutoDark window.");
        }

        // Explorer broadcasts this when the taskbar is (re)created; reacting
        // to it restores the icon after a shell crash and covers logon races
        // where the taskbar is not ready when we first add the icon.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage != 0)
        {
            ChangeWindowMessageFilterEx(_hwnd, _taskbarCreatedMessage, MsgFltAllow, nint.Zero);
        }

        _iconHandle = LoadImage(nint.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        _ownsIcon = _iconHandle != nint.Zero;
        if (_iconHandle == nint.Zero)
        {
            _iconHandle = LoadIcon(nint.Zero, new IntPtr(32512));
        }

        // Non-fatal: if the shell is not ready yet, TaskbarCreated re-adds it.
        TryAddIcon();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? ForceLightRequested;
    public event EventHandler? ForceDarkRequested;
    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_iconAdded)
        {
            var data = CreateNotifyIconData();
            ShellNotifyIcon(NimDelete, ref data);
            _iconAdded = false;
        }

        if (_subclassed)
        {
            ComCtl32.RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            _subclassed = false;
        }

        if (_ownsIcon)
        {
            DestroyIcon(_iconHandle);
        }

        _disposed = true;
    }

    private void TryAddIcon()
    {
        // NIM_ADD can fail although the icon exists (shell busy at logon, or
        // a spurious TaskbarCreated while the icon survived); NIM_MODIFY
        // succeeding proves it is present either way.
        var data = CreateNotifyIconData();
        _iconAdded = ShellNotifyIcon(NimAdd, ref data) || ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData CreateNotifyIconData()
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTray,
            hIcon = _iconHandle,
            szTip = "AutoDark",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private nint WndProc(nint hWnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (!_disposed && _taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            TryAddIcon();
        }

        if (message == WmAppTray)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == WmLButtonDoubleClick)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return nint.Zero;
            }

            if (mouseMessage == WmRButtonUp || mouseMessage == WmContextMenu)
            {
                ShowContextMenu();
                return nint.Zero;
            }
        }

        return ComCtl32.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, OpenCommand, "Open AutoDark");
            AppendMenu(menu, MfString, ToggleCommand, "Toggle theme");
            AppendMenu(menu, MfString, ForceLightCommand, "Force light");
            AppendMenu(menu, MfString, ForceDarkCommand, "Force dark");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, "Exit");

            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, _hwnd, nint.Zero);
            DispatchCommand(command);
            PostMessage(_hwnd, WmNull, nuint.Zero, nint.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void DispatchCommand(uint command)
    {
        switch (command)
        {
            case OpenCommand:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ToggleCommand:
                ToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ForceLightCommand:
                ForceLightRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ForceDarkCommand:
                ForceDarkRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ExitCommand:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInstance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern nint LoadIcon(nint hInstance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(nint hmenu, uint fuFlags, int x, int y, nint hwnd, nint lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);
}
