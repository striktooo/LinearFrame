namespace LinearFrame.Core;

/// <summary>Tiny append-only log (rotates at 1 MB). Never throws.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERR ", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = AppPaths.LogFile;
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    var old = file + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
