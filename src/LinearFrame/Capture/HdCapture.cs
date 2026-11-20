using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using LinearFrame.Core;
using LinearFrame.Imaging;

namespace LinearFrame.Capture;

/// <summary>Why a capture could not be re-rendered in HD (it then stays a normal 1× screenshot).</summary>
public enum HdFailure { None, NotInstalled, Desktop, SeveralWindows, LegacyApp, Admin, Fullscreen, NotResponding, NoScreen, Moved, Changed, NoGain, Error }

/// <summary>Outcome of an HD attempt: the real high-resolution picture, or the reason there is none.</summary>
public sealed record HdResult(PixelBuffer? Image, double Scale, HdFailure Failure, string Detail);

/// <summary>
/// "HD capture": the window under the selection is moved for a fraction of a second onto the off-screen virtual screen,
/// which runs at 200–400 % scaling. Windows then asks the app to draw itself for that scaling - exactly as if you had
/// dragged the window onto a 4K/8K monitor - so text, icons and vector UI are really rendered with 2–4× the pixels.
/// That picture is captured and the window goes back to where it was. The frozen overlay hides all of it.
/// Nothing is ever upscaled: if the app doesn't produce a sharper picture, the normal screenshot is used instead.
/// </summary>
internal static class HdCapture
{
    private static readonly object Gate = new();

    /// <summary>How different (0–255, blurred greys) the re-render may be from the frozen screenshot and still count as "the same thing".</summary>
    internal const double MatchLimit = 15;
    /// <summary>...and how alike its structure has to be (brightness-independent; the same content scores about 0.9).</summary>
    internal const double MinSimilarity = 0.6;
    internal static double GainGood(double k) => 1 + 0.42 * (k - 1); // clearly re-rendered
    internal static double GainMin(double k) => 1 + 0.18 * (k - 1);  // at least something gained
    private const int SearchRadius = 14; // logical pixels the content may have shifted

    /// <summary>
    /// The factor actually used for a selection: the wanted one for small things, less for big selections
    /// (a whole window at 4× would be a 30-megapixel picture - 2× or 3× is plenty there and much faster).
    /// </summary>
    public static int ScaleFor(int width, int height, int wanted)
    {
        const double MaxPixels = 20_000_000;
        int fits = (int)Math.Floor(Math.Sqrt(MaxPixels / Math.Max(1.0, (double)width * height)));
        return Math.Clamp(Math.Min(wanted, fits), 2, 4);
    }

    /// <param name="windows">top-level windows at the moment the screen was frozen, front first</param>
    /// <param name="monitor">the monitor the selection is on</param>
    /// <param name="selection">selection in that monitor's physical pixels</param>
    /// <param name="frozen">the selection as it was captured (1×)</param>
    /// <param name="wantedScale">2, 3 or 4</param>
    public static HdResult Render(IReadOnlyList<WindowEntry> windows, MonitorInfo monitor, Int32Rect selection, PixelBuffer frozen, int wantedScale)
    {
        lock (Gate)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var mb = monitor.Bounds;
                var r = RenderOn(windows, monitor, selection, frozen, wantedScale,
                    (n, percent, settle) => VirtualScreen.Arm(mb.Width * n, mb.Height * n, percent, settle), sameScaleOk: false, sw);
                Log.Info($"HD capture: {(r.Image != null ? $"{r.Image.Width}x{r.Image.Height} at {r.Scale:0.##}×" : "no - " + r.Failure)} in {sw.ElapsedMilliseconds} ms ({r.Detail})");
                return r;
            }
            catch (Exception ex)
            {
                Log.Error("HD capture failed", ex);
                return new HdResult(null, 1, HdFailure.Error, ex.Message);
            }
            finally { VirtualScreen.ParkSoon(); }
        }
    }

    private static HdResult Fail(HdFailure f, string detail = "") => new(null, 1, f, detail);

    private const uint Quiet = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNCWINDOWPOS;

    /// <summary>Dev only: gets the last picture looked at when a re-render didn't match.</summary>
    internal static Action<PixelBuffer>? DebugLastLook;

