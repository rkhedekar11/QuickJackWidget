using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace QuickJack.App.Services;

/// <summary>
/// Start-with-Windows, via the per-user Run key. HKCU needs no elevation — using the
/// machine-wide key or a scheduled task would turn a checkbox into a UAC prompt.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickJack";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains("QuickJack");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static bool TrySet(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var path = ExecutablePath();
                if (path is null) return false;
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static string? ExecutablePath()
    {
        // MainModule gives the real .exe. Assembly.Location points at the .dll under
        // `dotnet run`, which Windows cannot launch on its own.
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Environment.ProcessPath;
        }
    }
}
