using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.Player.Edit;
using LinearFrame.UI;

namespace LinearFrame.Editor;

public partial class EditorWindow : Window
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00),
        Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x00, 0xC7, 0xBE), Color.FromRgb(0x00, 0x7A, 0xFF),
        Color.FromRgb(0xAF, 0x52, 0xDE), Color.FromRgb(0xFF, 0x2D, 0x55), Color.FromRgb(0x1C, 0x1C, 0x1E),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    private readonly MarkupDocument _doc;
    private readonly AppSettings _s;
    private double _widthFactor = 1.8;     // thin 1 / medium 1.8 / thick 3.2 (× BaseUnit)
    private CaptionAnnotation? _caption;   // the caption the caption tool shows
    private bool _captionTyped, _captionSized;
    private bool _busy;
    private bool _closingConfirmed;
    private bool _initialFitDone;
    private (double? ratio, string label)[] _ratios = Array.Empty<(double?, string)>();

    public EditorWindow(MarkupDocument doc)
    {
        InitializeComponent();
        _doc = doc;
        _s = App.Instance!.Settings;
        WindowHelper.Attach(this);
        if (_doc.ExportSize == ExportSize.Original) _doc.ExportSize = _s.DefaultExportSize;

        View.Load(doc);
        View.CurrentColor = Palette[0];
        View.CurrentWidth = _widthFactor * doc.BaseUnit;
        View.Changed += OnDocChanged;
        View.SelectionChanged += OnSelectionChanged;
        View.ZoomChanged += UpdateStatus;
        View.CaptionEditRequested += EditCaption;
        View.CaptionResized += c => { if (ReferenceEquals(c, _caption)) Caption.ShowSize(c.Size); };
        Caption.TextEdited += OnCaptionText;
        Caption.StyleChosen += style => ChangeCaption(c => c.Style = style);
        Caption.ColorChosen += color => ChangeCaption(c => c.Color = color);
        Caption.SizeChosen += OnCaptionSize;
        Caption.DeleteRequested += () => { View.DeleteSelected(); OnDocChanged(); };
        Caption.Finished += FinishCaption;
        Caption.Redraw += View.RenderAll;
        View.CropFrameChanged += UpdateStatus;
        View.ColorPicked += OnColorPicked;

        BuildPalette();
        BuildWidths();
        PopupToggle.Attach(ColorPopup, ColorBtn);
        PopupToggle.Attach(WidthPopup, WidthBtn);
        BuildRatios();
        ToolArrow.IsChecked = true;
        UpdateTitle();
        UpdateSizeText();
        UpdateUndo();

        SizeInitialWindow();
        Loaded += (_, _) =>
        {
            FitZoom();
            _initialFitDone = true;
            View.Focus();
        };
        Scroller.SizeChanged += (_, _) => { if (_initialFitDone && _autoFit) FitZoom(); };
        SizeChanged += (_, _) => TitleText.Visibility = ActualWidth < 1320 ? Visibility.Collapsed : Visibility.Visible;
        Scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
        Closing += OnClosing;
        Closed += (_, _) => Memory.TrimSoon();
    }

    private bool _autoFit = true;

    private void SizeInitialWindow()
    {
        var work = WindowHelper.WorkAreaAtCursor(out double scale);
        double imgW = _doc.Frame.Width / scale, imgH = _doc.Frame.Height / scale;
        double w = Math.Max(MinWidth, imgW + 160);
        double h = Math.Max(MinHeight, imgH + 52 + 30 + 140);
        WindowHelper.PlaceCentered(this, Math.Min(w, work.Width / scale * 0.92), Math.Min(h, work.Height / scale * 0.92));
    }

    // =============================================================== tools & style

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (View == null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        View.Tool = Enum.Parse<ToolKind>(tag);
        if (View.Tool != ToolKind.Select) View.Select(null);
        View.Cursor = View.Tool switch { ToolKind.Select => Cursors.Arrow, ToolKind.Text => Cursors.IBeam, _ => Cursors.Cross };
    }

    private void SetTool(ToolKind t)
    {
        RadioButton rb = t switch
        {
            ToolKind.Select => ToolSelect,
            ToolKind.Pen => ToolPen,
            ToolKind.Highlighter => ToolHighlighter,
            ToolKind.Arrow => ToolArrow,
            ToolKind.Text => ToolText,
            ToolKind.Counter => ToolCounter,
            ToolKind.Pixelate => ToolPixelate,
            _ => ToolArrow,
        };
        rb.IsChecked = true;
    }

    private void BuildPalette()
    {
        SwatchGrid.Children.Clear();
        foreach (var c in Palette)
        {
            var color = c;
            var dot = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), StrokeThickness = 1 };
            var b = new Button { Content = dot, Width = 32, Height = 32, Margin = new Thickness(2) };
            b.SetResourceReference(StyleProperty, "Btn.Icon");
            b.Click += (_, _) =>
            {
                ColorPopup.IsOpen = false;
                SetColor(color);
            };
            SwatchGrid.Children.Add(b);
        }
    }

    private void SetColor(Color c)
    {
        View.CurrentColor = c;
        ColorDot.Fill = new SolidColorBrush(c);
        if (View.Selected != null) View.ApplyStyleToSelection(color: true, width: false);
        if (View.Selected is CaptionAnnotation caption) Caption.Show(caption);
    }

    private void BuildWidths()
    {
        WidthList.Children.Clear();
        foreach (var (factor, thickness) in new[] { (1.0, 2.0), (1.8, 4.0), (3.2, 7.0) })
        {
            var f = factor;
            var line = new Line { X1 = 4, X2 = 64, Y1 = 10, Y2 = 10, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            line.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
            var b = new Button { Content = line, Width = 84, Height = 30, Margin = new Thickness(1) };
            b.SetResourceReference(StyleProperty, "Btn.Icon");
            b.Click += (_, _) =>
            {
                WidthPopup.IsOpen = false;
                _widthFactor = f;
                View.CurrentWidth = f * _doc.BaseUnit;
                if (View.Selected != null) View.ApplyStyleToSelection(color: false, width: true);
            };
            WidthList.Children.Add(b);
        }
    }

    private void Color_Click(object sender, RoutedEventArgs e) { if (!PopupToggle.JustClosed(ColorBtn)) ColorPopup.IsOpen = true; }
    private void Width_Click(object sender, RoutedEventArgs e) { if (!PopupToggle.JustClosed(WidthBtn)) WidthPopup.IsOpen = true; }

    private void OnSelectionChanged()
    {
        if (View.Selected is CaptionAnnotation c) ShowCaption(c);
        else
        {
            HideCaption();
            if (View.Selected is Annotation a)
            {
                View.CurrentColor = a.Color;
                ColorDot.Fill = new SolidColorBrush(a.Color);
            }
        }
    }

    // =============================================================== captions

    /// <summary>The Text tool made a new caption (it joins the picture) or a caption was clicked: into the caption tool.</summary>
    private void EditCaption(CaptionAnnotation c, bool isNew)
    {
        if (isNew)
        {
            _doc.Checkpoint();
            _doc.Items.Add(c);
            View.Refresh();
            OnDocChanged();
        }
        View.Select(c);
        Caption.FocusText();
    }

    private void ShowCaption(CaptionAnnotation c)
    {
        if (!ReferenceEquals(c, _caption))
        {
            _caption = c;
            _captionTyped = _captionSized = false;
        }
        Caption.Show(c);
        CaptionBar.Visibility = Visibility.Visible;
    }

    /// <summary>The caption tool goes away; a caption left without text goes with it.</summary>
    private void HideCaption()
    {
        Caption.CloseEmoji();
        CaptionBar.Visibility = Visibility.Collapsed;
        var c = _caption;
        _caption = null;
        if (c != null && string.IsNullOrWhiteSpace(c.Text) && _doc.Items.Remove(c))
        {
            View.Refresh();
            OnDocChanged();
        }
    }

    private void FinishCaption()
    {
        View.Select(null);
        View.Focus();
    }

    private void OnCaptionText(string text)
    {
        if (_caption == null) return;
        if (!_captionTyped) { _doc.Checkpoint(); _captionTyped = true; }
        _caption.Text = text;
        // typing breaks lines at the picture's width; making it bigger later only scales it
        _caption.WrapEm = Captions.WrapEmFor(_caption.Size, _caption.CanvasWidth, _caption.CanvasHeight);
        View.RenderAll();
        OnDocChanged();
    }

    private void ChangeCaption(Action<CaptionAnnotation> change)
    {
        if (_caption == null) return;
        _doc.Checkpoint();
        change(_caption);
        View.RenderAll();
        OnDocChanged();
    }

    private void OnCaptionSize(double size, bool final)
    {
        if (_caption == null) return;
        if (!_captionSized) { _doc.Checkpoint(); _captionSized = true; }
        _caption.Size = size;
        if (final) _captionSized = false;
        View.RenderAll();
        if (final) OnDocChanged();
    }

    // =============================================================== crop & extend

    private void BuildRatios()
    {
        _ratios = new (double?, string)[]
        {
            (null, L.T("Editor.Free")), (16.0 / 9, "16:9"), (4.0 / 3, "4:3"), (1.0, "1:1"), (9.0 / 16, "9:16"),
        };
        RatioPanel.Children.Clear();
        for (int i = 0; i < _ratios.Length; i++)
        {
            var (ratio, label) = _ratios[i];
            var rb = new RadioButton { Content = label, GroupName = "ratio", Tag = i };
            rb.SetResourceReference(StyleProperty, "Segment.Item");
            rb.Checked += (_, _) =>
            {
                if (!View.CropMode) return;
                if (ratio == null && MarginSlider.Value == 0) View.CropRatio = null;
                else View.ApplyRatioAndMargin(ratio, MarginSlider.Value);
            };
            RatioPanel.Children.Add(rb);
        }
        AutoSwatch.Background = new SolidColorBrush(_doc.DetectedBackground);
    }

    private double? SelectedRatio()
    {
        foreach (RadioButton rb in RatioPanel.Children)
            if (rb.IsChecked == true) return _ratios[(int)rb.Tag].ratio;
        return null;
    }

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (View.CropMode) CropApply_Click(sender, e);
        else EnterCrop();
    }

    private void EnterCrop()
    {
        View.EnterCrop();
        CropToggle.IsChecked = true;
        CropBar.Visibility = Visibility.Visible;
        CanvasShadow.Visibility = Visibility.Collapsed;
        ((RadioButton)RatioPanel.Children[0]).IsChecked = true;
        MarginSlider.Value = 0;
        FillAuto.IsChecked = _doc.Fill == FillMode.Auto;
        FillTransparent.IsChecked = _doc.Fill == FillMode.Transparent;
        if (_doc.Fill == FillMode.Color) { FillCustom.Visibility = Visibility.Visible; FillCustom.IsChecked = true; CustomSwatch.Background = new SolidColorBrush(_doc.FillColor); }
        Dispatcher.BeginInvoke(new Action(FitZoom), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ExitCropUi()
    {
        CropToggle.IsChecked = false;
        CropBar.Visibility = Visibility.Collapsed;
        CanvasShadow.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(FitZoom), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CropApply_Click(object sender, RoutedEventArgs e)
    {
        View.ApplyCrop();
        ExitCropUi();
        OnDocChanged();
    }

    private void CropCancel_Click(object sender, RoutedEventArgs e)
    {
        View.CancelCrop();
        ExitCropUi();
    }

    private void CropReset_Click(object sender, RoutedEventArgs e)
    {
        ((RadioButton)RatioPanel.Children[0]).IsChecked = true;
        MarginSlider.Value = 0;
        View.ResetCrop();
    }

    private void Margin_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (View == null || !View.CropMode) return;
        View.ApplyRatioAndMargin(SelectedRatio(), MarginSlider.Value);
    }

    private void Fill_Checked(object sender, RoutedEventArgs e)
    {
        if (View == null || _doc == null || sender is not FrameworkElement fe) return;
        var mode = (fe.Tag as string) switch { "transparent" => FillMode.Transparent, "color" => FillMode.Color, _ => FillMode.Auto };
        if (mode == _doc.Fill) return;
        _doc.Checkpoint();
        _doc.Fill = mode;
        View.RenderAll();
        OnDocChanged();
    }

    private void Pick_Click(object sender, RoutedEventArgs e) => View.StartEyedropper();

    private void OnColorPicked(Color c)
    {
        if (View.CropMode)
        {
            _doc.Checkpoint();
            _doc.FillColor = c;
            _doc.Fill = FillMode.Color;
            CustomSwatch.Background = new SolidColorBrush(c);
            FillCustom.Visibility = Visibility.Visible;
            FillCustom.IsChecked = true;
            View.RenderAll();
            OnDocChanged();
        }
        else SetColor(c);
    }

    // =============================================================== undo / status

    private void OnDocChanged()
    {
        UpdateUndo();
        UpdateStatus();
    }

    private void UpdateUndo()
    {
        UndoBtn.IsEnabled = _doc.CanUndo;
        RedoBtn.IsEnabled = _doc.CanRedo;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => DoUndo();
    private void Redo_Click(object sender, RoutedEventArgs e) => DoRedo();

    private void DoUndo()
    {
        View.Select(null);
        if (View.CropMode) { View.CancelCrop(); ExitCropUi(); }
        if (_doc.Undo()) { View.Refresh(); OnDocChanged(); Dispatcher.BeginInvoke(new Action(FitZoom)); }
    }

    private void DoRedo()
    {
        View.Select(null);
        if (View.CropMode) { View.CancelCrop(); ExitCropUi(); }
        if (_doc.Redo()) { View.Refresh(); OnDocChanged(); Dispatcher.BeginInvoke(new Action(FitZoom)); }
    }

    private void UpdateTitle()
    {
        var name = _doc.FilePath != null ? System.IO.Path.GetFileNameWithoutExtension(_doc.FilePath) : L.T("Editor.Title");
        TitleText.Text = name;
        Title = $"{name} — LinearFrame";
    }

    private void UpdateSizeText() => SizeText.Text = ExportSizes.Label(_doc.ExportSize);

    private void UpdateStatus()
    {
        if (_doc == null || StatusText == null) return;
        Int32Rect f = _doc.Frame;
        if (View.CropMode)
        {
            var c = View.CropFrame;
            f = new Int32Rect((int)c.X, (int)c.Y, (int)c.Width, (int)c.Height);
        }
        string text = $"{f.Width} × {f.Height} px";
        if (_doc.ExportSize != ExportSize.Original)
        {
            var (w, h) = ExportSizes.TargetSize(_doc.ExportSize, f.Width, f.Height);
            text += $"   →   {w} × {h} px ({ExportSizes.Label(_doc.ExportSize)})";
        }
        if (View.CropMode) text += "     " + L.T("Editor.CropHint");
        StatusText.Text = text;
        ZoomText.Text = $"{Math.Round(View.Zoom * 100)}%";
    }

    // =============================================================== zoom

    private void FitZoom()
    {
        _autoFit = true;
        var vp = new Size(Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth,
                          Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight);
        if (vp.Width <= 0) return;
        View.SetZoom(View.FitZoom(vp));
        UpdateStatus();
    }

    /// <summary>
    /// Zoom that keeps the spot under the cursor (<paramref name="at"/>, in the scroll area) - or the middle of the view -
    /// where it is, instead of everything sliding away to the top-left corner.
    /// </summary>
    private void SetZoom(double z, Point? at = null)
    {
        _autoFit = false;
        var anchor = at ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        // which spot of the picture is there now, as a share of its size...
        double fx = 0.5, fy = 0.5;
        if (View.ActualWidth > 0 && View.ActualHeight > 0)
        {
            var p = Scroller.TranslatePoint(anchor, View);
            fx = p.X / View.ActualWidth;
            fy = p.Y / View.ActualHeight;
        }
        View.SetZoom(z);
        Scroller.UpdateLayout();
        // ...and the same spot scrolled back under it (a picture smaller than the view simply stays centred)
        var q = View.TranslatePoint(new Point(fx * View.ActualWidth, fy * View.ActualHeight), Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + q.X - anchor.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + q.Y - anchor.Y);
        UpdateStatus();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(View.Zoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(View.Zoom / 1.25);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => FitZoom();

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        SetZoom(View.Zoom * Math.Pow(1.0018, e.Delta), e.GetPosition(Scroller)); // one notch ≈ ×1.24, like the picture viewer
    }

    // =============================================================== output

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        var m = new NativeMenu();
        foreach (ExportSize s in Enum.GetValues<ExportSize>())
        {
            var size = s;
            m.Add(ExportSizes.Label(size), () => { _doc.ExportSize = size; UpdateSizeText(); UpdateStatus(); }, isChecked: size == _doc.ExportSize);
        }
        m.ShowFor(SizeBtn, forceDark: Theme.IsDark);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var m = new NativeMenu();
        m.Add(L.T("Editor.SaveAs"), SaveAs);
        if (_doc.FilePath != null)
        {
            m.Add(L.T("Editor.Reveal"), () => ShellUtil.Reveal(_doc.FilePath!));
            m.Separator();
            m.Add(L.T("Editor.Delete"), DeleteShot);
        }
        m.ShowFor((FrameworkElement)sender, forceDark: Theme.IsDark);
    }

    private async Task<BitmapSource?> RenderOutput()
    {
        if (View.CropMode) { View.ApplyCrop(); ExitCropUi(); }
        if (_busy) return null;
        _busy = true;
        bool big = _doc.ExportSize is ExportSize.UHD4K or ExportSize.UHD8K;
        if (big) BusyOverlay.Visibility = Visibility.Visible;
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            return await Exporter.RenderAsync(_doc, _doc.ExportSize);
        }
        catch (Exception ex)
        {
            Log.Error("Export failed", ex);
            AlertWindow.Tell(this, L.T("Editor.RenderFailed"), ex.Message);
            return null;
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var bmp = await RenderOutput();
        if (bmp == null) return;
        var png = await Task.Run(() => ImageIO.EncodePng(bmp));
        if (ImageIO.CopyToClipboard(bmp, png)) Hud.Show(L.T("Capture.Copied"), "copy", this);
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        var bmp = await RenderOutput();
        if (bmp == null) return;
        try
        {
            byte[]? png = null;
            if (_s.CopyToClipboard)
            {
                png = await Task.Run(() => ImageIO.EncodePng(bmp));
                ImageIO.CopyToClipboard(bmp, png);
            }
            bool changed = !_doc.IsPristine || _doc.ExportSize != ExportSize.Original;
            if (_doc.FilePath != null && changed)
            {
                var path = _doc.FilePath;
                bool jpg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
                var bytes = jpg ? await Task.Run(() => ImageIO.EncodeJpg(bmp)) : png ?? await Task.Run(() => ImageIO.EncodePng(bmp));
                await Task.Run(() => { File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true); });
            }
            else if (_doc.FilePath == null && _s.AutoSave)
            {
                var bytes = _s.ImageFormat == ImageFormatKind.Png ? png ?? await Task.Run(() => ImageIO.EncodePng(bmp)) : await Task.Run(() => ImageIO.EncodeJpg(bmp));
                _doc.FilePath = await Task.Run(() => ImageIO.SaveScreenshotBytes(bytes, _s));
            }
            App.Instance?.NotifyScreenshotsChanged();
            Hud.Show(_s.CopyToClipboard ? L.T("Capture.Copied") : L.T("Capture.Saved"), _s.CopyToClipboard ? "copy" : "check");
            _closingConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Done failed", ex);
            AlertWindow.Tell(this, L.T("Editor.SaveFailed"), ex.Message);
        }
    }

    private async void SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _doc.FilePath != null ? System.IO.Path.GetFileNameWithoutExtension(_doc.FilePath) : L.F("Capture.FileName", DateTime.Now),
            Filter = "PNG|*.png|JPG|*.jpg",
            DefaultExt = ".png",
            InitialDirectory = _s.ScreenshotFolder,
        };
        if (dlg.ShowDialog(this) != true) return;
        var bmp = await RenderOutput();
        if (bmp == null) return;
        bool jpg = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
        var bytes = await Task.Run(() => jpg ? ImageIO.EncodeJpg(bmp) : ImageIO.EncodePng(bmp));
        await File.WriteAllBytesAsync(dlg.FileName, bytes);
        Hud.Show(L.T("Capture.Saved"), "check", this);
        App.Instance?.NotifyScreenshotsChanged();
    }

    private void DeleteShot()
    {
        if (_doc.FilePath == null) return;
        if (ShellUtil.MoveToRecycleBin(_doc.FilePath))
        {
            App.Instance?.NotifyScreenshotsChanged();
            _closingConfirmed = true;
            Close();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || !_doc.Dirty) return;
        if (!AlertWindow.Ask(this, L.T("Editor.Unsaved"), L.T("Editor.UnsavedText"), L.T("Common.Discard"))) e.Cancel = true;
    }

    // =============================================================== keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Caption.IsTyping) { base.OnPreviewKeyDown(e); return; }
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        bool handled = true;
        switch (e.Key)
        {
            case Key.Z when ctrl && shift: DoRedo(); break;
            case Key.Z when ctrl: DoUndo(); break;
            case Key.Y when ctrl: DoRedo(); break;
            case Key.C when ctrl: Copy_Click(this, new RoutedEventArgs()); break;
            case Key.S when ctrl && shift: SaveAs(); break;
            case Key.S when ctrl: Done_Click(this, new RoutedEventArgs()); break;
            case Key.D0 when ctrl: FitZoom(); break;
            case Key.D1 when ctrl: SetZoom(1); break;
            case Key.OemPlus when ctrl: case Key.Add when ctrl: SetZoom(View.Zoom * 1.25); break;
            case Key.OemMinus when ctrl: case Key.Subtract when ctrl: SetZoom(View.Zoom / 1.25); break;
            case Key.Delete: case Key.Back: View.DeleteSelected(); OnDocChanged(); break;
            case Key.Escape:
                if (View.CropMode) CropCancel_Click(this, new RoutedEventArgs());
                else if (View.Selected != null) View.Select(null);
                else handled = false;
                break;
            case Key.Enter when View.CropMode: CropApply_Click(this, new RoutedEventArgs()); break;
            case Key.V when !ctrl: SetTool(ToolKind.Select); break;
            case Key.P when !ctrl: SetTool(ToolKind.Pen); break;
            case Key.H when !ctrl: SetTool(ToolKind.Highlighter); break;
            case Key.A when !ctrl: SetTool(ToolKind.Arrow); break;
            case Key.T when !ctrl: SetTool(ToolKind.Text); break;
            case Key.N when !ctrl: SetTool(ToolKind.Counter); break;
            case Key.B when !ctrl: SetTool(ToolKind.Pixelate); break;
            case Key.C when !ctrl: if (View.CropMode) CropApply_Click(this, new RoutedEventArgs()); else EnterCrop(); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
        else base.OnPreviewKeyDown(e);
    }
}