    /// <summary>The window that owns the whole selection - or the reason there is no single such window.</summary>
    internal static (WindowEntry? Window, HdFailure Failure) PickWindow(IReadOnlyList<WindowEntry> windows, Int32Rect sel)
    {
        foreach (var w in windows) // front first
        {
            var hit = ImageOps.Intersect(w.Bounds, sel);
            if (hit.Width <= 0 || hit.Height <= 0) continue;
            // the front-most window that touches the selection has to contain all of it
            bool inside = sel.X >= w.Bounds.X && sel.Y >= w.Bounds.Y
                && sel.X + sel.Width <= w.Bounds.X + w.Bounds.Width && sel.Y + sel.Height <= w.Bounds.Y + w.Bounds.Height;
            return inside ? (w, HdFailure.None) : (null, HdFailure.SeveralWindows);
        }
        return (null, HdFailure.Desktop);
    }

    /// <summary>Parts of the Windows shell that are tied to their monitor (taskbar, Start, notification area…).</summary>
    private static bool IsShellSurface(IntPtr hwnd)
    {
        string cls = Native.ClassName(hwnd);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "XamlExplorerHostIslandWindow"
            or "Shell_InputSwitchTopLevelWindow" or "TaskListThumbnailWnd" or "MultitaskingViewFrame") return true;
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName.ToLowerInvariant();
            if (name is "startmenuexperiencehost" or "searchhost" or "shellexperiencehost" or "textinputhost") return true;
            if (name == "explorer" && cls is not ("CabinetWClass" or "ExploreWClass" or "#32770")) return true;
        }
        catch { /* can't tell - try anyway */ }
        return false;
    }

    /// <param name="arm">gets the screen to render on for a factor and a scaling percentage, its work area final or not
    /// (null = not possible)</param>
    /// <param name="sameScaleOk">self-test only: accept a target with the same scaling (checks the mechanics, gains no detail)</param>
    internal static HdResult RenderOn(IReadOnlyList<WindowEntry> windows, MonitorInfo monitor, Int32Rect selection, PixelBuffer frozen,
        int wantedScale, Func<int, int, bool, VirtualScreen.Armed?> arm, bool sameScaleOk, Stopwatch sw)
    {
        var sel = new Int32Rect(monitor.Bounds.X + selection.X, monitor.Bounds.Y + selection.Y, selection.Width, selection.Height);
        var (entry, why) = PickWindow(windows, sel);
        if (entry == null) return Fail(why);
        IntPtr hwnd = entry.Handle;
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return Fail(HdFailure.Changed, "window gone");
        if (IsShellSurface(hwnd)) return Fail(HdFailure.Desktop, Native.ClassName(hwnd));
        if (Native.IsHungAppWindow(hwnd)) return Fail(HdFailure.NotResponding);
        int awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(hwnd));
        if (awareness != 2) return Fail(HdFailure.LegacyApp, $"dpi awareness {awareness}");
        var mb = monitor.Bounds;
        bool coversMonitor = entry.Bounds.X <= mb.X && entry.Bounds.Y <= mb.Y
            && entry.Bounds.X + entry.Bounds.Width >= mb.X + mb.Width && entry.Bounds.Y + entry.Bounds.Height >= mb.Y + mb.Height;
        if (coversMonitor && !Native.IsZoomed(hwnd)) return Fail(HdFailure.Fullscreen);

        if (!Native.GetWindowRect(hwnd, out var r0)) return Fail(HdFailure.Error, "no window rect");
        uint dpi0 = Native.GetDpiForWindow(hwnd);
        if (dpi0 == 0) dpi0 = (uint)Math.Round(monitor.Scale * 96);
        int srcPercent = (int)Math.Round(dpi0 * 100 / 96.0);
        bool maximized = Native.IsZoomed(hwnd);

        // the virtual screen: big enough for this monitor at N×, scaled N× - try the wanted factor, then smaller ones
        VirtualScreen.Armed? screen = null;
        int lowest = sameScaleOk ? 1 : 2;
        if (!sameScaleOk) wantedScale = ScaleFor(selection.Width, selection.Height, wantedScale);
        for (int n = Math.Clamp(wantedScale, lowest, 5); n >= lowest && screen == null; n--)
        {
            int percent = srcPercent * n;
            if (percent > 500 || !Displays.IsStep(percent)) continue;
            screen = arm(n, percent, maximized);
            if (screen != null && !sameScaleOk && screen.Dpi <= dpi0) screen = null;
        }
        if (screen == null) return Fail(VirtualScreen.Find() == null ? HdFailure.NotInstalled : HdFailure.NoScreen);
        double k = screen.Dpi / (double)dpi0;
        long armedMs = sw.ElapsedMilliseconds;

        // where the window goes: the same place relative to its screen, k times further out
        var vb = screen.Bounds;
        int dstX = vb.X + (int)Math.Round((r0.Left - mb.X) * k), dstY = vb.Y + (int)Math.Round((r0.Top - mb.Y) * k);
        int dstW = (int)Math.Round(r0.Width * k), dstH = (int)Math.Round(r0.Height * k);
        // keep the selection clear of a taskbar that may sit on the virtual screen
        var wa = screen.WorkArea;
        double selRight = dstX + (sel.X + sel.Width - r0.Left) * k, selBottom = dstY + (sel.Y + sel.Height - r0.Top) * k;
        if (selBottom > wa.Y + wa.Height) dstY -= (int)Math.Ceiling(selBottom - (wa.Y + wa.Height));
        if (selRight > wa.X + wa.Width) dstX -= (int)Math.Ceiling(selRight - (wa.X + wa.Width));
        double selLeft = dstX + (sel.X - r0.Left) * k, selTop = dstY + (sel.Y - r0.Top) * k;
        if (selTop < wa.Y) dstY += (int)Math.Ceiling(wa.Y - selTop);
        if (selLeft < wa.X) dstX += (int)Math.Ceiling(wa.X - selLeft);

        // A maximized window goes exactly over the virtual screen's work area, sticking out past it as much as it did on
        // its own screen (k times more) - that is where Windows itself keeps a maximized window on that screen.
        if (maximized)
        {
            // over the virtual screen's work area, sticking out past it as much as it did on its own screen (k times more)
            var mwa = monitor.WorkArea;
            int ol = mwa.X - r0.Left, ot = mwa.Y - r0.Top, orr = r0.Right - (mwa.X + mwa.Width), ob = r0.Bottom - (mwa.Y + mwa.Height);
            dstX = wa.X - (int)Math.Round(ol * k);
            dstY = wa.Y - (int)Math.Round(ot * k);
            dstW = wa.Width + (int)Math.Round((ol + orr) * k);
            dstH = wa.Height + (int)Math.Round((ot + ob) * k);
        }

        bool moved = false, sized, placed;
        try
        {
            // 1) only move: Windows notices the new scaling and tells the app (WM_DPICHANGED), which resizes itself
            if (!Native.SetWindowPos(hwnd, IntPtr.Zero, dstX, dstY, 0, 0, Quiet | Native.SWP_NOSIZE))
            {
                int err = Marshal.GetLastWin32Error();
                return Fail(err == 5 ? HdFailure.Admin : HdFailure.Error, $"move failed ({err})");
            }
            moved = true;
            if (!WaitFor(() => Native.GetDpiForWindow(hwnd) == screen.Dpi, 900))
                return Fail(HdFailure.Moved, $"dpi stayed {Native.GetDpiForWindow(hwnd)}");
            sized = WaitFor(() => Native.GetWindowRect(hwnd, out var r) && Math.Abs(r.Width - dstW) <= dstW / 40 + 4 && Math.Abs(r.Height - dstH) <= dstH / 40 + 4, 500);
            // 2) the exact place and size
            Native.SetWindowPos(hwnd, IntPtr.Zero, dstX, dstY, dstW, dstH, Quiet);
            placed = WaitFor(() => Native.GetWindowRect(hwnd, out var r) && r.Left == dstX && r.Top == dstY && r.Width == dstW && r.Height == dstH, 400);
            long movedMs = sw.ElapsedMilliseconds;

            var result = WaitForRender(hwnd, screen, sel, r0, k, frozen, sameScaleOk, new Int32Rect(dstX, dstY, dstW, dstH), out string detail);
            detail = $"screen {armedMs} ms, move{(maximized ? " (maximized)" : "")} {movedMs - armedMs} ms{(placed ? "" : sized ? " (not exactly placed)" : " (size not taken)")}, render {sw.ElapsedMilliseconds - movedMs} ms; {detail}";
            return result with { Detail = detail };
        }
        finally
        {
            if (moved) Restore(hwnd, r0, dpi0);
        }
    }

    /// <summary>Polls the virtual screen until the window shows the same content as the frozen screenshot, really re-rendered.</summary>
    private static HdResult WaitForRender(IntPtr hwnd, VirtualScreen.Armed screen, Int32Rect sel, Native.RECT r0, double k, PixelBuffer frozen, bool sameScaleOk, Int32Rect planned, out string detail)
    {
        var frozenGray = ImageMatch.ToGray(frozen);
        var frozenBlur = ImageMatch.Blur(frozenGray);
        int marginHd = (int)Math.Ceiling((SearchRadius + 2) * k);
        var vb = screen.Bounds;

        PixelBuffer? best = null, lastShot = null; double bestGain = double.NaN, bestDiff = 255; bool matched = false, nudged = false;
        ulong lastPrint = 0; int stable = 0, looks = 0;
        var clock = Stopwatch.StartNew();
        const int Deadline = 2600;
        while (clock.ElapsedMilliseconds < Deadline)
        {
            Thread.Sleep(looks == 0 ? 60 : 45);
            if (!Native.GetWindowRect(hwnd, out var now)) break;
            // where the selection is expected in the re-rendered window
            int ex = now.Left + (int)Math.Round((sel.X - r0.Left) * k), ey = now.Top + (int)Math.Round((sel.Y - r0.Top) * k);
            int ew = (int)Math.Round(sel.Width * k), eh = (int)Math.Round(sel.Height * k);
            var area = ImageOps.Intersect(new Int32Rect(ex - marginHd, ey - marginHd, ew + 2 * marginHd, eh + 2 * marginHd), vb);
            if (area.Width < ew || area.Height < eh)
            {
                uint dpiNow = Native.GetDpiForWindow(hwnd);
                string where = $"window at {now.Left},{now.Top} {now.Width}x{now.Height} (planned {planned.X},{planned.Y} {planned.Width}x{planned.Height}), " +
                               $"selection {ex},{ey} {ew}x{eh}, screen {vb.X},{vb.Y} {vb.Width}x{vb.Height}, dpi {dpiNow}";
                if (dpiNow != screen.Dpi) { detail = "the window went back to a normal screen: " + where; return Fail(HdFailure.Moved, detail); }
                if (!nudged)
                {
                    // the app put itself somewhere else than asked (or kept another size): once, slide the window so the
                    // selection is on the virtual screen, from where the window really is
                    nudged = true;
                    Log.Info("HD capture: selection off the virtual screen, sliding the window: " + where);
                    var wa = screen.WorkArea;
                    int dx = ex < wa.X ? wa.X - ex : ex + ew > wa.X + wa.Width ? wa.X + wa.Width - (ex + ew) : 0;
                    int dy = ey < wa.Y ? wa.Y - ey : ey + eh > wa.Y + wa.Height ? wa.Y + wa.Height - (ey + eh) : 0;
                    int nx = now.Left + dx, ny = now.Top + dy;
                    Native.SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0, Quiet | Native.SWP_NOSIZE);
                    WaitFor(() => Native.GetWindowRect(hwnd, out var r) && r.Left == nx && r.Top == ny, 400);
                    continue;
                }
                detail = "selection outside the virtual screen: " + where;
                return Fail(HdFailure.Error, detail);
            }

            var shot = ScreenCapturer.Capture(area, quiet: true);
            lastShot = shot;
            looks++;
            ulong print = ImageMatch.Fingerprint(shot);
            stable = print == lastPrint ? stable + 1 : 0;
            lastPrint = print;
            // judge a picture once it has stopped changing (or every look after a while - animated content never settles)
            if (stable < 1 && clock.ElapsedMilliseconds < 900) continue;

            var v = Judge(shot, ex - area.X, ey - area.Y, ew, eh, k, frozenGray, frozenBlur);
            if (v.Difference < bestDiff) bestDiff = v.Difference;
            if (!v.Match) continue; // not (yet) showing what was selected

            matched = true;
            var crop = v.Crop!;
            double gain = v.Gain;
            if (double.IsNaN(gain) || gain >= GainGood(k) || sameScaleOk)
            {
                detail = $"{looks} looks, difference {v.Difference:0.0}, alike {v.Similarity:0.00}, shift {v.Dx},{v.Dy}, detail gain {(double.IsNaN(gain) ? "n/a (flat)" : gain.ToString("0.00"))}";
                return new HdResult(crop, k, HdFailure.None, detail);
            }
            if (double.IsNaN(bestGain) || gain > bestGain) { bestGain = gain; best = crop; }
            // matching content but (still) a stretched bitmap: the app may not have redrawn yet - keep looking for a while
            if (clock.ElapsedMilliseconds > 1500 && stable >= 2) break;
        }

        if (best != null && bestGain >= GainMin(k))
        {
            detail = $"{looks} looks, accepted late, detail gain {bestGain:0.00}";
            return new HdResult(best, k, HdFailure.None, detail);
        }
        if (lastShot != null) DebugLastLook?.Invoke(lastShot);
        Native.GetWindowRect(hwnd, out var end);
        detail = (matched ? $"{looks} looks, detail gain only {bestGain:0.00}" : $"{looks} looks, best difference {bestDiff:0.0}") +
                 $"; window at {end.Left},{end.Top} {end.Width}x{end.Height} (planned {planned.X},{planned.Y} {planned.Width}x{planned.Height}), dpi {Native.GetDpiForWindow(hwnd)}";
        return Fail(matched ? HdFailure.NoGain : HdFailure.Changed, detail);
    }

    internal readonly record struct Verdict(bool Match, double Difference, double Similarity, int Dx, int Dy, double Gain, PixelBuffer? Crop);

    /// <summary>
    /// Looks at one picture of the re-rendered window: is the selection in it (and where exactly), and does it carry
    /// real extra detail? <paramref name="expectX"/>/<paramref name="expectY"/> = where the selection should start in
    /// <paramref name="shot"/>, <paramref name="width"/>×<paramref name="height"/> = its size there (all in HD pixels).
    /// </summary>
    internal static Verdict Judge(PixelBuffer shot, int expectX, int expectY, int width, int height, double k,
        ImageMatch.Gray frozenGray, ImageMatch.Gray frozenBlur)
    {
        int n = (int)Math.Round(k);
        var small = Math.Abs(k - n) < 0.01
            ? ImageMatch.Shrink(ImageMatch.ToGray(shot), n)
            : ImageMatch.ToGray(ImageOps.Resize(shot, Math.Max(1, (int)Math.Round(shot.Width / k)), Math.Max(1, (int)Math.Round(shot.Height / k))));
        small = ImageMatch.Blur(small);
        int lx = (int)Math.Round(expectX / k), ly = (int)Math.Round(expectY / k);
        var loc = ImageMatch.Locate(small, frozenBlur, lx, ly, SearchRadius);
        double alike = ImageMatch.Correlation(small, frozenBlur, lx + loc.Dx, ly + loc.Dy);
        if (loc.Difference > MatchLimit || alike < MinSimilarity) // (NaN = flat on both sides: nothing to object to)
            return new Verdict(false, loc.Difference, alike, loc.Dx, loc.Dy, double.NaN, null);
        int cx = Math.Clamp(expectX + (int)Math.Round(loc.Dx * k), 0, Math.Max(0, shot.Width - width));
        int cy = Math.Clamp(expectY + (int)Math.Round(loc.Dy * k), 0, Math.Max(0, shot.Height - height));
        var crop = shot.Crop(new Int32Rect(cx, cy, Math.Min(width, shot.Width), Math.Min(height, shot.Height)));
        return new Verdict(true, loc.Difference, alike, loc.Dx, loc.Dy, ImageMatch.DetailGain(ImageMatch.ToGray(crop), frozenGray, k), crop);
    }

    /// <summary>
    /// Puts the window back exactly where it was (place, size, scaling) - place and size in one go. Put back by its
    /// corner only, a window 2-4× its size mostly hangs off the screens and Windows left its scaling at the virtual
    /// screen's; the size set after that brought the scaling back late, and the app shrank itself by the same factor
    /// once more (a window came back at a quarter of its size). So: the size again whenever the scaling has come back,
    /// until it stays.
    /// </summary>
    private static void Restore(IntPtr hwnd, Native.RECT r0, uint dpi0)
    {
        try
        {
            bool Back() => Native.GetWindowRect(hwnd, out var r) && r.Left == r0.Left && r.Top == r0.Top && r.Width == r0.Width && r.Height == r0.Height;
            var clock = Stopwatch.StartNew();
            Native.SetWindowPos(hwnd, IntPtr.Zero, r0.Left, r0.Top, r0.Width, r0.Height, Quiet);
            if (!WaitFor(() => Native.GetDpiForWindow(hwnd) == dpi0, 900))
            {
                Native.GetWindowRect(hwnd, out var r);
                Log.Warn($"HD restore: dpi {Native.GetDpiForWindow(hwnd)} (not {dpi0}) {clock.ElapsedMilliseconds} ms after the move back, window at {r}");
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (WaitFor(Back, attempt == 0 ? 150 : 300) && Stays(Back, 120)) break;
                Native.SetWindowPos(hwnd, IntPtr.Zero, r0.Left, r0.Top, r0.Width, r0.Height, Quiet);
            }
            if (!Back())
            {
                Native.GetWindowRect(hwnd, out var r);
                Log.Warn($"HD capture: window not exactly restored ({r} instead of {r0}, dpi {Native.GetDpiForWindow(hwnd)})");
            }
            Thread.Sleep(60); // a moment for the app to draw itself at its normal size before the frozen picture goes away
        }
        catch (Exception ex) { Log.Error("HD restore failed", ex); }
    }

    /// <summary>True if the condition holds the whole time (a late change of the app's own counts as not holding).</summary>
    private static bool Stays(Func<bool> ok, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            if (!ok()) return false;
            Thread.Sleep(15);
        }
        return ok();
    }

    private static bool WaitFor(Func<bool> done, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (done()) return true;
            if (clock.ElapsedMilliseconds >= milliseconds) return false;
            Thread.Sleep(8);
        }
    }

    /// <summary>Short text for the user why a capture stayed at 1×.</summary>
    public static string Explain(HdFailure f) => f switch
    {
        HdFailure.Desktop => L.T("Hd.Why.Desktop"),
        HdFailure.SeveralWindows => L.T("Hd.Why.Several"),
        HdFailure.LegacyApp => L.T("Hd.Why.Legacy"),
        HdFailure.Admin => L.T("Hd.Why.Admin"),
        HdFailure.Fullscreen => L.T("Hd.Why.Fullscreen"),
        HdFailure.NotResponding => L.T("Hd.Why.Hung"),
        HdFailure.NoGain => L.T("Hd.Why.NoGain"),
        HdFailure.Changed or HdFailure.Moved => L.T("Hd.Why.Changed"),
        HdFailure.NotInstalled or HdFailure.NoScreen => L.T("Hd.Why.NoScreen"),
        _ => L.T("Hd.Why.Error"),
    };
}
