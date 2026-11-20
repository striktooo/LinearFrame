using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LinearFrame.Capture;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.Player;
using LinearFrame.UI;

namespace LinearFrame.Viewer;

/// <summary>
/// Picture viewer (photos, screenshots, anything Windows can decode). The picture shows up right away from a
/// screen-sized copy - the full one is only decoded when you zoom past it. ← → walk through the folder.
/// </summary>
public partial class ImageViewerWindow : Window
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Above this many pixels the "full" copy is capped (a 200-megapixel panorama would need 800 MB).</summary>
    private const long FullPixelsCap = 120_000_000;

    private string? _path;
    private ImageIO.ImageInfo? _info;
    private List<string> _folder = new();
    private string? _folderDir;
    private int _loadId;
    private bool _full, _fullLoading;
    private DateTime _stamp;
    private bool _shown;

    public string? CurrentPath => _path;
    private readonly LiveText _live;
    private CancellationTokenSource? _liveCts;

    public ImageViewerWindow()
    {
        InitializeComponent();
        WindowHelper.Attach(this);
        View.ViewChanged += OnViewChanged;
        _live = new LiveText(View, QrPills);
        Grid.SetRow(_live, 1);
        var grid = (Grid)View.Parent;
        grid.Children.Insert(grid.Children.IndexOf(View) + 1, _live);
        Activated += (_, _) => ReloadIfChanged();
        SizeChanged += (_, _) => UpdateCompact();
        Closed += (_, _) =>
        {
            _loadId++;
            View.Clear();
            Memory.TrimSoon();
        };
    }

    /// <summary>Shows a picture (the window sizes itself to it the first time).</summary>
    public void Open(string path, ImageIO.ImageInfo? info = null)
    {
        if (!_shown)
        {
            SizeTo(info ?? ImageIO.Probe(path));
            _shown = true;
        }
        ShowPicture(path, info);
        LoadFolder(path);
        WindowHelper.BringToFront(this);
    }

    /// <summary>
    /// First opening: the picture as big as it is (at most ~80 % of the screen), in a window never smaller than a
    /// comfortable ~45 % of the screen - a tiny icon doesn't get a tiny window, a 50 MP photo doesn't cover everything.
    /// </summary>
    private void SizeTo(ImageIO.ImageInfo? info)
    {
        var work = WindowHelper.WorkAreaAtCursor(out double scale);
        double sw = work.Width / scale, sh = work.Height / scale;
        double maxW = sw * 0.80, maxH = sh * 0.86 - 46;
        double minW = Math.Min(maxW, Math.Max(720, sw * 0.45)), minH = Math.Min(maxH, Math.Max(460, sh * 0.48));
        double w = minW, h = minH;
        if (info != null && info.ShownWidth > 0 && info.ShownHeight > 0)
        {
            double pw = info.ShownWidth / scale, ph = info.ShownHeight / scale;
            double k = Math.Min(1, Math.Min(maxW / pw, maxH / ph));
            w = Math.Clamp(pw * k, minW, maxW);
            h = Math.Clamp(ph * k, minH, maxH);
        }
        WindowHelper.PlaceCentered(this, w, h + 46);
    }

    // ------------------------------------------------------------------ loading

    private static int PreviewSide()
    {
        // a copy as big as the largest screen: sharp when the picture fills it, light and fast to decode
        int side = 1920;
        foreach (var m in MonitorInfo.All()) side = Math.Max(side, Math.Max(m.Bounds.Width, m.Bounds.Height));
        return side;
    }

    private static int FullSide(ImageIO.ImageInfo info)
    {
        long px = (long)info.Width * info.Height;
        if (px <= FullPixelsCap) return 0;
        return (int)(Math.Max(info.Width, info.Height) * Math.Sqrt(FullPixelsCap / (double)px));
    }

    private long _shownAt = long.MinValue / 2; // when the last picture was asked for (Environment.TickCount64)

    private async void ShowPicture(string path, ImageIO.ImageInfo? known)
    {
        int id = ++_loadId;
        // walking through a folder (← → held or pressed quickly): pictures that are only passed by are not read for text
        long now = Environment.TickCount64;
        bool flipping = now - _shownAt < 1200;
        _shownAt = now;
        _path = path;
        _info = null;
        _full = _fullLoading = false;
        Title = $"{Path.GetFileName(path)} — LinearFrame";
        TitleText.Text = Path.GetFileName(path);
        InfoText.Text = "";
        MessageView.Visibility = Visibility.Collapsed;
        try { _stamp = File.GetLastWriteTimeUtc(path); } catch { _stamp = default; }
        UpdateNav();

        int side = PreviewSide();
        ImageIO.ImageInfo? info = null;
        BitmapSource? bmp = null;
        try
        {
            (info, bmp) = await Task.Run<(ImageIO.ImageInfo?, BitmapSource?)>(() =>
            {
                var i = known ?? ImageIO.Probe(path);
                return i == null ? (null, null) : (i, ImageIO.Decode(path, i, side));
            });
        }
        catch (Exception ex) { Log.Warn($"Viewer: {Path.GetFileName(path)} could not be decoded: {ex.Message}"); }
        if (id != _loadId) return; // the next picture was asked for meanwhile

        if (info == null || bmp == null)
        {
            View.Clear();
            ShowMessage(Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" or ".avif" or ".webp" or ".jxr"
                ? L.F("Viewer.NoCodec", Path.GetExtension(path).TrimStart('.').ToUpperInvariant())
                : L.T("Viewer.CannotOpen"));
            UpdateZoomText();
            return;
        }
        _info = info;
        _full = bmp.PixelWidth >= info.ShownWidth;
        View.SetPicture(bmp, info.ShownWidth, info.ShownHeight, info.HasAlpha);
        InfoText.Text = $"{info.ShownWidth} × {info.ShownHeight}";
        ReadText(id, bmp, info, flipping);
        if (View.WantsMorePixels) LoadFull();
        Memory.TrimSoon(4000); // the previous picture's pixels go back
    }

    /// <summary>
    /// Live Text: the picture's text and QR codes are read in the background (the screen-sized copy is plenty for that);
    /// then text can be selected right on the picture and codes get a button.
    /// </summary>
    private async void ReadText(int id, BitmapSource bmp, ImageIO.ImageInfo info, bool flipping)
    {
        _liveCts?.Cancel();
        var cts = _liveCts = new CancellationTokenSource();
        _live.Reset();
        TextBtn.Visibility = Visibility.Collapsed;
        try
        {
            // Reading takes every processor core for up to a second. While you flip through pictures only the one you
            // stop on is read - the ones passed by used to be started, cancelled and thrown away one after another.
            if (flipping) await Task.Delay(350, cts.Token);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (text, codes) = await Task.Run(() =>
            {
                var px = PixelBuffer.FromBitmap(bmp);
                var codes = QrReader.Read(px);
                cts.Token.ThrowIfCancellationRequested();
                var text = TextRecognizer.Recognize(px, cts.Token);
                return (text, codes);
            }, cts.Token);
            if (id != _loadId || cts.IsCancellationRequested) return;
            // the copy on screen may be smaller than the picture: positions are in the picture's own pixels
            double k = info.ShownWidth / (double)Math.Max(1, bmp.PixelWidth);
            Rect S(Rect r) => new(r.X * k, r.Y * k, r.Width * k, r.Height * k);
            var scaled = text == null ? null : new OcrText(text.Lines.Select(l => new OcrLine(l.Words.Select(w => new OcrWord(w.Text, S(w.Box))).ToList())).ToList());
            var scaledCodes = codes.Select(c => c with { Corners = c.Corners.Select(p => new Point(p.X * k, p.Y * k)).ToArray() }).ToList();
            _live.Set(scaled, scaledCodes);
            TextBtn.Visibility = _live.HasText ? Visibility.Visible : Visibility.Collapsed;
            Log.Info($"Live Text: {scaled?.Lines.Count ?? 0} lines, {codes.Count} QR code(s) in {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Live Text: " + ex.Message); }
    }

    private void Text_Click(object sender, RoutedEventArgs e) => _live.CopyAll();

    /// <summary>Zoomed past the screen-sized copy: decode every pixel (once per picture).</summary>
    private async void LoadFull()
    {
        if (_full || _fullLoading || _path == null || _info == null) return;
        _fullLoading = true;
        int id = _loadId;
        var path = _path;
        var info = _info;
        BitmapSource? bmp = null;
        try { bmp = await Task.Run(() => ImageIO.Decode(path, info, FullSide(info))); }
        catch (Exception ex) { Log.Warn($"Viewer: full decode failed: {ex.Message}"); }
        if (id != _loadId) return;
        _fullLoading = false;
        if (bmp == null) return;
        _full = true;
        View.Upgrade(bmp);
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageView.Visibility = Visibility.Visible;
    }

    /// <summary>Back from the editor (or the file changed otherwise): show what is on disk now.</summary>
    private void ReloadIfChanged()
    {
        if (_path == null) return;
        if (!File.Exists(_path))
        {
            _folder.RemoveAll(p => string.Equals(p, _path, StringComparison.OrdinalIgnoreCase));
            var next = _folder.FirstOrDefault(File.Exists);
            if (next != null) ShowPicture(next, null);
            else { View.Clear(); ShowMessage(L.T("Viewer.Gone")); }
            return;
        }
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(_path); } catch { return; }
        if (stamp != _stamp) ShowPicture(_path, null);
    }

    // ------------------------------------------------------------------ folder

    private void LoadFolder(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir == null) return;
        if (string.Equals(dir, _folderDir, StringComparison.OrdinalIgnoreCase) && _folder.Count > 0 &&
            _folder.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            UpdateNav();
            return;
        }
        _folderDir = dir;
        _folder = new List<string> { path };
        UpdateNav();
        Task.Run(() =>
        {
            var list = Directory.EnumerateFiles(dir).Where(MediaFormats.IsImage).ToList();
            list.Sort((a, b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b))); // like Explorer: "2" before "10"
            return list;
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (!t.IsCompletedSuccessfully || !string.Equals(dir, _folderDir, StringComparison.OrdinalIgnoreCase)) return;
            _folder = t.Result;
            UpdateNav();
        }));
    }

    private int Index => _path == null ? -1 : _folder.FindIndex(p => string.Equals(p, _path, StringComparison.OrdinalIgnoreCase));

    private void UpdateNav()
    {
        int i = Index;
        NavButtons.Visibility = _folder.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PrevBtn.IsEnabled = i > 0;
        NextBtn.IsEnabled = i >= 0 && i < _folder.Count - 1;
    }

    internal void Step(int delta)
    {
        int i = Index;
        if (i < 0) return;
        for (int j = i + delta; j >= 0 && j < _folder.Count; j += delta)
        {
            if (File.Exists(_folder[j])) { ShowPicture(_folder[j], null); return; }
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    // ------------------------------------------------------------------ zoom

    private void OnViewChanged()
    {
        UpdateZoomText();
        if (View.WantsMorePixels) LoadFull();
    }

    private void UpdateZoomText() => ZoomText.Text = View.HasPicture ? $"{Math.Round(View.Zoom * 100)}%" : "";

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => View.ZoomBy(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => View.ZoomBy(0.8);
    private void ZoomToggle_Click(object sender, RoutedEventArgs e) => View.ToggleZoom();

    /// <summary>Narrow window: the toolbar drops what matters least first.</summary>
    private void UpdateCompact()
    {
        double w = ActualWidth;
        InfoText.Visibility = w < 720 ? Visibility.Collapsed : Visibility.Visible;
        ZoomGroup.Visibility = w < 600 ? Visibility.Collapsed : Visibility.Visible;
        EditText.Visibility = w < 480 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ actions

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();

    private async Task CopyAsync()
    {
        if (_path == null || _info == null) return;
        var path = _path;
        var info = _info;
        try
        {
            var (bmp, png) = await Task.Run(() =>
            {
                var b = ImageIO.Decode(path, info, FullSide(info));
                return (b, ImageIO.EncodePng(b));
            });
            if (ImageIO.CopyToClipboard(bmp, png)) Hud.Show(L.T("Capture.Copied"), "copy", this);
        }
        catch (Exception ex) { Log.Error("Viewer copy failed", ex); }
        finally { Memory.TrimSoon(4000); }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => Edit();

    private void Edit()
    {
        if (_path == null || _info == null) return;
        App.Instance?.OpenImageInEditor(_path);
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool handled = true;
        switch (e.Key)
        {
            case Key.Left: case Key.PageUp: Step(-1); break;
            case Key.Right: case Key.PageDown: Step(1); break;
            case Key.Home when _folder.Count > 0: if (_folder[0] != _path) ShowPicture(_folder[0], null); break;
            case Key.End when _folder.Count > 0: if (_folder[^1] != _path) ShowPicture(_folder[^1], null); break;
            case Key.OemPlus: case Key.Add: View.ZoomBy(1.25); break;
            case Key.OemMinus: case Key.Subtract: View.ZoomBy(0.8); break;
            case Key.D0: case Key.NumPad0: View.ShowWhole(); break;
            case Key.D1: case Key.NumPad1: View.ShowActualPixels(); break;
            case Key.C when ctrl:
                // selected text first (Live Text), otherwise the picture
                if (!_live.CopySelection()) _ = CopyAsync();
                break;
            case Key.A when ctrl: _live.SelectAll(); break;
            case Key.E when !ctrl: Edit(); break;
            case Key.W when ctrl: Close(); break;
            case Key.Escape:
                if (_live.HasSelection) _live.Clear();
                else Close();
                break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
        else base.OnPreviewKeyDown(e);
    }
}
