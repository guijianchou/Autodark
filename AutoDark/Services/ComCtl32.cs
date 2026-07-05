using System.Runtime.InteropServices;

namespace AutoDark.Services;

internal static class ComCtl32
{
    internal delegate nint SubclassProc(
        nint hWnd,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    internal static extern bool SetWindowSubclass(
        nint hWnd,
        SubclassProc subclassProc,
        nuint subclassId,
        nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    internal static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc subclassProc, nuint subclassId);

    [DllImport("comctl32.dll", SetLastError = true)]
    internal static extern nint DefSubclassProc(nint hWnd, uint message, nuint wParam, nint lParam);
}
