using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// The emoji gallery, like the iPhone keyboard: recently used first, then the iOS categories, tabs to jump between them.
/// All ~1900 emoji are one drawing over one shared sheet picture, so scrolling is just moving it.
/// </summary>
public sealed class EmojiPicker : Border
{
    private readonly EmojiGrid _grid = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status;
    private bool _loading;

    public event Action<AppleEmoji.Entry>? Picked;
    internal bool Ready => _grid.HasContent;

    public EmojiPicker()
    {
        Width = 404;
        Height = 360;
        CornerRadius = new CornerRadius(14);
        Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x23, 0x23, 0x26));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(8, 6, 8, 8);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Direction = 270 };

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _grid,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly,
        };
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(30, 0, 30, 0),
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_tabs);
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(_status, 1);
        root.Children.Add(_scroll);
        root.Children.Add(_status);
        Child = root;
        _grid.Picked += e =>
        {
            Remember(e);
            Picked?.Invoke(e);
        };
    }

    /// <summary>Fetches (first time only) and shows the gallery.</summary>
    public async Task LoadAsync()
    {
        if (_grid.HasContent) { _grid.SetRecent(Recent()); return; }
        if (_loading) return;
        _loading = true;
        _status.Text = AppleEmoji.IsDownloaded ? L.T("Edit.EmojiLoading") : L.T("Edit.EmojiDownloading");
        _status.Visibility = Visibility.Visible;
        try
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var catalogTask = AppleEmoji.CatalogAsync();
            var sheetTask = AppleEmoji.SheetAsync((int)Math.Ceiling(32 * dpi));
            var catalog = await catalogTask;
            var sheet = await sheetTask;
            if (catalog == null || sheet == null)
            {
                _status.Text = L.T("Edit.EmojiOffline");
                return;
            }
            _grid.SetContent(catalog, sheet, Recent());
            _status.Visibility = Visibility.Collapsed;
            BuildTabs(sheet, catalog);
        }
        finally { _loading = false; }
    }

    private void BuildTabs(BitmapSource sheet, IReadOnlyList<AppleEmoji.Entry> catalog)
    {
        _tabs.Children.Clear();
        double k = sheet.PixelWidth / (double)(AppleEmoji.SheetCells * AppleEmoji.SheetCell);
        for (int i = 0; i < AppleEmoji.Tabs.Length; i++)
        {
            var (key, _, icon) = AppleEmoji.Tabs[i];
            var code = AppleEmoji.Code(icon);
            var e = catalog.FirstOrDefault(x => x.Unified == code || x.NonQualified == code || x.Unified.Replace("-fe0f", "") == code.Replace("-fe0f", ""));
            var face = new Rectangle { Width = 20, Height = 20, Opacity = 0.85 };
            if (e != null) face.Fill = EmojiGrid.BrushFor(sheet, e, k);
            var b = new Button { Content = face, ToolTip = L.T("Edit.EmojiTab." + key), Width = 42, Height = 32, Margin = new Thickness(1, 0, 1, 0) };
            b.SetResourceReference(StyleProperty, "Hud.IconButton");
            int index = i;
            b.Click += (_, _) => _scroll.ScrollToVerticalOffset(_grid.SectionTop(key));
            _tabs.Children.Add(b);
        }
    }

    private static List<string> Recent() => App.Instance?.Settings.RecentEmoji.ToList() ?? new();

    private static void Remember(AppleEmoji.Entry e)
    {
        var s = App.Instance?.Settings;
        if (s == null) return;
        s.RecentEmoji.RemoveAll(x => x == e.Unified);
        s.RecentEmoji.Insert(0, e.Unified);
        if (s.RecentEmoji.Count > 24) s.RecentEmoji.RemoveRange(24, s.RecentEmoji.Count - 24);
        s.Save();
    }

    private sealed class Rectangle : FrameworkElement
    {
        public Brush? Fill { get; set; }
        protected override void OnRender(DrawingContext dc) { if (Fill != null) dc.DrawRectangle(Fill, null, new Rect(RenderSize)); }
    }
}

