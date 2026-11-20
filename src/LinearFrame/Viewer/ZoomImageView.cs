using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Viewer;

/// <summary>
/// One picture on a canvas, like Preview or Photos: it fits the window (never blown up past 100 %), the wheel zooms where
/// the cursor is, dragging pans, a double-click switches between the whole picture and actual pixels. Zoom is screen
/// pixels per picture pixel, so 100 % really is one pixel for one pixel.
/// </summary>
public sealed class ZoomImageView : FrameworkElement
{
    private const double MaxZoom = 32;

    /// <summary>How far out it zooms: a fifth of the whole-picture view (or of 100 % for a small picture), so the picture
    /// can also be looked at smaller than the window.</summary>
    private double MinZoom => Math.Max(0.01, Math.Min(FitZoom, 1) * 0.2);

    private BitmapSource? _bmp;
    private int _w, _h;          // the full picture, as shown (camera rotation applied)
    private bool _alpha;
    private double _zoom = 1;
    private Vector _offset;      // top-left corner of the picture, in DIPs - exact (drawn on whole screen pixels: Shown)
    private bool _fit = true;
    private double _dpi = 1;

    private bool _dragging;
    private Point _dragStart;
    private Vector _offsetAtDrag;

    // Zoom in motion. An ease goes to a given zoom (buttons, double-click); a glide follows the mouse wheel - every
    // further notch only moves its goal, so turning the wheel is one smooth movement instead of a jump per notch.
    private enum Motion { None, Ease, Glide }
    private Motion _motion;
    private double _animFrom, _animTo;
    private Point _animAt;
    private bool _animFit;
    private long _animStart;
    private double _glideTo;       // the zoom the wheel has been turned to
    private Point _glideAt;        // where the cursor is (view)...
    private Point _glidePic;       // ...and the picture point that stays under it
    private long _glideFrame;

    /// <summary>How quickly a glide closes in on its goal (ms for about two thirds of what is left).</summary>
    private const double GlideMs = 50;
    /// <summary>A wheel step this big is a notch of a mouse wheel; touchpads and free-spinning wheels send many small ones.</summary>
    private const int WheelNotch = 100;

    /// <summary>Zoom or position changed (the window updates its zoom label and fetches more pixels when needed).</summary>
    public event Action? ViewChanged;

    // Live Text gets the mouse first: a press on a word starts a text selection instead of moving the picture
    public Func<Point, int, bool>? TextDown;
    public Action<Point>? TextDrag;
    public Action? TextUp;
    public Func<Point, bool>? IsOverText;
    private bool _selecting;

    /// <summary>A point of the picture (its own pixels) → this view (DIPs), and back.</summary>
    public Point ToView(Point pic) => new(Shown.X + pic.X * _zoom / _dpi, Shown.Y + pic.Y * _zoom / _dpi);
    public Point ToPicture(Point view) => new((view.X - Shown.X) * _dpi / _zoom, (view.Y - Shown.Y) * _dpi / _zoom);

    /// <summary>
    /// Where the picture's corner is drawn: on whole screen pixels (at 100 % every picture pixel lands on exactly one screen
    /// pixel). The exact position is kept apart, so zooming step after step doesn't drift away from the cursor.
    /// </summary>
    private Vector Shown => new(Math.Round(_offset.X * _dpi) / _dpi, Math.Round(_offset.Y * _dpi) / _dpi);

    public double Zoom => _zoom;
    public bool IsFit => _fit;
    public bool HasPicture => _bmp != null && _w > 0 && _h > 0;
    public int PictureWidth => _w;
    public int PictureHeight => _h;
    /// <summary>Pixels the bitmap on screen really has (less than the picture while only the screen-sized copy is loaded).</summary>
    internal int BitmapWidth => _bmp?.PixelWidth ?? 0;

    /// <summary>The bitmap on screen is being stretched: a copy with more pixels would look sharper.</summary>
    public bool WantsMorePixels => _bmp != null && _bmp.PixelWidth < _w && _zoom * _w / _bmp.PixelWidth > 1.02;

