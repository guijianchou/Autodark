using Microsoft.Win32;

namespace AutoDark.Services;

public sealed class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoDark";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: false);
        if (key?.GetValue(ValueName) is not string value)
        {
            return false;
        }

        // A stale entry from a moved/renamed install must read as Off, so
        // re-enabling the toggle rewrites the value to the current path
        // instead of leaving autostart silently broken.
        var exePath = Environment.ProcessPath;
        return string.IsNullOrEmpty(exePath)
            ? value.Contains("AutoDark", StringComparison.OrdinalIgnoreCase)
            : value.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve AutoDark executable path.");
        key.SetValue(ValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
    }
}
