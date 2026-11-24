using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// The caption tool - one and the same in the video editor and the picture editor: the text (Enter = done, Shift+Enter =
/// a new line), the iPhone emoji gallery (an emoji goes in at the caret; recent ones and a search), outline / plate /
/// plain, TikTok's colours and the size. It only tells what was chosen; the editor applies it to its caption, which
/// <see cref="Captions"/> draws.
/// </summary>
public partial class CaptionPanel : UserControl
{
    /// <summary>The size slider's range: font size as a part of the canvas height.</summary>
    public const double MinSize = 0.018, MaxSize = 0.14;

    private bool _suppress;
    private int _caret = -1;
    private string? _font;

    public event Action<string>? TextEdited;
    public event Action<CaptionStyle>? StyleChosen;
    /// <summary>
    /// A colour was picked: for the selected part of the text (start, length), or - nothing or everything selected,
    /// length 0 - for the whole caption. The last flag: false while the mouse is still moving over the palette (the
    /// caption follows it), true for the colour that was chosen - one change to undo.
    /// </summary>
    public event Action<Color, int, int, bool>? ColorChosen;
    /// <summary>A font was picked (an imported one's id; null = TikTok Sans).</summary>
    public event Action<string?>? FontChosen;
    /// <summary>The duplicate button (only where <see cref="LayerTools"/> is on).</summary>
    public event Action? DuplicateRequested;
    /// <summary>A new size (a part of the canvas height); true when the slider was let go.</summary>
    public event Action<double, bool>? SizeChosen;
    public event Action? DeleteRequested;
    /// <summary>Done (the button, Enter or Esc in the text).</summary>
    public event Action? Finished;
    /// <summary>An emoji's picture arrived (downloaded the first time): the caption needs drawing again.</summary>
    public event Action? Redraw;

    /// <summary>Typing goes to the caption (or the emoji search): editor shortcuts must stay away.</summary>
    public bool IsTyping => TextEdit.IsKeyboardFocusWithin || Picker.IsTyping;

    public CaptionPanel()
    {
        InitializeComponent();
        Picker.Picked += OnEmojiPicked;
        PopupToggle.Attach(EmojiPopup, EmojiBtn);
        TextSize.ValueChanging += v => { if (!_suppress) SizeChosen?.Invoke(SizeFrom(v), false); };
        TextSize.ValueCommitted += v => { if (!_suppress) SizeChosen?.Invoke(SizeFrom(v), true); };
        BuildSwatches();
        Palette.Picked += OnPalettePicked;
        ColorPopup.CustomPopupPlacementCallback = PlacePalette;
        ColorPopup.Closed += (_, _) =>
        {
            Palette.Close();
            PopupToggle.Closed(_wheel);
        };
    }

    /// <summary>
    /// Where the palette opens: under the colour dots when the screen has the room - there it lies over the time line and
    /// leaves the picture alone, with the caption that is being coloured in sight; otherwise above them. Centred on the
    /// dots either way. (The sizes are in screen pixels and include the panel's margin - its shadow's room.)
    /// </summary>
    private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlacePalette(Size popup, Size target, Point offset)
    {
        double x = (target.Width - popup.Width) / 2;
        double gap = (6 - Palette.Margin.Top) * VisualTreeHelper.GetDpi(this).DpiScaleY;
        return new[]
        {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(x, target.Height + gap), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(x, -popup.Height - gap), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
        };
    }

    public static double SizeFrom(double v) => MinSize + v * (MaxSize - MinSize);
    public static double SizeTo(double s) => Math.Clamp((s - MinSize) / (MaxSize - MinSize), 0, 1);

