using System.IO;

namespace BudsTray;

/// <summary>
/// Keeps the app's data (config, logs) next to the executable when that folder
/// is writable, so the whole project stays self-contained. Falls back to
/// %APPDATA%\BudsTray for read-only install locations.
/// </summary>
public static class PortablePaths
{
    private static string? _dataDirectory;

    public static string DataDirectory => _dataDirectory ??= Resolve();

    private static string Resolve()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("BUDS_TRAY_DATA");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            Directory.CreateDirectory(overrideDirectory);
            return overrideDirectory;
        }

        var local = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return local;
        }
        catch
        {
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BudsTray");
            Directory.CreateDirectory(roaming);
            return roaming;
        }
    }
}
