using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinearFrame.Core;
using LinearFrame.Editor;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Capture;

public enum CaptureAction { Copy, Edit, SaveAs }

/// <summary>
/// One "Ctrl+Shift+4" session: the desktop is frozen into a bitmap, every monitor gets a dimmed overlay,
/// the user selects an area (or clicks a window), then we copy/save/edit.
/// </summary>
public sealed class CaptureSession
{
    public static CaptureSession? Current { get; private set; }

    private readonly AppSettings _s;
    private readonly List<WindowEntry> _entries; // visible windows when the screen was frozen, front first
    private readonly List<Int32Rect> _windows;   // their bounds (for "click a window"), physical virtual-screen pixels
    private readonly List<(CaptureOverlayWindow Overlay, BitmapSource Shot)> _overlays = new(); // one frozen screen per monitor
    private bool _finished;

    public ExportSize Size { get; private set; }
    /// <summary>HD is on for this capture: the selection is really re-rendered at 2–4× instead of taken from the screen.</summary>
    public bool Hd { get; private set; }
    /// <summary>The "HD" switch can be offered (the virtual screen is installed and switched on in Settings).</summary>
    public bool HdAvailable { get; }
    public int HdScale => Math.Clamp(_s.HdScale, 2, 4);
    public event Action? SizeChanged;

    private CaptureSession(AppSettings s, List<WindowEntry> entries, bool? hd = null)
    {
        _s = s;
        _entries = entries;
        _windows = s.DetectWindows ? entries.Select(e => e.Bounds).ToList() : new List<Int32Rect>();
        Size = s.DefaultExportSize;
        HdAvailable = hd ?? App.Instance?.HdAvailable == true;
        Hd = HdAvailable && s.HdCapture;
    }

    internal static (CaptureSession session, CaptureOverlayWindow overlay) CreateForTest(AppSettings s, PixelBuffer screen, bool hd = false)
    {
        var mon = new MonitorInfo(IntPtr.Zero, new Int32Rect(0, 0, screen.Width, screen.Height), new Int32Rect(0, 0, screen.Width, screen.Height), 1.0, true);
        var session = new CaptureSession(s, new List<WindowEntry>(), hd);
        var overlay = session.AddOverlay(mon, screen.ToBitmap(), activate: false, show: false);
        return (session, overlay);
    }

    private static bool _warming;

    /// <summary>
    /// Puts a tiny, invisible (cloaked, click-through) overlay on every monitor for a moment, so the first shortcut press
    /// is as fast as every later one: code, styles and fonts are warm, and WPF has set up its Direct3D device for each
    /// screen. Right after Windows starts that set-up alone can take several seconds - the very first capture used to sit
    /// invisible for that long (and an off-screen window, which this used to be, never set it up at all).
    /// </summary>
    public static void Prewarm(AppSettings s, string why)
    {
        if (Current != null || _warming) return;
        _warming = true;
        var sw = Stopwatch.StartNew();
        CaptureSession? session = null;
        try
        {
            var monitors = MonitorInfo.All();
            session = new CaptureSession(s, new List<WindowEntry>(), hd: false);
            var done = session;
            int pending = monitors.Count;
            long created = 0, drawn = 0;
            var pb = new PixelBuffer(16, 16);
            pb.ForceOpaque();
            var dot = pb.ToBitmap();

            void Finish(string state)
            {
                if (!_warming) return;
                _warming = false;
                done._finished = true;
                done.CloseOverlays();
                Log.Info($"Warm-up ({why}): {monitors.Count} screens, windows {created} ms, first frames {(drawn > 0 ? drawn + " ms" : state)}");
                Memory.TrimSoon(3000);
            }

            // never leave anything behind, even if a window doesn't get to draw
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            guard.Tick += (_, _) => { guard.Stop(); Finish("not drawn after 30 s"); };
            guard.Start();

            foreach (var m in monitors)
            {
                var spot = new Int32Rect(m.Bounds.X, m.Bounds.Y, 16, 16); // the monitor's corner: WPF picks that screen's device
                var o = session.AddOverlay(new MonitorInfo(m.Handle, spot, spot, m.Scale, m.Primary), dot, activate: false, show: true, warmup: true);
                o.WarmedUp += () =>
                {
                    if (--pending > 0) return;
                    drawn = sw.ElapsedMilliseconds;
                    // the render thread finishes the device and the first frame after this - give it a moment, then let go
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                    t.Tick += (_, _) => { t.Stop(); guard.Stop(); Finish(""); };
                    t.Start();
                };
            }
            created = sw.ElapsedMilliseconds;
            if (monitors.Count == 0) { guard.Stop(); Finish("no screens"); }
        }
        catch (Exception ex)
        {
            Log.Error("Prewarm failed", ex);
            _warming = false;
            if (session != null) { session._finished = true; session.CloseOverlays(); }
        }
    }

