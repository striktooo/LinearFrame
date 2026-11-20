using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.UI;

namespace LinearFrame.Viewer;

/// <summary>
/// "Live Text" over a picture, like on an iPhone or a Mac: the text in it was recognised in the background, the cursor
/// turns into a text cursor over it, dragging selects words (in reading order, across lines), Ctrl+C or a right-click copies
/// them, and every QR code gets a button that opens its link. Works on a <see cref="ZoomImageView"/> at any zoom.
/// </summary>
internal sealed class LiveText : FrameworkElement
{
    private static readonly Brush SelFill = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0x0A, 0x84, 0xFF)));
    private static readonly Brush AllFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x84, 0xFF)));

    private readonly ZoomImageView _view;
    private readonly Canvas _pills;
    private readonly List<(OcrWord Word, int Line)> _words = new();
    private OcrText? _text;
    private List<QrReader.Code> _codes = new();
    private int _a = -1, _b = -1;
    private bool _showAll;

    public bool HasText => _words.Count > 0;
    public bool HasSelection => _a >= 0 && _b >= 0;
    public event Action? Changed;

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public LiveText(ZoomImageView view, Canvas pills)
    {
        _view = view;
        _pills = pills;
        IsHitTestVisible = false;
        view.ViewChanged += () => { InvalidateVisual(); PlacePills(); };
        view.SizeChanged += (_, _) => { InvalidateVisual(); PlacePills(); };
        view.IsOverText = p => WordAt(p) >= 0;
        view.TextDown = (p, clicks) =>
        {
            int i = WordAt(p);
            if (i < 0) { Clear(); return false; }
            if (clicks == 2)
            {
                // a double-click selects the whole line
                int line = _words[i].Line;
                _a = _words.FindIndex(w => w.Line == line);
                _b = _words.FindLastIndex(w => w.Line == line);
            }
            else _a = _b = i;
            _showAll = false;
            Update();
            return true;
        };
        view.TextDrag = p =>
        {
            int i = Nearest(p);
            if (i >= 0 && i != _b) { _b = i; Update(); }
        };
        view.TextUp = () => Changed?.Invoke();
        view.MouseRightButtonUp += (_, e) =>
        {
            if (!HasText) return;
            int i = WordAt(e.GetPosition(view));
            if (i < 0 && !HasSelection) return;
            if (i >= 0 && !Selected(i)) { _a = _b = i; Update(); }
            e.Handled = true;
            var m = new NativeMenu();
            m.Add(L.T("Live.Copy") + "\tCtrl+C", () => CopySelection());
            m.Add(L.T("Live.SelectAll"), SelectAll);
            Native.GetCursorPos(out var pt);
            m.Show(new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(view)).Handle, pt.X, pt.Y, forceDark: Theme.IsDark);
        };
    }

    /// <summary>What was found in the current picture (positions in the picture's own pixels).</summary>
    public void Set(OcrText? text, List<QrReader.Code> codes)
    {
        _text = text;
        _codes = codes;
        _words.Clear();
        if (text != null)
            for (int l = 0; l < text.Lines.Count; l++)
                foreach (var w in text.Lines[l].Words) _words.Add((w, l));
        _a = _b = -1;
        _showAll = false;
        BuildPills();
        Update();
    }

    public void Reset() => Set(null, new List<QrReader.Code>());

    public void Clear()
    {
        if (_a < 0 && !_showAll) return;
        _a = _b = -1;
        _showAll = false;
        Update();
    }

    public void SelectAll()
    {
        if (!HasText) return;
        _a = 0;
        _b = _words.Count - 1;
        Update();
    }

    /// <summary>Copies the selection (or, with none, everything). False when there is no text.</summary>
    public bool CopySelection(bool allWhenNone = false)
    {
        string text = HasSelection ? SelectedText() : allWhenNone && _text != null ? _text.Text : "";
        if (text.Length == 0) return false;
        if (!ImageIO.CopyText(text)) return false;
        Hud.Show(L.T("Live.Copied"), "copy", Window.GetWindow(this));
        return true;
    }

    /// <summary>All text highlighted for a moment and copied ("Copy text" button).</summary>
    public void CopyAll()
    {
        if (_text == null || _text.IsEmpty) return;
        _showAll = true;
        _a = _b = -1;
        InvalidateVisual();
        if (ImageIO.CopyText(_text.Text)) Hud.Show(L.T("Live.Copied"), "copy", Window.GetWindow(this));
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) => { t.Stop(); _showAll = false; InvalidateVisual(); };
        t.Start();
    }

    private bool Selected(int i) => HasSelection && i >= Math.Min(_a, _b) && i <= Math.Max(_a, _b);

    private string SelectedText()
    {
        int a = Math.Min(_a, _b), b = Math.Max(_a, _b);
        var sb = new System.Text.StringBuilder();
        for (int i = a; i <= b; i++)
        {
            if (i > a) sb.Append(_words[i].Line != _words[i - 1].Line ? Environment.NewLine : " ");
            sb.Append(_words[i].Word.Text);
        }
        return sb.ToString();
    }

    private Rect ViewRect(Rect pic)
    {
        var a = _view.ToView(pic.TopLeft);
        var b = _view.ToView(pic.BottomRight);
        return new Rect(a, b);
    }

    private int WordAt(Point p)
    {
        for (int i = 0; i < _words.Count; i++)
        {
            var r = ViewRect(_words[i].Word.Box);
            r.Inflate(2, 2);
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>The word under the mouse, or the nearest one on the line under it (dragging past the line ends).</summary>
    private int Nearest(Point p)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _words.Count; i++)
        {
            var r = ViewRect(_words[i].Word.Box);
            double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
            double d = dy * 4 + dx; // the line counts more than the distance along it
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void Update()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_words.Count == 0) return;
        for (int i = 0; i < _words.Count; i++)
        {
            if (!_showAll && !Selected(i)) continue;
            var r = ViewRect(_words[i].Word.Box);
            r.Inflate(2, 1.5);
            // the gap to the next word on the line is selected too, so a selection reads as one band
            if (Selected(i) && i + 1 < _words.Count && Selected(i + 1) && _words[i + 1].Line == _words[i].Line)
            {
                var next = ViewRect(_words[i + 1].Word.Box);
                r.Union(new Rect(r.Right, r.Top, Math.Max(0, next.Left - r.Right), r.Height));
            }
            dc.DrawRoundedRectangle(_showAll ? AllFill : SelFill, null, r, 3, 3);
        }
    }

    // ------------------------------------------------------------------ QR codes

    private void BuildPills()
    {
        _pills.Children.Clear();
        foreach (var code in _codes)
        {
            string? link = code.Link;
            var label = new TextBlock
            {
                Text = link != null ? Short(link) : L.T("Live.QrCopy"),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new IconView { Icon = link != null ? "link" : "copy", Width = 14, Height = 14, Foreground = Brushes.White, Margin = new Thickness(0, 0, 7, 0) });
            content.Children.Add(label);
            var pill = new Button
            {
                Content = content,
                ToolTip = code.Text,
                Padding = new Thickness(12, 6, 13, 6),
                Cursor = Cursors.Hand,
                Tag = code,
            };
            pill.SetResourceReference(StyleProperty, "Hud.Primary");
            pill.Click += (_, _) =>
            {
                if (code.Link is string l) ShellUtil.Open(l);
                else if (ImageIO.CopyText(code.Text)) Hud.Show(L.T("Live.Copied"), "copy", Window.GetWindow(this));
            };
            _pills.Children.Add(pill);
        }
        PlacePills();
    }

    private void PlacePills()
    {
        foreach (Button pill in _pills.Children)
        {
            if (pill.Tag is not QrReader.Code code) continue;
            var r = ViewRect(code.Bounds);
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var s = pill.DesiredSize;
            double x = Math.Clamp(r.X + r.Width / 2 - s.Width / 2, 6, Math.Max(6, _pills.ActualWidth - s.Width - 6));
            double y = Math.Clamp(r.Bottom + 10, 6, Math.Max(6, _pills.ActualHeight - s.Height - 6));
            Canvas.SetLeft(pill, x);
            Canvas.SetTop(pill, y);
        }
    }

    /// <summary>"https://www.example.com/a/b?c" → "example.com/a/b?c" (shortened when long).</summary>
    public static string Short(string link)
    {
        var s = link;
        foreach (var p in new[] { "https://", "http://" }) if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) s = s[p.Length..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        s = s.TrimEnd('/');
        return s.Length > 42 ? s[..41] + "…" : s;
    }
}
