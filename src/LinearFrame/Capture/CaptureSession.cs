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
    private readonly List<Int32Rect> _windows; // visible windows, physical virtual-screen pixels
    private readonly List<(CaptureOverlayWindow Overlay, PixelBuffer Pixels)> _overlays = new();
    private bool _finished;

    public ExportSize Size { get; private set; }
    public event Action? SizeChanged;

    private CaptureSession(AppSettings s, List<Int32Rect> windows)
    {
        _s = s;
        _windows = windows;
        Size = s.DefaultExportSize;
    }

    internal static (CaptureSession session, CaptureOverlayWindow overlay) CreateForTest(AppSettings s, PixelBuffer screen)
    {
        var mon = new MonitorInfo(IntPtr.Zero, new Int32Rect(0, 0, screen.Width, screen.Height), new Int32Rect(0, 0, screen.Width, screen.Height), 1.0, true);
        var session = new CaptureSession(s, new List<Int32Rect>());
        var overlay = session.AddOverlay(mon, screen, activate: false, show: false);
        return (session, overlay);
    }

    /// <summary>
    /// Runs one invisible overlay off-screen shortly after start-up, so the very first Ctrl+Shift+4 is as fast as every
    /// later one (code, styles, fonts and the render pipeline are already warm).
    /// </summary>
    public static void Prewarm(AppSettings s)
    {
        if (Current != null) return;
        try
        {
            var area = new Int32Rect(-32000, -32000, 32, 32);
            var mon = new MonitorInfo(IntPtr.Zero, area, area, 1.0, false);
            var session = new CaptureSession(s, new List<Int32Rect>());
            var pb = new PixelBuffer(32, 32);
            pb.ForceOpaque();
            var o = session.AddOverlay(mon, pb, activate: false, show: true);
            o.ContentRendered += (_, _) => o.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                session._finished = true;
                o.Close();
            }));
        }
        catch (Exception ex) { Log.Error("Prewarm failed", ex); }
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
            var windows = s.DetectWindows ? WindowFinder.VisibleWindows() : new List<Int32Rect>();
            var session = new CaptureSession(s, windows);
            Current = session;

            // The monitor you are looking at first - the overlay appears there right away...
            session.AddOverlay(first, ScreenCapturer.Capture(first.Bounds), activate: true, show: true);
            Log.Info($"Capture overlay ready in {sw.ElapsedMilliseconds} ms ({monitors.Count} monitors, {windows.Count} windows)");

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
                            session.AddOverlay(m, ScreenCapturer.Capture(m.Bounds), activate: false, show: true);
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

    public static void CaptureFullscreen(AppSettings s)
    {
        if (Current != null) return;
        try
        {
            Native.GetCursorPos(out var p);
            var monitors = MonitorInfo.All();
            var m = monitors.FirstOrDefault(x => x.Contains(p.X, p.Y)) ?? monitors.FirstOrDefault(x => x.Primary) ?? monitors[0];
            var pb = ScreenCapturer.Capture(m.Bounds);
            Deliver(s, pb, s.DefaultExportSize, CaptureAction.Copy, m);
        }
        catch (Exception ex) { Log.Error("Fullscreen capture failed", ex); }
    }

    private CaptureOverlayWindow AddOverlay(MonitorInfo m, PixelBuffer pixels, bool activate, bool show)
    {
        var bmp = pixels.ToBitmap();
        var windows = _windows
            .Select(w => ImageOps.Intersect(w, m.Bounds))
            .Where(w => w.Width > 0 && w.Height > 0)
            .Select(w => new Int32Rect(w.X - m.Bounds.X, w.Y - m.Bounds.Y, w.Width, w.Height))
            .ToList();
        var o = new CaptureOverlayWindow(this, m, bmp, windows, activate);
        _overlays.Add((o, pixels));
        if (show) o.Show();
        return o;
    }

    private void ActivateUnderCursor()
    {
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

    public void Finish(CaptureOverlayWindow source, Int32Rect localPx, CaptureAction action)
    {
        if (_finished) return;
        _finished = true;
        var pixels = _overlays.First(x => x.Overlay == source).Pixels;
        var r = ImageOps.Intersect(localPx, new Int32Rect(0, 0, pixels.Width, pixels.Height));
        CloseOverlays();
        Current = null;
        if (r.Width <= 0 || r.Height <= 0) return;
        var crop = pixels.Crop(r);
        Deliver(_s, crop, Size, action, source.Monitor);
    }

    private void CloseOverlays()
    {
        foreach (var (o, _) in _overlays) { try { o.Close(); } catch { /* closing anyway */ } }
        _overlays.Clear();
    }

    // ------------------------------------------------------------------ results

    private static async void Deliver(AppSettings s, PixelBuffer shot, ExportSize size, CaptureAction action, MonitorInfo monitor)
    {
        try
        {
            if (s.PlaySound) Sound.Shutter();

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
                Hud.Show(L.T("Capture.Saved"), "check");
                App.Instance?.NotifyScreenshotsChanged();
                return;
            }

            byte[] pngBytes = await Task.Run(() => ImageIO.EncodePng(output));
            string? saved = null;
            if (s.AutoSave)
            {
                byte[] fileBytes = s.ImageFormat == ImageFormatKind.Png ? pngBytes : await Task.Run(() => ImageIO.EncodeJpg(output));
                saved = await Task.Run(() => ImageIO.SaveScreenshotBytes(fileBytes, s));
                App.Instance?.NotifyScreenshotsChanged();
            }
            if (s.CopyToClipboard) ImageIO.CopyToClipboard(output, pngBytes);

            if (s.ShowThumbnail) CaptureToast.Show(output, saved, monitor);
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
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.C when mods.HasFlag(ModifierKeys.Control):
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
            case Key.Left: source.Nudge(-1, 0, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
            case Key.Right: source.Nudge(1, 0, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
            case Key.Up: source.Nudge(0, -1, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
            case Key.Down: source.Nudge(0, 1, mods.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
        }
    }
}
