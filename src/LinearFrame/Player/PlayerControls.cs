using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinearFrame.Player;

/// <summary>Thin capsule slider (scrubber / volume) like QuickTime: grows on hover, knob appears when needed.</summary>
public sealed class ThinSlider : FrameworkElement
{
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x4D, 255, 255, 255)));
    private static readonly Brush FillBrush = Brushes.White;
    private static readonly Pen KnobPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 0.5));
    private double _value;
    private bool _dragging;

    public double Value
    {
        get => _value;
        set
        {
            double v = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            if (Math.Abs(v - _value) < 1e-6) return;
            _value = v;
            InvalidateVisual();
        }
    }

    public bool IsDragging => _dragging;
    public bool AlwaysShowKnob { get; set; }

    public event Action<double>? ValueChanging;
    public event Action<double>? ValueCommitted;

    public ThinSlider()
    {
        Height = 18;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-test area
        bool active = _dragging || IsMouseOver;
        double th = active ? 6 : 4;
        double y = (h - th) / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, y, w, th), th / 2, th / 2);
        double fw = Math.Max(th, w * _value);
        dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, y, fw, th), th / 2, th / 2);
        if (active || AlwaysShowKnob)
        {
            double r = active ? 7 : 6;
            double x = Math.Clamp(w * _value, r, w - r);
            dc.DrawEllipse(Brushes.White, KnobPen, new Point(x, h / 2), r, r);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }

    private double ValueAt(MouseEventArgs e) => ActualWidth <= 0 ? 0 : Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragging = true;
        CaptureMouse();
        Value = ValueAt(e);
        ValueChanging?.Invoke(_value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Value = ValueAt(e);
        ValueChanging?.Invoke(_value);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        _dragging = false;
        ReleaseMouseCapture();
        Value = ValueAt(e);
        ValueCommitted?.Invoke(_value);
        InvalidateVisual();
    }
}