    /// <summary>The video editor's captions can be duplicated like its other layers.</summary>
    public bool LayerTools
    {
        get => DuplicateBtn.Visibility == Visibility.Visible;
        set => DuplicateBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows a caption's settings (without reporting them back as changes).</summary>
    public void Show(ICaption c)
    {
        _shown = c;
        _suppress = true;
        try
        {
            if (TextEdit.Text != c.Text) TextEdit.Text = c.Text;
            StyleStroke.IsChecked = c.Style == CaptionStyle.Stroke;
            StyleFill.IsChecked = c.Style == CaptionStyle.Fill;
            StyleNone.IsChecked = c.Style == CaptionStyle.None;
            TextSize.Value = SizeTo(c.Size);
            MarkSwatch(c.Color);
            ShowFont(c.Font);
        }
        finally { _suppress = false; }
    }

    private void ShowFont(string? font)
    {
        _font = FontBook.Find(font)?.Id;
        FontBtn.ToolTip = L.F("Edit.FontTip", FontBook.NameOf(_font));
    }

    /// <summary>The size changed on the picture (the caption's corner dragged).</summary>
    public void ShowSize(double size)
    {
        _suppress = true;
        TextSize.Value = SizeTo(size);
        _suppress = false;
    }

    /// <summary>The cursor into the text, all of it selected (a new caption's placeholder is simply typed over).</summary>
    public void FocusText() => Dispatcher.BeginInvoke(new Action(() => { TextEdit.Focus(); TextEdit.SelectAll(); }), DispatcherPriority.Input);

    /// <summary>Closes what the tool has open (the emoji gallery, the palette).</summary>
    public void CloseEmoji() => EmojiPopup.IsOpen = ColorPopup.IsOpen = false;

    // ---- for the self-test
    internal void TestType(string text) => TextEdit.Text = text;
    /// <summary>A part of the text selected and a swatch clicked.</summary>
    internal void TestColor(Color color, int start, int length)
    {
        TextEdit.Select(start, length);
        PickColor(color);
    }
    /// <summary>
    /// The wheel clicked with this part of the text selected - without the palette's own window coming up (a test must
    /// not put anything on the screen) - and then what the palette reports.
    /// </summary>
    internal void TestPaletteFor(int start, int length)
    {
        TextEdit.Select(start, length);
        (_paletteStart, _paletteLength) = Target();
        Palette.Open(TargetColor());
    }
    internal void TestPalettePick(Color color, bool final) => OnPalettePicked(color, final);
    /// <summary>The colour the palette shows as the one in use.</summary>
    internal Color? TestPaletteShows => Palette.Field.Current;
    /// <summary>Where the palette would open for a popup and a row of dots of these sizes (screen pixels, from the dots' corner): the places in the order they are tried.</summary>
    internal Point[] TestPalettePlaces(Size popup, Size target) => PlacePalette(popup, target, default).Select(p => p.Point).ToArray();
    internal bool TestOwnColorShown => _wheelDot.Visibility == Visibility.Visible;
    internal int TestDots => Swatches.Children.Count;
    internal void TestFont(string? id) => PickFont(id);
    internal void TestOpenEmoji() => Emoji_Click(this, new RoutedEventArgs());
    internal bool EmojiReady => Picker.Ready;
    internal void TestPick(AppleEmoji.Entry e) => OnEmojiPicked(e);

    // ------------------------------------------------------------------ text

    private void TextEdit_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppress) TextEdited?.Invoke(TextEdit.Text);
    }

    private void TextEdit_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int at = TextEdit.CaretIndex;
            TextEdit.Text = TextEdit.Text.Insert(at, "\n");
            TextEdit.CaretIndex = at + 1;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ColorPopup.IsOpen)
        {
            // Esc puts the palette away first; once more and the caption is done
            e.Handled = true;
            ColorPopup.IsOpen = false;
        }
        else if (e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            Done_Click(this, new RoutedEventArgs());
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        EmojiPopup.IsOpen = ColorPopup.IsOpen = false;
        Finished?.Invoke();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        EmojiPopup.IsOpen = ColorPopup.IsOpen = false;
        DeleteRequested?.Invoke();
    }

    // ------------------------------------------------------------------ look

    private void Style_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not FrameworkElement { Tag: string tag }) return;
        StyleChosen?.Invoke(Enum.Parse<CaptionStyle>(tag));
    }

    // ---- colour: white, black, and a wheel that opens the full palette (every colour, and the ones used lately)

    private Border _wheel = null!, _wheelRing = null!, _wheelDot = null!;
    private Color _color = Colors.White;
    // the caption on show (its coloured parts are read when the palette opens)
    private ICaption? _shown;
    // the part of the text the palette was opened for (the selection as it was then)
    private int _paletteStart, _paletteLength;

    private void BuildSwatches()
    {
        foreach (var c in new[] { Colors.White, Colors.Black })
        {
            var color = c;
            var dot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(3, 2, 3, 2),
                Cursor = Cursors.Hand,
                Tag = c,
            };
            dot.MouseLeftButtonUp += (_, _) => PickColor(color);
            Swatches.Children.Add(dot);
        }
        // the wheel: all hues around; with a colour of your own in use, that colour sits in its middle
        _wheelDot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _wheelRing = new Border { CornerRadius = new CornerRadius(11), BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        var face = new Border { CornerRadius = new CornerRadius(11), Background = new ImageBrush(ColorPickerPanel.Wheel()) { Stretch = Stretch.UniformToFill } };
        var grid = new Grid();
        grid.Children.Add(face);
        grid.Children.Add(_wheelRing);
        grid.Children.Add(_wheelDot);
        _wheel = new Border { Width = 22, Height = 22, Margin = new Thickness(3, 2, 3, 2), Cursor = Cursors.Hand, Child = grid, ToolTip = L.T("Edit.ColorMore"), Background = Brushes.Transparent };
        _wheel.MouseLeftButtonUp += (_, _) => OpenPalette();
        Swatches.Children.Add(_wheel);
    }

    /// <summary>The selection a colour goes to: a part of the text, or (nothing or everything selected) the whole caption = length 0.</summary>
    private (int Start, int Length) Target()
    {
        int start = TextEdit.SelectionStart, length = TextEdit.SelectionLength;
        return length > 0 && length < TextEdit.Text.Length ? (start, length) : (0, 0);
    }

    /// <summary>
    /// White or black was clicked. A part of the text is selected: only that part takes the colour. Nothing selected (or
    /// all of it, like right after a caption is made): the whole caption does.
    /// </summary>
    private void PickColor(Color color)
    {
        var (start, length) = Target();
        if (length == 0) MarkSwatch(color);
        ColorChosen?.Invoke(color, start, length, true);
    }

    /// <summary>The colour in use where a colour would go now: the selected part's own (as its first letter has it), else the caption's.</summary>
    private Color TargetColor()
    {
        var (start, length) = Target();
        return length > 0 && ColorSpans.At(_shown?.Spans, start) is Color own ? own : _color;
    }

    private void OpenPalette()
    {
        if (PopupToggle.JustClosed(_wheel)) return; // the click that closed it
        EmojiPopup.IsOpen = false;
        (_paletteStart, _paletteLength) = Target();
        Palette.Open(TargetColor());
        ColorPopup.IsOpen = true;
    }

    /// <summary>The palette: the caption follows the mouse over it, and takes the colour it is let go on (or a recent one clicked).</summary>
    private void OnPalettePicked(Color color, bool final)
    {
        if (_paletteLength == 0) MarkSwatch(color);
        ColorChosen?.Invoke(color, _paletteStart, _paletteLength, final);
    }

    // ------------------------------------------------------------------ font

    private void Font_Click(object sender, RoutedEventArgs e)
    {
        var menu = new NativeMenu();
        menu.Add("TikTok Sans", () => PickFont(null), isChecked: _font == null);
        foreach (var f in FontBook.Imported)
        {
            string id = f.Id;
            menu.Add(f.Name.Replace("&", "&&"), () => PickFont(id), isChecked: string.Equals(_font, id, StringComparison.OrdinalIgnoreCase));
        }
        menu.Separator();
        menu.Add(L.T("Edit.FontImport"), ImportFont);
        menu.ShowFor(FontBtn, forceDark: true, above: true);
    }

    private void PickFont(string? id)
    {
        ShowFont(id);
        FontChosen?.Invoke(_font);
    }

    /// <summary>Font files of your own (.ttf, .otf): copied into the app's folder and used from there.</summary>
    private void ImportFont()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Edit.FontPick"),
            Filter = L.T("Edit.FontFilter") + "|" + string.Join(";", FontBook.Extensions.Select(x => "*" + x)),
            Multiselect = true,
        };
        var owner = Window.GetWindow(this);
        if (dlg.ShowDialog(owner) != true) return;
        FontBook.Entry? first = null;
        foreach (var file in dlg.FileNames)
        {
            var entry = FontBook.Import(file);
            first ??= entry;
        }
        if (first == null)
        {
            if (owner != null) UI.Hud.Show(L.T("Edit.FontFailed"), "info", owner);
            return;
        }
        PickFont(first.Id);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        EmojiPopup.IsOpen = ColorPopup.IsOpen = false;
        DuplicateRequested?.Invoke();
    }

    /// <summary>Which dot is the caption's colour: white, black, or - any other colour - the wheel, with that colour in its middle.</summary>
    private void MarkSwatch(Color c)
    {
        _color = c;
        bool own = true;
        foreach (Border b in Swatches.Children)
        {
            if (b.Tag is not Color x) continue;
            bool on = x == c;
            own &= !on;
            b.BorderThickness = new Thickness(on ? 2.5 : 1);
            b.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255));
        }
        _wheelRing.BorderThickness = new Thickness(own ? 2.5 : 1);
        _wheelRing.BorderBrush = own ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255));
        _wheelDot.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        if (own) _wheelDot.Background = new SolidColorBrush(c);
    }

    // ------------------------------------------------------------------ emoji

    /// <summary>The emoji gallery opens over the caption being written (the caret is kept for the emoji to go to).</summary>
    private async void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (PopupToggle.JustClosed(EmojiBtn)) return; // the click that closed the gallery
        _caret = TextEdit.CaretIndex;
        EmojiPopup.IsOpen = true;
        await Picker.LoadAsync();
    }

    /// <summary>
    /// An emoji goes into the text at the caret, like on a phone keyboard (the gallery stays open for more); the placeholder
    /// of a new caption makes way for it. A caption of just an emoji is a sticker.
    /// </summary>
    private void OnEmojiPicked(AppleEmoji.Entry entry)
    {
        string text = TextEdit.Text;
        int at = Math.Clamp(_caret < 0 ? text.Length : _caret, 0, text.Length);
        if (text == L.T("Edit.Placeholder")) { text = ""; at = 0; }
        TextEdit.Text = text.Insert(at, entry.Text);
        _caret = at + entry.Text.Length;
        TextEdit.CaretIndex = _caret;
        _ = AppleEmoji.ImageAsync(entry.Unified).ContinueWith(_ => Dispatcher.InvokeAsync(() => Redraw?.Invoke()));
    }
}
