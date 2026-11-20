using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// The emoji gallery, like the iPhone keyboard: a search field (Polish and English words), recently used first, then the
/// iOS categories, tabs to jump between them. All ~1900 emoji are one drawing over one shared sheet picture, so scrolling
/// is just moving it.
/// </summary>
public sealed class EmojiPicker : Border
{
    private readonly EmojiGrid _grid = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status;
    private readonly TextBox _search;
    private readonly TextBlock _hint;
    private bool _loading;

    public event Action<AppleEmoji.Entry>? Picked;
    internal bool Ready => _grid.HasContent;

    public EmojiPicker()
    {
        Width = 404;
        Height = 396;
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
        // the search field (a flat rounded field, like the iOS one)
        _search = new TextBox
        {
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
            FontSize = 13.5,
            Padding = new Thickness(30, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
                "<Border Background='#3A3A3C' CornerRadius='9'><ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center'" +
                " HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/></Border></ControlTemplate>"),
        };
        _hint = new TextBlock
        {
            Text = L.T("Edit.EmojiSearch"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(31, 0, 0, 0),
            IsHitTestVisible = false,
        };
        var glass = new UI.IconView { Icon = "search", Width = 15, Height = 15, Foreground = _hint.Foreground, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var searchBox = new Grid { Height = 30, Margin = new Thickness(2, 2, 2, 4) };
        searchBox.Children.Add(_search);
        searchBox.Children.Add(glass);
        searchBox.Children.Add(_hint);
        _search.TextChanged += (_, _) => OnSearch();
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _search.Text.Length > 0) { _search.Clear(); e.Handled = true; }
            else if (e.Key == Key.Enter && _grid.First is AppleEmoji.Entry first) { _grid.Pick(first); e.Handled = true; }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // the tabs (gone while searching)
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _tabs.Height = 38;
        root.Children.Add(searchBox);
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);
        Grid.SetRow(_scroll, 2);
        Grid.SetRow(_status, 2);
        root.Children.Add(_scroll);
        root.Children.Add(_status);
        Child = root;
        _grid.Picked += e =>
        {
            Remember(e);
            Picked?.Invoke(e);
        };
    }

    /// <summary>A caption is being typed into the search field (keys belong to it, not to the editor's shortcuts).</summary>
    internal bool IsTyping => _search.IsKeyboardFocusWithin;
    internal void TestSearch(string q) => _search.Text = q;

    private void OnSearch()
    {
        string q = _search.Text;
        _hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_grid.HasContent) return;
        if (q.Trim().Length == 0)
        {
            _grid.SetSearch(null, "");
            _tabs.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Collapsed;
        }
        else
        {
            var found = AppleEmoji.Search(q);
            _grid.SetSearch(found, L.T("Edit.EmojiResults"));
            _tabs.Visibility = Visibility.Collapsed;
            _status.Text = L.T("Edit.EmojiNone");
            _status.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _scroll.ScrollToVerticalOffset(0);
    }

    /// <summary>Fetches (first time only) and shows the gallery.</summary>
    public async Task LoadAsync()
    {
        if (_grid.HasContent) { _grid.SetRecent(Recent()); BuildRecentTab(); return; }
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

    private Button? _recentTab;

    /// <summary>The clock tab (recently used) is there once something was used.</summary>
    private void BuildRecentTab()
    {
        if (_recentTab != null) _recentTab.Visibility = Recent().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildTabs(BitmapSource sheet, IReadOnlyList<AppleEmoji.Entry> catalog)
    {
        _tabs.Children.Clear();
        _recentTab = new Button
        {
            Content = new UI.IconView { Icon = "clock", Width = 17, Height = 17, Foreground = Brushes.White, Opacity = 0.85 },
            ToolTip = L.T("Edit.EmojiTab.recent"),
            Width = 38,
            Height = 32,
            Margin = new Thickness(1, 0, 1, 0),
        };
        _recentTab.SetResourceReference(StyleProperty, "Hud.IconButton");
        _recentTab.Click += (_, _) => _scroll.ScrollToVerticalOffset(_grid.SectionTop("recent"));
        _tabs.Children.Add(_recentTab);
        BuildRecentTab();
        double k = sheet.PixelWidth / (double)(AppleEmoji.SheetCells * AppleEmoji.SheetCell);
        for (int i = 0; i < AppleEmoji.Tabs.Length; i++)
        {
            var (key, _, icon) = AppleEmoji.Tabs[i];
            var code = AppleEmoji.Code(icon);
            var e = catalog.FirstOrDefault(x => x.Unified == code || x.NonQualified == code || x.Unified.Replace("-fe0f", "") == code.Replace("-fe0f", ""));
            var face = new Rectangle { Width = 20, Height = 20, Opacity = 0.85 };
            if (e != null) face.Fill = EmojiGrid.BrushFor(sheet, e, k);
            var b = new Button { Content = face, ToolTip = L.T("Edit.EmojiTab." + key), Width = 38, Height = 32, Margin = new Thickness(1, 0, 1, 0) };
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
    private List<(string Key, string Title, List<AppleEmoji.Entry> Items)>? _normal;

    /// <summary>The first emoji shown (Enter in the search field picks it).</summary>
    public AppleEmoji.Entry? First => _sections.FirstOrDefault(s => s.Items.Count > 0).Items?.FirstOrDefault();

    public void Pick(AppleEmoji.Entry e) => Picked?.Invoke(e);

    /// <summary>Search results instead of the categories (null = back to the categories).</summary>
    public void SetSearch(List<AppleEmoji.Entry>? results, string title)
    {
        if (results == null)
        {
            if (_normal == null) return;
            _sections.Clear();
            _sections.AddRange(_normal);
            _normal = null;
        }
        else
        {
            _normal ??= _sections.ToList();
            _sections.Clear();
            _sections.Add(("search", title, results));
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

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
        var sections = _normal ?? _sections; // while a search shows, the categories wait aside
        sections[0] = (sections[0].Key, sections[0].Title, recent.Select(r => by.GetValueOrDefault(r)).Where(e => e != null).Select(e => e!).ToList());
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
