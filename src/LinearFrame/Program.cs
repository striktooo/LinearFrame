using LinearFrame.Core;

namespace LinearFrame;

/// <summary>
/// The entry point. A second start while LinearFrame already runs - a file opened from Explorer, the Start menu entry,
/// "Trim with LinearFrame" - only has to hand its command line over to the running copy: that is done right here,
/// before any of the UI libraries are loaded, so a double-clicked video reaches the player without that detour.
/// </summary>
internal static class Program
{
    /// <summary>Commands that run on their own (no single-instance hand-over): the elevated HD set-up helper and the dev tools.</summary>
    internal static bool IsStandaloneCommand(string[] args) => args.Length > 0 && args[0] is "--hd-setup" or "--selftest" or "--make-icon"
        or "--icon-preview" or "--qr-test" or "--ocr-test" or "--ocr-file" or "--ocr-exp" or "--render-ui" or "--marketing" or "--compose";

    /// <summary>
    /// What build.ps1 asks the finished package: does it start at all, and on the .NET in its own "runtime" folder?
    /// (Exit code 0 = yes. No window, no single-instance lock, nothing written.)
    /// </summary>
    private static int RuntimeCheck()
    {
        string own = Path.Combine(AppContext.BaseDirectory, "runtime") + Path.DirectorySeparatorChar;
        bool Ours(Type t) => t.Assembly.Location.StartsWith(own, StringComparison.OrdinalIgnoreCase);
        return Ours(typeof(object)) && Ours(typeof(System.Windows.Window)) ? 0 : 3;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--runtime-check") return RuntimeCheck();
        StartupTrace.Begin();
        if (!IsStandaloneCommand(args) && SingleInstance.RunsAlready())
        {
            SingleInstance.HandOver(args);
            return 0;
        }
        StartupTrace.Mark("runtime");
        var app = new App();
        app.InitializeComponent();
        StartupTrace.Mark("app");
        return app.Run();
    }
}
