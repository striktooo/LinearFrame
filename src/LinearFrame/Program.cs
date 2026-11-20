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

    [STAThread]
    private static int Main(string[] args)
    {
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
