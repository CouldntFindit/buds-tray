using Microsoft.Win32;

namespace BudsTray;

public static class ThemeUtil
{
    /// <summary>Windows 11 reports build 22000 or higher.</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsLight(AppTheme theme) => theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => IsLightTheme()
    };
}
