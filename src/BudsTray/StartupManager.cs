using System.Windows.Forms;
using Microsoft.Win32;

namespace BudsTray;

/// <summary>
/// Registers BudsTray in the current user's Run key so it starts with Windows.
/// This is per-user and needs no administrator rights. The executable path is
/// quoted so folders with spaces work, and the entry is only considered enabled
/// when it points at the running executable.
/// </summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BudsTray";

    public static string ExecutablePath => Application.ExecutablePath;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return Matches(key?.GetValue(ValueName) as string, ExecutablePath);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the Windows startup entry", ex);
            return false;
        }
    }

    /// <summary>
    /// Turns the startup entry on or off and returns its state afterwards. On
    /// failure the previous state is kept and the error is logged.
    /// </summary>
    public static bool Toggle()
    {
        var enable = !IsEnabled();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                            ?? throw new InvalidOperationException("The Run key is unavailable.");

            if (enable)
            {
                key.SetValue(ValueName, BuildCommand(ExecutablePath));
                Log.Info("Start with Windows enabled.");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Start with Windows disabled.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(enable
                ? "Could not enable start with Windows"
                : "Could not disable start with Windows", ex);
        }

        return IsEnabled();
    }

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\"";

    internal static bool Matches(string? registeredCommand, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand)) return false;

        return string.Equals(
            registeredCommand.Trim(),
            BuildCommand(executablePath),
            StringComparison.OrdinalIgnoreCase);
    }
}
