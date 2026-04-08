using System.Diagnostics;

namespace PhotoOrganizer;

/// <summary>
/// Lightweight startup timing. Writes to debugger output AND a log file next to the exe
/// so failures are visible when launched standalone (WinExe hides the console).
/// </summary>
internal static class StartupTimer
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly string _logPath;
    private static readonly object _fileLock = new();

    static StartupTimer()
    {
        // Write log next to the exe so it's easy to find
        var dir = AppContext.BaseDirectory;
        _logPath = Path.Combine(dir, "startup.log");

        // Truncate / create fresh on each launch
        try { File.WriteAllText(_logPath, $"=== PhotoOrganizer started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); }
        catch { /* can't write log — no-op */ }
    }

    public static void Log(string message)
    {
        var msg = $"[{_sw.Elapsed.TotalSeconds:F2}s] {message}";
        Debug.WriteLine(msg);
        Console.WriteLine(msg);

        try
        {
            lock (_fileLock)
                File.AppendAllText(_logPath, msg + "\n");
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Log an exception with full stack trace.</summary>
    public static void LogException(string context, Exception ex)
    {
        Log($"EXCEPTION in {context}: {ex.GetType().Name}: {ex.Message}");
        try
        {
            lock (_fileLock)
                File.AppendAllText(_logPath, ex.StackTrace + "\n");
        }
        catch { }
    }
}
