using System.Diagnostics;

namespace PhotoOrganizer;

/// <summary>
/// Lightweight startup timing — mirrors the Python t0 / print(f"[{time()-t0:.2f}s] ...") pattern.
/// Output goes to the VS debugger Output window via Debug.WriteLine.
/// </summary>
internal static class StartupTimer
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();

    public static void Log(string message)
    {
        var msg = $"[{_sw.Elapsed.TotalSeconds:F2}s] {message}";
        Debug.WriteLine(msg);
        Console.WriteLine(msg); // also visible if a console is attached
    }
}
