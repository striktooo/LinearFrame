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

    public event Action<string>? TextEdited;
    public event Action<CaptionStyle>? StyleChosen;
    public event Action<Color>? ColorChosen;
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
    }

    public static double SizeFrom(double v) => MinSize + v * (MaxSize - MinSize);
    public static double SizeTo(double s) => Math.Clamp((s - MinSize) / (MaxSize - MinSize), 0, 1);

    /// <summary>Shows a caption's settings (without reporting them back as changes).</summary>
    public void Show(ICaption c)
    {
        _suppress = true;
        try
        {
            if (TextEdit.Text != c.Text) TextEdit.Text = c.Text;
            StyleStroke.IsChecked = c.Style == CaptionStyle.Stroke;
            StyleFill.IsChecked = c.Style == CaptionStyle.Fill;
            StyleNone.IsChecked = c.Style == CaptionStyle.None;
            TextSize.Value = SizeTo(c.Size);
            MarkSwatch(c.Color);
        }
        finally { _suppress = false; }
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

    public void CloseEmoji() => EmojiPopup.IsOpen = false;

    // ---- for the self-test
    internal void TestType(string text) => TextEdit.Text = text;
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
        else if (e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            Done_Click(this, new RoutedEventArgs());
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        EmojiPopup.IsOpen = false;
        Finished?.Invoke();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        EmojiPopup.IsOpen = false;
        DeleteRequested?.Invoke();
    }

    // ------------------------------------------------------------------ look

    private void Style_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not FrameworkElement { Tag: string tag }) return;
        StyleChosen?.Invoke(Enum.Parse<CaptionStyle>(tag));
    }

    private void BuildSwatches()
    {
        var p = Captions.Palette;
        foreach (var c in new[] { Colors.White, Colors.Black, p[2], p[3], p[4], p[5], p[7], p[8], p[10] })
        {
            var color = c;
            var dot = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                Tag = c,
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                MarkSwatch(color);
                ColorChosen?.Invoke(color);
            };
            Swatches.Children.Add(dot);
        }
    }

    private void MarkSwatch(Color c)
    {
        foreach (Border b in Swatches.Children)
        {
            bool on = b.Tag is Color x && x == c;
            b.BorderThickness = new Thickness(on ? 2.5 : 1);
            b.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255));
        }
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