/// <summary>
/// iPhone-style trim bar: filmstrip (or waveform), yellow frame with grab handles, white playhead.
/// </summary>
public sealed class TrimBar : FrameworkElement
{
    private const double HandleW = 15;
    private static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    private static readonly Brush RedTint = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0x3A, 0x0C, 0x0A)));
    private static readonly Pen Hatch = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x45, 0x3A)), 2));
    private static readonly Pen ChevronLight = Freeze(new Pen(Brushes.White, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private bool _cutMode;

    /// <summary>When true the selection is the part that gets removed (red), not the part that is kept (yellow).</summary>
    public bool CutMode
    {
        get => _cutMode;
        set { _cutMode = value; InvalidateVisual(); }
    }
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)));
    private static readonly Brush Placeholder = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    private static readonly Brush WaveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly Pen Chevron = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x00)), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    private BitmapSource?[] _thumbs = Array.Empty<BitmapSource?>();
    private float[]? _wave;
    private int _drag; // 0 none, 1 start, 2 end, 3 playhead

    public double Duration { get; private set; } = 1;
    public double Start { get; private set; }
    public double End { get; private set; } = 1;
    public double Position { get; private set; }
    public double MinLength { get; set; } = 0.1;

    /// <summary>(start, end, which handle moved: 1 start / 2 end, final)</summary>
    public event Action<double, double, int, bool>? RangeChanged;
    public event Action<double, bool>? Scrubbed;

    public TrimBar()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public void Reset(double duration)
    {
        Duration = Math.Max(0.01, duration);
        MinLength = Math.Min(0.1, Duration / 4);
        Start = 0;
        End = Duration;
        Position = 0;
        _thumbs = Array.Empty<BitmapSource?>();
        _wave = null;
        InvalidateVisual();
    }

    public void SetRange(double start, double end)
    {
        Start = Math.Clamp(start, 0, Duration);
        End = Math.Clamp(end, Start, Duration);
        InvalidateVisual();
    }

    public void SetPosition(double t)
    {
        if (_drag == 3) return;
        Position = Math.Clamp(t, 0, Duration);
        InvalidateVisual();
    }

    public void SetThumbnailCount(int n)
    {
        _thumbs = new BitmapSource?[n];
        InvalidateVisual();
    }

    public void SetThumbnail(int i, BitmapSource bmp)
    {
        if (i < 0 || i >= _thumbs.Length) return;
        _thumbs[i] = bmp;
        InvalidateVisual();
    }

    public void SetWaveform(float[]? wave)
    {
        _wave = wave;
        InvalidateVisual();
    }

    public double StripWidth => Math.Max(1, ActualWidth - HandleW * 2);
    public double StripHeight => Math.Max(1, ActualHeight - 6);
    private Rect Strip => new(HandleW, 3, StripWidth, StripHeight);

    private double X(double t) => Strip.X + Strip.Width * (t / Duration);
    private double T(double x) => Math.Clamp((x - Strip.X) / Strip.Width, 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var s = Strip;
        if (s.Width <= 2) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // strip content
        dc.PushClip(new RectangleGeometry(s, 6, 6));
        dc.DrawRectangle(Placeholder, null, s);
        if (_wave != null && _wave.Length > 0)
        {
            double bw = s.Width / _wave.Length;
            for (int i = 0; i < _wave.Length; i++)
            {
                double hgt = Math.Max(1.5, _wave[i] * (s.Height - 10));
                dc.DrawRoundedRectangle(WaveBrush, null, new Rect(s.X + i * bw + bw * 0.18, s.Y + (s.Height - hgt) / 2, Math.Max(1, bw * 0.64), hgt), 1, 1);
            }
        }
        else if (_thumbs.Length > 0)
        {
            double cw = s.Width / _thumbs.Length;
            for (int i = 0; i < _thumbs.Length; i++)
            {
                var bmp = _thumbs[i];
                if (bmp == null) continue;
                var cell = new Rect(s.X + i * cw, s.Y, cw + 0.5, s.Height);
                // uniform-to-fill
                double ar = bmp.PixelWidth / (double)Math.Max(1, bmp.PixelHeight);
                double w = cell.Height * ar, h = cell.Height;
                if (w < cell.Width) { w = cell.Width; h = w / ar; }
                dc.PushClip(new RectangleGeometry(cell));
                dc.DrawImage(bmp, new Rect(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h));
                dc.Pop();
            }
        }
        dc.Pop();

        double xs = X(Start), xe = X(End);
        if (_cutMode)
        {
            // the selected part disappears: red tint + diagonal hatching
            var cut = new Rect(xs, s.Top, Math.Max(0, xe - xs), s.Height);
            dc.DrawRectangle(RedTint, null, cut);
            dc.PushClip(new RectangleGeometry(cut));
            for (double hx = cut.Left - cut.Height; hx < cut.Right; hx += 9)
                dc.DrawLine(Hatch, new Point(hx, cut.Bottom), new Point(hx + cut.Height, cut.Top));
            dc.Pop();
        }
        else
        {
            // dim outside the selection
            if (xs > s.Left) dc.DrawRectangle(Dim, null, new Rect(s.Left, s.Top, xs - s.Left, s.Height));
            if (xe < s.Right) dc.DrawRectangle(Dim, null, new Rect(xe, s.Top, s.Right - xe, s.Height));
        }

        // frame: top/bottom bars + handles (yellow = keep, red = cut out)
        bool trimmed = Start > 0.001 || End < Duration - 0.001;
        var frameBrush = _cutMode ? Red : Yellow;
        double top = 0, bottom = ActualHeight;
        var outer = new Rect(xs - HandleW, top, (xe - xs) + HandleW * 2, bottom - top);
        var geo = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(outer, 7, 7),
            new RectangleGeometry(new Rect(xs, top + 3, Math.Max(0, xe - xs), Math.Max(0, bottom - top - 6))));
        dc.DrawGeometry(trimmed || _cutMode ? frameBrush : new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)) { Opacity = 0.92 }, null, geo);

        // chevrons on the handles
        double cy = ActualHeight / 2;
        var chevron = _cutMode ? ChevronLight : Chevron;
        dc.DrawLine(chevron, new Point(xs - HandleW / 2 + 2, cy - 6), new Point(xs - HandleW / 2 - 2, cy));
        dc.DrawLine(chevron, new Point(xs - HandleW / 2 - 2, cy), new Point(xs - HandleW / 2 + 2, cy + 6));
        dc.DrawLine(chevron, new Point(xe + HandleW / 2 - 2, cy - 6), new Point(xe + HandleW / 2 + 2, cy));
        dc.DrawLine(chevron, new Point(xe + HandleW / 2 + 2, cy), new Point(xe + HandleW / 2 - 2, cy + 6));

        // playhead
        double xp = Math.Clamp(X(Position), s.Left, s.Right);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), null, new Rect(xp - 2.5, -2, 5, ActualHeight + 4), 2.5, 2.5);
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(xp - 1.5, -1, 3, ActualHeight + 2), 1.5, 1.5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        var p = e.GetPosition(this);
        double xs = X(Start), xe = X(End);
        if (p.X >= xs - HandleW - 4 && p.X <= xs + 4) _drag = 1;
        else if (p.X >= xe - 4 && p.X <= xe + HandleW + 4) _drag = 2;
        else _drag = 3;
        CaptureMouse();
        Apply(p.X, final: false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_drag == 0)
        {
            double xs = X(Start), xe = X(End);
            bool onHandle = (p.X >= xs - HandleW - 4 && p.X <= xs + 4) || (p.X >= xe - 4 && p.X <= xe + HandleW + 4);
            Cursor = onHandle ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        Apply(p.X, final: false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == 0) return;
        e.Handled = true;
        Apply(e.GetPosition(this).X, final: true);
        _drag = 0;
        ReleaseMouseCapture();
    }

    private void Apply(double x, bool final)
    {
        double t = T(x);
        switch (_drag)
        {
            case 1:
                Start = Math.Clamp(T(x + HandleW / 2), 0, End - MinLength);
                Position = Start;
                RangeChanged?.Invoke(Start, End, 1, final);
                break;
            case 2:
                End = Math.Clamp(T(x - HandleW / 2), Start + MinLength, Duration);
                Position = End;
                RangeChanged?.Invoke(Start, End, 2, final);
                break;
            case 3:
                Position = _cutMode ? Math.Clamp(t, 0, Duration) : Math.Clamp(t, Start, End);
                Scrubbed?.Invoke(Position, final);
                break;
        }
        InvalidateVisual();
    }

    public static string FormatTime(double sec, bool fraction = false)
    {
        if (!double.IsFinite(sec) || sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        string main = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
        return fraction ? main + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + (ts.Milliseconds / 100).ToString(CultureInfo.InvariantCulture) : main;
    }
}