    public ZoomImageView()
    {
        ClipToBounds = true;
        Focusable = false;
        Loaded += (_, _) =>
        {
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (_fit) ApplyFit(); else Clamp();
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpi = newDpi.DpiScaleX;
        if (_fit) ApplyFit(); else { Clamp(); InvalidateVisual(); }
    }

    // ------------------------------------------------------------------ picture

    public void SetPicture(BitmapSource bmp, int width, int height, bool alpha)
    {
        StopAnimation();
        _bmp = bmp;
        _w = width;
        _h = height;
        _alpha = alpha;
        _fit = true;
        ApplyFit();
    }

    /// <summary>The same picture with more pixels (the full-size copy arrived) - the view stays exactly where it is.</summary>
    public void Upgrade(BitmapSource bmp)
    {
        _bmp = bmp;
        UpdateScaling();
        InvalidateVisual();
    }

    public void Clear()
    {
        StopAnimation();
        _bmp = null;
        _w = _h = 0;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ zoom

    /// <summary>Zoom at which the whole picture fits (at most 100 %).</summary>
    public double FitZoom
    {
        get
        {
            if (_w <= 0 || _h <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return 1;
            return Math.Min(1, Math.Min(ActualWidth * _dpi / _w, ActualHeight * _dpi / _h));
        }
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    public void ShowWhole() => AnimateTo(FitZoom, Center, fit: true);
    public void ShowActualPixels() => AnimateTo(1, Center);
    public void ZoomBy(double factor) => AnimateTo(_zoom * factor, Center);

    /// <summary>Double-click / the zoom label: whole picture ⇄ actual pixels (a small picture goes to 200 %).</summary>
    public void ToggleZoom(Point? at = null)
    {
        var p = at ?? Center;
        double fit = FitZoom;
        if (_fit || Math.Abs(_zoom - fit) < 1e-3) AnimateTo(fit >= 0.999 ? 2 : 1, p);
        else AnimateTo(fit, p, fit: true);
    }

    private void ApplyFit()
    {
        if (!HasPicture) { InvalidateVisual(); return; }
        _fit = true;
        _zoom = FitZoom;
        _offset = new Vector((ActualWidth - _w * _zoom / _dpi) / 2, (ActualHeight - _h * _zoom / _dpi) / 2);
        UpdateScaling();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Zooms about a point of the view: the picture point the user sees there stays there.</summary>
    private void SetZoom(double z, Point at) => SetZoomAbout(z, at, ToPicture(at));

    /// <summary>Zooms so that the picture point <paramref name="pic"/> lies at <paramref name="at"/> (view).</summary>
    private void SetZoomAbout(double z, Point at, Point pic)
    {
        if (!HasPicture) return;
        z = Math.Clamp(z, MinZoom, MaxZoom);
        _zoom = z;
        _offset = new Vector(at.X - pic.X * _zoom / _dpi, at.Y - pic.Y * _zoom / _dpi);
        _fit = Math.Abs(_zoom - FitZoom) < 1e-4;
        Clamp();
        UpdateScaling();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>No empty band next to a picture that is bigger than the window; a smaller one stays centred.</summary>
    private void Clamp()
    {
        double pw = _w * _zoom / _dpi, ph = _h * _zoom / _dpi;
        double x = pw <= ActualWidth ? (ActualWidth - pw) / 2 : Math.Clamp(_offset.X, ActualWidth - pw, 0);
        double y = ph <= ActualHeight ? (ActualHeight - ph) / 2 : Math.Clamp(_offset.Y, ActualHeight - ph, 0);
        _offset = new Vector(x, y);
    }

    private void UpdateScaling()
    {
        if (_bmp == null) return;
        double screenPerBitmapPixel = _zoom * _w / Math.Max(1, _bmp.PixelWidth);
        // smooth when shrinking or slightly enlarging; from 250 % on the pixels themselves are what you want to see.
        // While the zoom is moving the graphics card scales (the careful scaling is worked out anew for every size - on
        // every frame of a movement that made it stutter); the frame it comes to rest on is the careful one again.
        var mode = screenPerBitmapPixel >= 2.5 ? BitmapScalingMode.NearestNeighbor
            : _motion != Motion.None ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality;
        if (RenderOptions.GetBitmapScalingMode(this) != mode) RenderOptions.SetBitmapScalingMode(this, mode);
    }

    private void AnimateTo(double z, Point at, bool fit = false)
    {
        if (!HasPicture) return;
        z = Math.Clamp(z, MinZoom, MaxZoom);
        if (!IsLoaded || Math.Abs(z - _zoom) < 1e-6)
        {
            StopAnimation();
            if (fit) ApplyFit(); else SetZoom(z, at);
            return;
        }
        _animFrom = _zoom;
        _animTo = z;
        _animAt = at;
        _animFit = fit;
        _animStart = Stopwatch.GetTimestamp();
        StartMotion(Motion.Ease);
    }

    /// <summary>The wheel was turned: the zoom glides to <paramref name="z"/>, about the point under the cursor.</summary>
    private void GlideTo(double z, Point at)
    {
        if (!HasPicture) return;
        AimGlide(z, at);
        if (_motion == Motion.Glide) return;
        _glideFrame = Stopwatch.GetTimestamp();
        StartMotion(Motion.Glide);
    }

    private void AimGlide(double z, Point at)
    {
        // the same spot as for the notch before: the picture point under it stays the one it was (no creeping)
        if (_motion != Motion.Glide || (at - _glideAt).Length > 0.5) _glidePic = ToPicture(at);
        _glideAt = at;
        _glideTo = Math.Clamp(z, MinZoom, MaxZoom);
    }

    private void StartMotion(Motion motion)
    {
        bool running = _motion != Motion.None;
        _motion = motion;
        if (!running) CompositionTarget.Rendering += OnAnimationFrame;
    }

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        if (_motion == Motion.Glide)
        {
            long now = Stopwatch.GetTimestamp();
            double ms = Math.Min(50, Stopwatch.GetElapsedTime(_glideFrame, now).TotalMilliseconds);
            _glideFrame = now;
            if (GlideStep(ms)) StopAnimation();
            return;
        }
        double t = Math.Min(1, Stopwatch.GetElapsedTime(_animStart).TotalMilliseconds / 170.0);
        double k = 1 - Math.Pow(1 - t, 3); // ease-out
        SetZoom(_animFrom * Math.Pow(_animTo / _animFrom, k), _animAt); // geometric: every frame zooms by the same ratio
        if (t < 1) return;
        bool fit = _animFit;
        StopAnimation();
        if (fit) ApplyFit();
    }

    /// <summary>
    /// One frame of a glide, <paramref name="ms"/> after the one before: a share of what is left to the goal - big
    /// steps first, ever smaller ones as it arrives (measured as a ratio, so zooming in and out feel alike). True
    /// once it is there.
    /// </summary>
    private bool GlideStep(double ms)
    {
        double left = Math.Log(_glideTo / _zoom);
        double step = left * (1 - Math.Exp(-ms / GlideMs));
        bool there = Math.Abs(left - step) < 0.002; // within a fifth of a percent
        SetZoomAbout(there ? _glideTo : _zoom * Math.Exp(step), _glideAt, _glidePic);
        return there;
    }

    private void StopAnimation()
    {
        if (_motion == Motion.None) return;
        _motion = Motion.None;
        CompositionTarget.Rendering -= OnAnimationFrame;
        // at rest the picture is drawn with the careful scaling again
        UpdateScaling();
        InvalidateVisual();
    }

    // ---- for the self-test (no window there, so no frames): a glide aimed and stepped by hand
    internal void GlideForTest(double z, Point at)
    {
        AimGlide(z, at);
        _motion = Motion.Glide;
        UpdateScaling();
    }

    internal bool GlideStepForTest(double ms)
    {
        bool there = GlideStep(ms);
        if (there) { _motion = Motion.None; UpdateScaling(); }
        return there;
    }

    internal double GlideGoal => _glideTo;
    internal BitmapScalingMode Scaling => RenderOptions.GetBitmapScalingMode(this);

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the whole area takes the mouse
        if (!HasPicture) return;
        var r = new Rect(Shown.X, Shown.Y, _w * _zoom / _dpi, _h * _zoom / _dpi);
        if (_alpha) dc.DrawRectangle(Theme.IsDark ? CheckerDark : CheckerLight, null, r);
        dc.DrawImage(_bmp, r);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fit) ApplyFit();
        else
        {
            Clamp();
                InvalidateVisual();
            ViewChanged?.Invoke();
        }
    }

    private static readonly Brush CheckerLight = MakeChecker(Colors.White, Color.FromRgb(0xE2, 0xE2, 0xE6));
    private static readonly Brush CheckerDark = MakeChecker(Color.FromRgb(0x2E, 0x2E, 0x31), Color.FromRgb(0x24, 0x24, 0x27));

    private static Brush MakeChecker(Color a, Color b)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            var light = new SolidColorBrush(a);
            var dark = new SolidColorBrush(b);
            dc.DrawRectangle(light, null, new Rect(0, 0, 16, 16));
            dc.DrawRectangle(dark, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(dark, null, new Rect(8, 8, 8, 8));
        }
        var brush = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ mouse

    private bool CanPan => HasPicture && (_w * _zoom / _dpi > ActualWidth + 0.5 || _h * _zoom / _dpi > ActualHeight + 0.5);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasPicture) return;
        e.Handled = true;
        var at = e.GetPosition(this);
        double factor = Math.Pow(1.0018, e.Delta); // one notch ≈ ×1.24
        if (!IsLoaded || Math.Abs(e.Delta) < WheelNotch)
        {
            // a touchpad (or a free-spinning wheel) sends many small steps: they are followed as they come
            StopAnimation();
            SetZoom(_zoom * factor, at);
            return;
        }
        // a wheel notch: the zoom glides there; a notch more while it is still on its way only moves the goal
        GlideTo((_motion == Motion.Glide ? _glideTo : _zoom) * factor, at);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasPicture) return;
        var p = e.GetPosition(this);
        if (TextDown != null && TextDown(p, e.ClickCount))
        {
            _selecting = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            ToggleZoom(p);
            return;
        }
        if (!CanPan) return;
        StopAnimation();
        _dragging = true;
        _dragStart = p;
        _offsetAtDrag = _offset;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_selecting)
        {
            TextDrag?.Invoke(e.GetPosition(this));
            return;
        }
        if (!_dragging)
        {
            Cursor = IsOverText?.Invoke(e.GetPosition(this)) == true ? Cursors.IBeam : CanPan ? Cursors.Hand : null;
            return;
        }
        _offset = _offsetAtDrag + (e.GetPosition(this) - _dragStart);
        Clamp();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_selecting)
        {
            _selecting = false;
            ReleaseMouseCapture();
            TextUp?.Invoke();
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = CanPan ? Cursors.Hand : null;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        if (_selecting) { _selecting = false; TextUp?.Invoke(); }
    }
}
