using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BudsTray.Buds;

/// <summary>
/// Samsung's official Galaxy Buds package keeps two background processes:
/// <c>QuickControls.exe</c> (the full-trust startup task that owns the Bluetooth
/// connection) and <c>BudsManager.exe</c> (the UWP front end, which the package's
/// app service restarts whenever QuickControls asks for it). The SPP/RFCOMM
/// channel is exclusive, so BudsTray has to stop both before it can connect;
/// stopping only BudsManager is undone by QuickControls in a fraction of a second.
/// </summary>
public static class GalaxyBudsApp
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Processes to stop, in order: QuickControls first, because it restarts
    /// BudsManager through the package's app service.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = new[] { "QuickControls", "BudsManager" };

    /// <summary>
    /// True while the official app's window is visible. BudsTray releases the
    /// channel in that state so the app can connect; it does not kill a window the
    /// user deliberately opened. UWP windows are hosted by
    /// <c>ApplicationFrameHost</c>, hence the host check: a browser tab whose title
    /// mentions "Galaxy Buds" must not pause the tray.
    /// </summary>
    public static bool IsWindowOpen()
    {
        var open = false;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (open || !IsWindowVisible(hWnd)) return true;

                var title = new StringBuilder(256);
                if (GetWindowText(hWnd, title, title.Capacity) <= 0) return true;
                if (!title.ToString().Contains("Buds", StringComparison.OrdinalIgnoreCase)) return true;

                GetWindowThreadProcessId(hWnd, out var processId);
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    if (process.ProcessName is "ApplicationFrameHost" or "BudsManager" or "QuickControls")
                        open = true;
                }
                catch
                {
                    // The process ended while the windows were being enumerated.
                }

                return !open;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Could not check whether the Galaxy Buds app is open", ex);
        }

        return open;
    }

    /// <summary>True when either background process of the official app is running.</summary>
    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Any(IsGalaxyBudsProcess)) return true;
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not inspect {name}", ex);
            }
        }

        return false;
    }

    /// <summary>
    /// Stops the official app's background processes so the SPP channel becomes
    /// free. Returns the process names that were actually stopped. Safe to call
    /// when the app is not running: it then returns an empty list.
    /// </summary>
    public static IReadOnlyList<string> Stop()
    {
        var stopped = new List<string>();

        // Two passes: QuickControls can spawn BudsManager (or be restarted by
        // the startup task) while the first pass is still running.
        for (var pass = 0; pass < 2; pass++)
        {
            var processes = FindProcesses();
            if (processes.Count == 0) break;

            foreach (var process in processes)
            {
                var name = SafeName(process);
                try
                {
                    process.Kill();
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                    if (!stopped.Contains(name)) stopped.Add(name);
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and the kill.
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not stop the official Galaxy Buds process {name}", ex);
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (pass == 0) Thread.Sleep(250);
        }

        return stopped;
    }

    private static List<Process> FindProcesses()
    {
        var found = new List<Process>();
        foreach (var name in ProcessNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (IsGalaxyBudsProcess(process)) found.Add(process);
                    else process.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not enumerate {name}", ex);
            }
        }

        return found;
    }

    /// <summary>
    /// Guards against stopping an unrelated process that happens to share the
    /// name: the executable must live in the GalaxyBuds package folder.
    /// </summary>
    private static bool IsGalaxyBudsProcess(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return path != null && path.Contains("GalaxyBuds", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Protected process we cannot inspect: do not touch it.
            return false;
        }
    }

    private static string SafeName(Process process)
    {
        try { return process.ProcessName; }
        catch { return "Galaxy Buds app"; }
    }
}
