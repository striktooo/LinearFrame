using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinearFrame.Core;
using LinearFrame.Editor;
using LinearFrame.Imaging;
using LinearFrame.Player;

namespace LinearFrame.UI;

public partial class MainWindow : Window
{
    public const string ProjectUrl = "https://github.com/striktooo/LinearFrame";

    private readonly AppSettings _s;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _reloadTimer;
    private CancellationTokenSource? _thumbCts;
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        _s = App.Instance!.Settings;
        WindowHelper.Attach(this);

        var icon = AppIconArt.Render(96);
        AppIconImage.Source = icon;
        BannerIcon.Source = icon;
        AboutIcon.Source = icon;
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        string v = ver == null ? "1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        VersionText.Text = L.F("Settings.Version", v);
        AboutVersion.Text = $"LinearFrame {v}";

        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); LoadShots(); };

        UpdateTexts();
        LoadSettingsIntoUi();
        HookSettingsEvents();
        RefreshRecent();

        WelcomeBanner.Visibility = _s.FirstRunDone ? Visibility.Collapsed : Visibility.Visible;
        NavScreens.IsChecked = true;

        Loaded += (_, _) => { LoadShots(); StartWatcher(); };
        if (App.Instance != null) App.Instance.UpdateStateChanged += OnUpdateState;
        UpdateUpdatesUi();
        Closed += (_, _) =>
        {
            if (App.Instance != null) App.Instance.UpdateStateChanged -= OnUpdateState;
            _watcher?.Dispose();
            _thumbCts?.Cancel();
            ShotsList.Items.Clear();
        };
    }

    private void UpdateTexts()
    {
        ScreensSubtitle.Text = L.F("Screens.Subtitle", _s.RegionHotkey, _s.FullscreenHotkey);
        ShotsEmptyHint.Text = L.F("Screens.EmptyHint", _s.RegionHotkey);
        WelcomeBody.Text = L.F("Welcome.Body", _s.RegionHotkey);
    }

    public void Navigate(string page)
    {
        switch (page)
        {
            case "player": NavPlayer.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
            default: NavScreens.IsChecked = true; break;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ScreensPage == null) return;
        string tag = (sender as FrameworkElement)?.Tag as string ?? "screens";
        ScreensPage.Visibility = tag == "screens" ? Visibility.Visible : Visibility.Collapsed;
        PlayerPage.Visibility = tag == "player" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    // =============================================================== screenshots

    private void NewShot_Click(object sender, RoutedEventArgs e)
    {
        DismissWelcome();
        // Hide ourselves so we don't end up in the screenshot.
        WindowState = WindowState.Minimized;
        App.Instance?.StartRegionCapture(320);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.Instance?.OpenScreenshotFolder();

    private void DismissWelcome_Click(object sender, RoutedEventArgs e) => DismissWelcome();

    private void DismissWelcome()
    {
        if (_s.FirstRunDone) return;
        _s.FirstRunDone = true;
        _s.Save();
        WelcomeBanner.Visibility = Visibility.Collapsed;
    }

    public void ScheduleShotsReload()
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void StartWatcher()
    {
        try
        {
            var dir = _s.ScreenshotFolder;
            Directory.CreateDirectory(dir);
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, _) => Dispatcher.InvokeAsync(ScheduleShotsReload);
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Changed += h;
            _watcher.Renamed += (_, _) => Dispatcher.InvokeAsync(ScheduleShotsReload);
        }
        catch (Exception ex) { Log.Error("Watcher failed", ex); }
    }

    private void LoadShots()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var token = _thumbCts.Token;

        List<FileInfo> files;
        try
        {
            var dir = new DirectoryInfo(_s.ScreenshotFolder);
            files = dir.Exists
                ? dir.EnumerateFiles().Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(150).ToList()
                : new List<FileInfo>();
        }
        catch { files = new List<FileInfo>(); }

        ShotsList.Items.Clear();
        ShotsEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotsScroll.Visibility = files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        double scale = WindowHelper.DpiScale(this);
        int decodeW = (int)(200 * scale * 1.25);
        var cards = new List<(FileInfo file, Border thumb)>();
        foreach (var f in files)
        {
            var (card, thumb) = MakeCard(f);
            ShotsList.Items.Add(card);
            cards.Add((f, thumb));
        }

        Task.Run(() =>
        {
            foreach (var (file, thumb) in cards)
            {
                if (token.IsCancellationRequested) return;
                var bmp = ImageIO.LoadThumbnail(file.FullName, decodeW);
                if (bmp == null) continue;
                Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    thumb.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
                }, DispatcherPriority.Background);
            }
        }, token);
    }

    private (FrameworkElement card, Border thumb) MakeCard(FileInfo f)
    {
        var thumb = new Border
        {
            Width = 200,
            Height = 128,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };
        thumb.SetResourceReference(Border.BackgroundProperty, "Brush.Thumbnail");
        thumb.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");

        var name = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(f.Name),
            Margin = new Thickness(2, 7, 2, 0),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200,
        };
        var date = new TextBlock
        {
            Text = f.LastWriteTime.ToString("g"),
            Margin = new Thickness(2, 1, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        date.SetResourceReference(StyleProperty, "Text.Caption");

        var stack = new StackPanel { Width = 200 };
        stack.Children.Add(thumb);
        stack.Children.Add(name);
        stack.Children.Add(date);

        var card = new Border
        {
            Child = stack,
            Margin = new Thickness(0, 0, 16, 18),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = f.Name,
        };
        card.MouseEnter += (_, _) => thumb.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        card.MouseLeave += (_, _) => thumb.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        card.MouseLeftButtonUp += (_, _) => OpenShot(f.FullName);
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowShotMenu(f.FullName);
        };
        return (card, thumb);
    }

    /// <summary>Self-test / README images: fill the library with in-memory pictures (no files involved).</summary>
    internal void ShowShotsForTest(List<(string Name, DateTime Date, BitmapSource Image)> items)
    {
        WelcomeBanner.Visibility = Visibility.Collapsed;
        ShotsEmpty.Visibility = Visibility.Collapsed;
        ShotsScroll.Visibility = Visibility.Visible;
        ShotsList.Items.Clear();
        foreach (var (name, date, image) in items)
        {
            var thumb = new Border { Width = 200, Height = 128, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
            thumb.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
            thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            var title = new TextBlock { Text = name, Margin = new Thickness(2, 7, 2, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
            var sub = new TextBlock { Text = date.ToString("g"), Margin = new Thickness(2, 1, 2, 0) };
            sub.SetResourceReference(StyleProperty, "Text.Caption");
            var stack = new StackPanel { Width = 200 };
            stack.Children.Add(thumb);
            stack.Children.Add(title);
            stack.Children.Add(sub);
            ShotsList.Items.Add(new Border { Child = stack, Margin = new Thickness(0, 0, 16, 18) });
        }
    }

    private void OpenShot(string path)
    {
        if (!File.Exists(path)) { ScheduleShotsReload(); return; }
        App.Instance?.OpenImageInEditor(path);
    }

    private void ShowShotMenu(string path)
    {
        Native.GetCursorPos(out var p);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        new NativeMenu()
            .Add(L.T("Screens.Edit"), () => OpenShot(path), isDefault: true)
            .Add(L.T("Screens.Copy"), () =>
            {
                var bmp = ImageIO.LoadFull(path);
                if (bmp != null && ImageIO.CopyToClipboard(bmp)) Hud.Show(L.T("Capture.Copied"), "copy", this);
            })
            .Add(L.T("Screens.Reveal"), () => ShellUtil.Reveal(path))
            .Separator()
            .Add(L.T("Screens.Delete"), () =>
            {
                if (ShellUtil.MoveToRecycleBin(path)) ScheduleShotsReload();
            })
            .Show(hwnd, p.X, p.Y);
    }

    // =============================================================== player page

    private void OpenMedia_Click(object sender, RoutedEventArgs e) => App.Instance?.OpenMediaDialog();

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropZoneBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Accent");
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) =>
        DropZoneBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.TextTertiary");

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            App.Instance?.OpenMedia(files[0]);
    }

    public void RefreshRecent()
    {
        RecentList.Children.Clear();
        var items = _s.RecentMedia.Where(File.Exists).Take(8).ToList();
        RecentHeader.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var path in items)
        {
            bool audio = MediaFormats.IsAudio(path);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new IconView { Icon = audio ? "music" : "film", Width = 18, Height = 18 };
            icon.SetResourceReference(IconView.ForegroundProperty, "Brush.Accent");
            row.Children.Add(icon);
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
            var dir = new TextBlock { Text = Path.GetDirectoryName(path) ?? "" };
            dir.SetResourceReference(StyleProperty, "Text.Caption");
            text.Children.Add(dir);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var btn = new Button
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 7, 10, 7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
            };
            btn.SetResourceReference(StyleProperty, "Btn.Icon");
            btn.Width = double.NaN;
            btn.Height = double.NaN;
            btn.Click += (_, _) => App.Instance?.OpenMedia(path);
            RecentList.Children.Add(btn);
        }
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        FileAssociations.Register(MediaFormats.AssociationList);
        FileAssociations.OpenDefaultAppsSettings();
    }

    // =============================================================== settings

    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        RegionHotkeyBox.Gesture = _s.RegionHotkey.Clone();
        FullHotkeyBox.Gesture = _s.FullscreenHotkey.Clone();
        ClipboardToggle.IsChecked = _s.CopyToClipboard;
        AutoSaveToggle.IsChecked = _s.AutoSave;
        FolderText.Text = _s.ScreenshotFolder;
        FmtPng.IsChecked = _s.ImageFormat == ImageFormatKind.Png;
        FmtJpg.IsChecked = _s.ImageFormat == ImageFormatKind.Jpg;
        DetectToggle.IsChecked = _s.DetectWindows;
        ThumbToggle.IsChecked = _s.ShowThumbnail;
        SoundToggle.IsChecked = _s.PlaySound;
        StartupToggle.IsChecked = _s.RunAtStartup;
        AutoUpdateToggle.IsChecked = _s.AutoUpdate;
        LangAuto.IsChecked = _s.Language == "auto";
        LangPl.IsChecked = _s.Language == "pl";
        LangEn.IsChecked = _s.Language == "en";

        SizePanel.Children.Clear();
        foreach (ExportSize size in Enum.GetValues<ExportSize>())
        {
            var rb = new RadioButton
            {
                Content = ExportSizes.Label(size),
                GroupName = "size",
                IsChecked = _s.DefaultExportSize == size,
                Tag = size,
            };
            rb.SetResourceReference(StyleProperty, "Segment.Item");
            rb.Checked += (_, _) =>
            {
                if (_loadingSettings) return;
                _s.DefaultExportSize = (ExportSize)rb.Tag;
                App.Instance?.SettingsChanged();
            };
            SizePanel.Children.Add(rb);
        }
        _loadingSettings = false;
    }

    private void HookSettingsEvents()
    {
        RegionHotkeyBox.GestureChanged += g =>
        {
            _s.RegionHotkey = g;
            App.Instance?.SettingsChanged(hotkeysChanged: true);
            UpdateTexts();
        };
        FullHotkeyBox.GestureChanged += g =>
        {
            _s.FullscreenHotkey = g;
            App.Instance?.SettingsChanged(hotkeysChanged: true);
            UpdateTexts();
        };

        void Toggle(CheckBox cb, Action<bool> set, bool startup = false)
        {
            cb.Checked += (_, _) => { if (_loadingSettings) return; set(true); App.Instance?.SettingsChanged(startupChanged: startup); };
            cb.Unchecked += (_, _) => { if (_loadingSettings) return; set(false); App.Instance?.SettingsChanged(startupChanged: startup); };
        }
        Toggle(ClipboardToggle, v => _s.CopyToClipboard = v);
        Toggle(AutoSaveToggle, v => _s.AutoSave = v);
        Toggle(DetectToggle, v => _s.DetectWindows = v);
        Toggle(ThumbToggle, v => _s.ShowThumbnail = v);
        Toggle(SoundToggle, v => { _s.PlaySound = v; if (v) Sound.Shutter(); });
        Toggle(StartupToggle, v => _s.RunAtStartup = v, startup: true);
        Toggle(AutoUpdateToggle, v => _s.AutoUpdate = v);

        FmtPng.Checked += (_, _) => { if (_loadingSettings) return; _s.ImageFormat = ImageFormatKind.Png; App.Instance?.SettingsChanged(); };
        FmtJpg.Checked += (_, _) => { if (_loadingSettings) return; _s.ImageFormat = ImageFormatKind.Jpg; App.Instance?.SettingsChanged(); };

        void Lang(RadioButton rb, string value) => rb.Checked += (_, _) =>
        {
            if (_loadingSettings) return;
            _s.Language = value;
            App.Instance?.SettingsChanged();
            LangHint.Visibility = Visibility.Visible;
        };
        Lang(LangAuto, "auto");
        Lang(LangPl, "pl");
        Lang(LangEn, "en");
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = _s.ScreenshotFolder,
            Title = L.T("Settings.Folder"),
        };
        if (dlg.ShowDialog(this) == true)
        {
            _s.SaveFolder = dlg.FolderName;
            App.Instance?.SettingsChanged();
            FolderText.Text = _s.ScreenshotFolder;
            StartWatcher();
            LoadShots();
        }
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => ShellUtil.Open(ProjectUrl);

    // =============================================================== updates

    private void OnUpdateState() => Dispatcher.InvokeAsync(UpdateUpdatesUi);

    private void UpdateUpdatesUi()
    {
        var app = App.Instance;
        if (app == null) return;
        var available = app.AvailableUpdate;
        string status = app.UpdateStatus;
        if (string.IsNullOrEmpty(status))
        {
            status = L.F("Update.Current", Updater.Current);
            if (_s.LastUpdateCheckUtc is DateTime t) status += "  ·  " + t.ToLocalTime().ToString("g");
        }
        UpdateStatusText.Text = status;
        UpdateButton.IsEnabled = !app.UpdateBusy;
        if (available != null)
        {
            UpdateButton.Content = L.F("Update.Install", available.Version);
            UpdateButton.SetResourceReference(StyleProperty, "Btn.Primary");
        }
        else
        {
            UpdateButton.Content = L.T("Update.Check");
            UpdateButton.SetResourceReference(StyleProperty, "Btn.Secondary");
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var app = App.Instance;
        if (app == null) return;
        if (app.AvailableUpdate == null) await app.CheckForUpdatesAsync(manual: true);
        else await app.InstallUpdateNowAsync();
        UpdateUpdatesUi();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not HotkeyBox) { Close(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
}
