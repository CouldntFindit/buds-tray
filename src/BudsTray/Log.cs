using System.IO;

namespace BudsTray;

public static class Log
{
    private static readonly object Gate = new();

    private static string DirectoryPath => PortablePaths.DataDirectory;

    public static string FilePath => Path.Combine(DirectoryPath, "budstray.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000) file.Delete();
                File.AppendAllText(FilePath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
