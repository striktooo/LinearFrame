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

    private BitmapSource? _bmp;
    private int _w, _h;          // the full picture, as shown (camera rotation applied)
    private bool _alpha;
    private double _zoom = 1;
    private Vector _offset;      // top-left corner of the picture, in DIPs
    private bool _fit = true;
    private double _dpi = 1;

    private bool _dragging;
    private Point _dragStart;
    private Vector _offsetAtDrag;

    private bool _animating;
    private double _animFrom, _animTo;
    private Point _animAt;
    private bool _animFit;
    private long _animStart;

    /// <summary>Zoom or position changed (the window updates its zoom label and fetches more pixels when needed).</summary>
    public event Action? ViewChanged;

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
        Snap();
        UpdateScaling();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void SetZoom(double z, Point at)
    {
        if (!HasPicture) return;
        z = Math.Clamp(z, FitZoom, MaxZoom);
        // the picture point under the cursor stays under the cursor
        double px = (at.X - _offset.X) * _dpi / _zoom, py = (at.Y - _offset.Y) * _dpi / _zoom;
        _zoom = z;
        _offset = new Vector(at.X - px * _zoom / _dpi, at.Y - py * _zoom / _dpi);
        _fit = Math.Abs(_zoom - FitZoom) < 1e-4;
        Clamp();
        Snap();
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

    /// <summary>Whole screen pixels for the corner: at 100 % every picture pixel lands on exactly one screen pixel.</summary>
    private void Snap() => _offset = new Vector(Math.Round(_offset.X * _dpi) / _dpi, Math.Round(_offset.Y * _dpi) / _dpi);

    private void UpdateScaling()
    {
        if (_bmp == null) return;
        double screenPerBitmapPixel = _zoom * _w / Math.Max(1, _bmp.PixelWidth);
        // smooth when shrinking or slightly enlarging; from 250 % on the pixels themselves are what you want to see
        RenderOptions.SetBitmapScalingMode(this, screenPerBitmapPixel >= 2.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void AnimateTo(double z, Point at, bool fit = false)
    {
        if (!HasPicture) return;
        z = Math.Clamp(z, FitZoom, MaxZoom);
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
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnAnimationFrame;
    }

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, Stopwatch.GetElapsedTime(_animStart).TotalMilliseconds / 170.0);
        double k = 1 - Math.Pow(1 - t, 3); // ease-out
        SetZoom(_animFrom * Math.Pow(_animTo / _animFrom, k), _animAt); // geometric: every frame zooms by the same ratio
        if (t < 1) return;
        StopAnimation();
        if (_animFit) ApplyFit();
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnAnimationFrame;
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the whole area takes the mouse
        if (!HasPicture) return;
        var r = new Rect(_offset.X, _offset.Y, _w * _zoom / _dpi, _h * _zoom / _dpi);
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
            Snap();
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
        StopAnimation();
        SetZoom(_zoom * Math.Pow(1.0018, e.Delta), e.GetPosition(this)); // one notch ≈ ×1.24, touchpads glide
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasPicture) return;
        var p = e.GetPosition(this);
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
        if (!_dragging)
        {
            Cursor = CanPan ? Cursors.Hand : null;
            return;
        }
        _offset = _offsetAtDrag + (e.GetPosition(this) - _dragStart);
        Clamp();
        Snap();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
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
    }
}
