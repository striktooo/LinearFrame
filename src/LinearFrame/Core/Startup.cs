using System.Diagnostics;
using System.Globalization;

namespace LinearFrame.Core;

/// <summary>
/// One copy of LinearFrame per signed-in user. Uses nothing but the lock and a window message, so the check costs a
/// second start (a file opened from Explorer) next to nothing.
/// </summary>
internal static class SingleInstance
{
    public static string LockName => @"Local\LinearFrame.SingleInstance" + AppPaths.Instance;

    /// <summary>Another copy already holds the lock.</summary>
    public static bool RunsAlready()
    {
        try
        {
            if (!Mutex.TryOpenExisting(LockName, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch { return false; } // can't tell: the normal start decides
    }

    /// <summary>Hands the command line over to the running copy (it may still be starting up: a few tries).</summary>
    public static bool HandOver(string[] args)
    {
        for (int i = 0; i < 10; i++)
        {
            if (Ipc.TrySendToRunningInstance(args)) return true;
            Thread.Sleep(200);
        }
        return false;
    }
}

/// <summary>Where the time of a start goes - one log line that tells a slow Windows start from a slow LinearFrame.</summary>
internal static class StartupTrace
{
    private static readonly List<string> Steps = new();
    private static long _begin, _last;

    public static void Begin() => _begin = _last = Stopwatch.GetTimestamp();

    /// <summary>The step that just ended (time since the previous mark).</summary>
    public static void Mark(string step)
    {
        long now = Stopwatch.GetTimestamp();
        Steps.Add(string.Create(CultureInfo.InvariantCulture, $"{step} {Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds:0}"));
        _last = now;
    }

    /// <summary>Milliseconds since the process was created, and how they were spent ("before Main" = Windows and .NET).</summary>
    public static (double Total, string Steps) Summary()
    {
        double sinceMain = _begin == 0 ? 0 : Stopwatch.GetElapsedTime(_begin).TotalMilliseconds;
        double total = sinceMain;
        try
        {
            using var self = Process.GetCurrentProcess();
            total = (DateTime.Now - self.StartTime).TotalMilliseconds;
        }
        catch { /* only for the log */ }
        string before = string.Create(CultureInfo.InvariantCulture, $"before Main {Math.Max(0, total - sinceMain):0}");
        return (total, string.Join(", ", Steps.Prepend(before)));
    }
}