internal sealed class EmojiGrid : FrameworkElement
{
    private const double Cell = 42, Header = 30, Pic = 32;
    private static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush HeaderInk = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)));

    private readonly DrawingVisual _hover = new();
    private readonly List<(string Key, string Title, List<AppleEmoji.Entry> Items)> _sections = new();
    private IReadOnlyList<AppleEmoji.Entry>? _catalog;
    private BitmapSource? _sheet;
    private double _k = 1;
    private readonly Dictionary<AppleEmoji.Entry, Brush> _brushes = new();
    private int _hot = -1;

    public event Action<AppleEmoji.Entry>? Picked;
    public bool HasContent => _sheet != null;

    public EmojiGrid()
    {
        AddVisualChild(_hover);
        Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _hover;

    public static Brush BrushFor(BitmapSource sheet, AppleEmoji.Entry e, double k)
    {
        var b = new ImageBrush(sheet)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect((e.SheetX * AppleEmoji.SheetCell + 1) * k, (e.SheetY * AppleEmoji.SheetCell + 1) * k, 64 * k, 64 * k),
            Stretch = Stretch.Fill,
        };
        b.Freeze();
        return b;
    }

    private Brush Brush(AppleEmoji.Entry e)
    {
        if (!_brushes.TryGetValue(e, out var b)) _brushes[e] = b = BrushFor(_sheet!, e, _k);
        return b;
    }

    public void SetContent(IReadOnlyList<AppleEmoji.Entry> catalog, BitmapSource sheet, List<string> recent)
    {
        _catalog = catalog;
        _sheet = sheet;
        _k = sheet.PixelWidth / (double)(AppleEmoji.SheetCells * AppleEmoji.SheetCell);
        _brushes.Clear();
        _sections.Clear();
        _sections.Add(("recent", L.T("Edit.EmojiTab.recent"), new List<AppleEmoji.Entry>()));
        foreach (var (key, sources, _) in AppleEmoji.Tabs)
            _sections.Add((key, L.T("Edit.EmojiTab." + key), catalog.Where(e => sources.Contains(e.Category)).ToList()));
        SetRecent(recent);
    }

    public void SetRecent(List<string> recent)
    {
        if (_catalog == null || _sections.Count == 0) return;
        var by = _catalog.ToDictionary(e => e.Unified, e => e);
        _sections[0] = (_sections[0].Key, _sections[0].Title, recent.Select(r => by.GetValueOrDefault(r)).Where(e => e != null).Select(e => e!).ToList());
        InvalidateMeasure();
        InvalidateVisual();
    }

    private int Columns => Math.Max(1, (int)((ActualWidth > 0 ? ActualWidth : 380) / Cell));

    public double SectionTop(string key)
    {
        double y = 0;
        foreach (var s in _sections)
        {
            if (s.Items.Count == 0) continue;
            if (s.Key == key) return y;
            y += Header + Math.Ceiling(s.Items.Count / (double)Columns) * Cell;
        }
        return y;
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 380 : available.Width;
        int cols = Math.Max(1, (int)(w / Cell));
        double h = _sections.Where(s => s.Items.Count > 0).Sum(s => Header + Math.Ceiling(s.Items.Count / (double)cols) * Cell);
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_sheet == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int cols = Columns;
        double x0 = (ActualWidth - cols * Cell) / 2;
        double y = 0;
        foreach (var s in _sections)
        {
            if (s.Items.Count == 0) continue;
            var ft = new FormattedText(s.Title.ToUpper(CultureInfo.CurrentUICulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Ui, 11, HeaderInk, dpi);
            dc.DrawText(ft, new Point(x0 + 6, y + Header - ft.Height - 6));
            y += Header;
            for (int i = 0; i < s.Items.Count; i++)
            {
                double cx = x0 + (i % cols) * Cell, cy = y + (i / cols) * Cell;
                dc.DrawRectangle(Brush(s.Items[i]), null, new Rect(cx + (Cell - Pic) / 2, cy + (Cell - Pic) / 2, Pic, Pic));
            }
            y += Math.Ceiling(s.Items.Count / (double)cols) * Cell;
        }
    }

    private (AppleEmoji.Entry? Entry, Rect Cell) HitTest(Point p)
    {
        int cols = Columns;
        double x0 = (ActualWidth - cols * Cell) / 2;
        double y = 0;
        foreach (var s in _sections)
        {
            if (s.Items.Count == 0) continue;
            y += Header;
            double rows = Math.Ceiling(s.Items.Count / (double)cols);
            if (p.Y >= y && p.Y < y + rows * Cell)
            {
                int c = (int)((p.X - x0) / Cell), r = (int)((p.Y - y) / Cell);
                int i = r * cols + c;
                if (c >= 0 && c < cols && i >= 0 && i < s.Items.Count) return (s.Items[i], new Rect(x0 + c * Cell, y + r * Cell, Cell, Cell));
                return (null, Rect.Empty);
            }
            y += rows * Cell;
        }
        return (null, Rect.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (entry, cell) = HitTest(e.GetPosition(this));
        int hot = entry == null ? -1 : (int)(cell.Y * 10000 + cell.X);
        if (hot == _hot) return;
        _hot = hot;
        using var dc = _hover.RenderOpen();
        if (entry != null) dc.DrawRoundedRectangle(HoverFill, null, new Rect(cell.X + 2, cell.Y + 2, Cell - 4, Cell - 4), 8, 8);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = -1;
        using var dc = _hover.RenderOpen();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var (entry, _) = HitTest(e.GetPosition(this));
        if (entry != null) Picked?.Invoke(entry);
    }
}
