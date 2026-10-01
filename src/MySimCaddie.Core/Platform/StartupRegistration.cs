using Microsoft.Win32;

namespace MySimCaddie.Core.Platform;

/// <summary>Start-with-Windows via HKCU\...\Run (per user, no admin needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MySimCaddie";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{exePath}\" --autostart");
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName);
        }
        catch (Exception ex)
        {
            Log.Error("Could not update start-with-Windows", ex);
        }
    }
}
