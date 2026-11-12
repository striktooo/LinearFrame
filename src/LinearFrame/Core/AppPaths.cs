namespace LinearFrame.Core;

internal static class AppPaths
{
    public const string AppName = "LinearFrame";

    /// <summary>Settings and small state (roaming).</summary>
    public static string DataDir { get; } = Init(
        Environment.GetEnvironmentVariable("LINEARFRAME_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));

    /// <summary>Logs and caches (local).</summary>
    public static string LocalDir { get; } = Init(
        Environment.GetEnvironmentVariable("LINEARFRAME_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(LocalDir, "log.txt");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppName + ".exe");
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>A development build runs from bin\Debug or bin\Release - such builds never touch autostart.</summary>
    public static bool IsDevBuild =>
        BaseDir.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
        BaseDir.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// LINEARFRAME_INSTANCE=name runs a second, separate copy next to the normal one (own single-instance lock and
    /// message window) - for testing updates and the like without touching the installed app.
    /// </summary>
    public static string Instance { get; } = (Environment.GetEnvironmentVariable("LINEARFRAME_INSTANCE") ?? "").Trim();

    /// <summary>Only the real, installed app registers itself with Windows (autostart, Start menu entry).</summary>
    public static bool RegistersWithWindows => !IsDevBuild && Instance.Length == 0;

    public static string DefaultScreenshotDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);

    /// <summary>Folder holding libmpv-2.dll and ffmpeg.exe.</summary>
    public static string? FindEngineFile(string fileName)
    {
        string[] candidates =
        {
            Path.Combine(BaseDir, "engine", fileName),
            Path.Combine(BaseDir, fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    private static string Init(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* read-only location - ignore */ }
        return dir;
    }
}