    public static void StartRegion(AppSettings s)
    {
        if (Current != null) { Current.ActivateUnderCursor(); return; }
        var sw = Stopwatch.StartNew();
        try
        {
            var monitors = MonitorInfo.All();
            if (monitors.Count == 0) return;
            Native.GetCursorPos(out var cursor);
            var first = monitors.FirstOrDefault(m => m.Contains(cursor.X, cursor.Y)) ?? monitors[0];
            var session = new CaptureSession(s, WindowFinder.Snapshot());
            Current = session;

            // The monitor you are looking at first - the overlay appears there right away...
            session.AddOverlay(first, ScreenCapturer.CaptureBitmap(first.Bounds), activate: true, show: true);
            Log.Info($"Capture overlay ready in {sw.ElapsedMilliseconds} ms ({monitors.Count} monitors, {session._entries.Count} windows)");

            // ...the other monitors follow a moment later (each overlay only covers its own screen).
            var rest = monitors.Where(m => m != first).ToList();
            if (rest.Count > 0)
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        foreach (var m in rest)
                        {
                            if (session._finished) return;
                            session.AddOverlay(m, ScreenCapturer.CaptureBitmap(m.Bounds), activate: false, show: true);
                        }
                        Log.Info($"All overlays ready in {sw.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex) { Log.Error("Secondary overlay failed", ex); }
                }));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            Current?.CloseOverlays();
            Current = null;
        }
    }

    private CaptureOverlayWindow AddOverlay(MonitorInfo m, BitmapSource bmp, bool activate, bool show, bool warmup = false)
    {
        var windows = _windows
            .Select(w => ImageOps.Intersect(w, m.Bounds))
            .Where(w => w.Width > 0 && w.Height > 0)
            .Select(w => new Int32Rect(w.X - m.Bounds.X, w.Y - m.Bounds.Y, w.Width, w.Height))
            .ToList();
        var o = new CaptureOverlayWindow(this, m, bmp, windows, activate, warmup);
        _overlays.Add((o, bmp));
        if (show) o.Show();
        return o;
    }

    /// <summary>Test copies: select the given area on the main monitor and "press Ctrl+C".</summary>
    internal void CommitForTest(Int32Rect localPx)
    {
        if (_finished) return;
        var o = _overlays.Select(x => x.Overlay).FirstOrDefault(x => x.Monitor.Primary) ?? _overlays.Select(x => x.Overlay).FirstOrDefault();
        if (o == null) return;
        o.SelectForTest(localPx);
        o.Commit(CaptureAction.Copy);
    }

    private void ActivateUnderCursor()
    {
        if (_finished) return;
        Native.GetCursorPos(out var p);
        var list = _overlays.Select(x => x.Overlay).ToList();
        var o = list.FirstOrDefault(x => x.Monitor.Contains(p.X, p.Y)) ?? list.FirstOrDefault();
        o?.Activate();
    }

    /// <summary>A selection started on one monitor - clear the others.</summary>
    public void OnSelectionStarted(CaptureOverlayWindow source)
    {
        foreach (var (o, _) in _overlays) if (o != source) o.ClearSelection();
    }

    public void SetSize(ExportSize size)
    {
        Size = size;
        SizeChanged?.Invoke();
    }

    public void ToggleHd()
    {
        if (!HdAvailable || _finished) return;
        Hd = !Hd;
        _s.HdCapture = Hd;
        _s.Save();
        SizeChanged?.Invoke();
    }

    public void ShowSizeMenu(FrameworkElement anchor)
    {
        var m = new NativeMenu();
        foreach (ExportSize s in Enum.GetValues<ExportSize>())
        {
            var size = s;
            m.Add(ExportSizes.Label(size), () => SetSize(size), isChecked: size == Size);
        }
        m.ShowFor(anchor, forceDark: true);
    }

    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        CloseOverlays();
        Current = null;
        Memory.TrimSoon();
    }

    private ClipTicket? _ticket;

    public void Finish(CaptureOverlayWindow source, Int32Rect localPx, CaptureAction action)
    {
        if (_finished) return;
        _finished = true;
        var ticket = action == CaptureAction.Copy ? _ticket : null;
        var shot = _overlays.First(x => x.Overlay == source).Shot;
        var r = ImageOps.Intersect(localPx, new Int32Rect(0, 0, shot.PixelWidth, shot.PixelHeight));
        if (r.Width <= 0 || r.Height <= 0)
        {
            CloseOverlays();
            Current = null;
            return;
        }
        var crop = PixelBuffer.FromBitmap(new CroppedBitmap(shot, r)); // only the selection is ever copied out
        var monitor = source.Monitor;

        if (!Hd)
        {
            CloseOverlays();
            Current = null;
            Deliver(_s, crop, Size, action, monitor, null, ticket);
            return;
        }

        // HD: the frozen picture stays on screen while the window is re-rendered off-screen (the window briefly leaves
        // its place - nobody should have to watch that).
        int scale = HdCapture.ScaleFor(r.Width, r.Height, HdScale);
        foreach (var (o, _) in _overlays) o.ShowBusy(o == source ? L.F("Capture.HdBusy", scale) : null);
        var entries = _entries;
        Task.Run(() => HdCapture.Render(entries, monitor, r, crop, scale)).ContinueWith(t =>
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                var result = t.IsCompletedSuccessfully ? t.Result : new HdResult(null, 1, HdFailure.Error, t.Exception?.GetBaseException().Message ?? "");
                CloseOverlays();
                Current = null;
                Deliver(_s, result.Image ?? crop, Size, action, monitor, result, ticket);
            }));
        });
    }

    /// <summary>The text in the selection goes to the clipboard (read by Windows' own OCR), the screenshot is dropped.</summary>
    public async void CopyText(CaptureOverlayWindow source)
    {
        if (_finished) return;
        if (source.Target is not Int32Rect sel) return;
        var shot = _overlays.First(x => x.Overlay == source).Shot;
        var r = ImageOps.Intersect(sel, new Int32Rect(0, 0, shot.PixelWidth, shot.PixelHeight));
        if (r.Width < 4 || r.Height < 4) return;
        var crop = new CroppedBitmap(shot, r);
        crop.Freeze();
        source.ShowBusy(L.T("Capture.Reading"));
        OcrText? text = null;
        try { text = await Task.Run(() => TextRecognizer.Recognize(PixelBuffer.FromBitmap(crop))); }
        catch (Exception ex) { Log.Warn("Copy text: " + ex.Message); }
        if (_finished) return;
        if (text == null || text.IsEmpty)
        {
            source.EndBusy();
            Hud.Show(L.T(text == null ? "Capture.NoOcr" : "Capture.NoText"), "info");
            return;
        }
        _finished = true;
        CloseOverlays();
        Current = null;
        if (ImageIO.CopyText(text.Text)) Hud.Show(L.T("Live.Copied"), "copy");
        Log.Info($"Copied text: {text.Lines.Count} lines, {text.Text.Length} characters");
        Memory.TrimSoon();
    }

    private void CloseOverlays()
    {
        foreach (var (o, _) in _overlays) { try { o.Close(); } catch { /* closing anyway */ } }
        _overlays.Clear();
    }

    // ------------------------------------------------------------------ results

    private static async void Deliver(AppSettings s, PixelBuffer shot, ExportSize size, CaptureAction action, MonitorInfo monitor, HdResult? hd, ClipTicket? ticket = null)
    {
        try
        {
            if (s.PlaySound) Sound.Shutter();
            // what the little badge on the preview says: "HD 4×", or why this one stayed a normal screenshot
            string? badge = hd == null ? null : hd.Image != null ? $"HD {hd.Scale:0.#}×" : HdCapture.Explain(hd.Failure);
            bool badgeGood = hd?.Image != null;

            if (action == CaptureAction.Edit)
            {
                var original = shot.ToBitmap();
                string? path = null;
                if (s.AutoSave)
                {
                    var png = await Task.Run(() => ImageIO.Encode(original, s.ImageFormat));
                    path = await Task.Run(() => ImageIO.SaveScreenshotBytes(png, s));
                    App.Instance?.NotifyScreenshotsChanged();
                }
                var doc = new MarkupDocument(original) { FilePath = path, ExportSize = size };
                var ed = new EditorWindow(doc);
                ed.Show();
                ed.Activate();
                if (hd is { Image: null }) Hud.Show(badge!, "info", ed);
                return;
            }

            // Scale (Lanczos) + encode off the UI thread - an 8K export takes a moment.
            var output = await Task.Run(() =>
            {
                if (size == ExportSize.Original) return shot.ToBitmap();
                var (w, h) = ExportSizes.TargetSize(size, shot.Width, shot.Height);
                return ImageOps.Resize(shot, w, h).ToBitmap();
            });

            if (action == CaptureAction.SaveAs)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = L.F("Capture.FileName", DateTime.Now),
                    Filter = "PNG|*.png|JPG|*.jpg",
                    DefaultExt = ".png",
                    InitialDirectory = s.ScreenshotFolder,
                };
                if (dlg.ShowDialog() != true) return;
                var fmt = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? ImageFormatKind.Jpg : ImageFormatKind.Png;
                var bytes = await Task.Run(() => ImageIO.Encode(output, fmt));
                await File.WriteAllBytesAsync(dlg.FileName, bytes);
                if (s.CopyToClipboard) ImageIO.CopyToClipboard(output);
                Hud.Show(hd is { Image: null } ? badge! : L.T("Capture.Saved"), hd is { Image: null } ? "info" : "check");
                App.Instance?.NotifyScreenshotsChanged();
                return;
            }

            // (the small picture for the preview is made beside the encoding, so it costs no time)
            var preview = s.ShowThumbnail ? Task.Run(() => CaptureToast.Preview(output)) : null;
            byte[] pngBytes = await Task.Run(() => ImageIO.EncodePng(output));
            string? saved = null;
            if (s.AutoSave)
            {
                byte[] fileBytes = s.ImageFormat == ImageFormatKind.Png ? pngBytes : await Task.Run(() => ImageIO.EncodeJpg(output));
                saved = await Task.Run(() => ImageIO.SaveScreenshotBytes(fileBytes, s));
                App.Instance?.NotifyScreenshotsChanged();
            }
            if (s.CopyToClipboard)
            {
                if (ticket != null) ClipboardStack.Delivered(ticket, output, pngBytes, saved);
                else
                {
                    ImageIO.CopyToClipboard(output, pngBytes);
                    ClipboardStack.Single(saved);
                }
            }

            if (s.ShowThumbnail)
            {
                CaptureToast.Show(output, saved, monitor, badge, badgeGood, preview == null ? null : await preview);
                CaptureToast.FindCode(output); // a QR code in the screenshot: its link on the preview
            }
            else if (hd is { Image: null }) Hud.Show(badge!, "info");
            else Hud.Show(s.CopyToClipboard ? L.T("Capture.Copied") : L.T("Capture.Saved"), s.CopyToClipboard ? "copy" : "check");
        }
        catch (Exception ex)
        {
            Log.Error("Deliver failed", ex);
        }
        finally
        {
            Memory.TrimSoon(4000);
        }
    }

    public void HandleKey(CaptureOverlayWindow source, KeyEventArgs e)
    {
        if (_finished) { e.Handled = true; return; } // an HD render is under way
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;
            case Key.Enter:
                source.Commit(CaptureAction.Copy);
                e.Handled = true;
                break;
            case Key.C when mods.HasFlag(ModifierKeys.Control):
                // Ctrl+C+C (C once more, Ctrl still held) adds this capture to the ones in the clipboard
                if (_s.MultiCopy && _s.CopyToClipboard && !e.IsRepeat) _ticket = ClipboardStack.Begin();
                source.Commit(CaptureAction.Copy);
                e.Handled = true;
                break;
            case Key.S when mods.HasFlag(ModifierKeys.Control):
                source.Commit(CaptureAction.SaveAs);
                e.Handled = true;
                break;
            case Key.E when mods == ModifierKeys.None:
                source.Commit(CaptureAction.Edit);
                e.Handled = true;
                break;
            case Key.T when mods == ModifierKeys.None:
                CopyText(source);
                e.Handled = true;
                break;
            case Key.H when mods == ModifierKeys.None:
                ToggleHd();
                e.Handled = true;
                break;
            case Key.Left: source.Nudge(-1, 0, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
            case Key.Right: source.Nudge(1, 0, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
            case Key.Up: source.Nudge(0, -1, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
            case Key.Down: source.Nudge(0, 1, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
        }
    }
}
