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
