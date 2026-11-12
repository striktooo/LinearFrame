using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LinearFrame.Core;

namespace LinearFrame.UI;

/// <summary>
/// "Keep these display settings?" after Retina mode was switched on - like Windows' own prompt: if nobody answers
/// (e.g. the picture went black) everything goes back by itself after 15 seconds.
/// </summary>
public static class RetinaConfirm
{
    public static Task<bool> AskAsync(int seconds = 15)
    {
        var tcs = new TaskCompletionSource<bool>();
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Title = "LinearFrame",
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        var title = new TextBlock { Text = L.T("Retina.ConfirmTitle"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6), TextAlignment = TextAlignment.Center };
        var body = new TextBlock { Text = L.T("Retina.ConfirmBody"), TextWrapping = TextWrapping.Wrap, MaxWidth = 330, TextAlignment = TextAlignment.Center };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var icon = new IconView { Icon = "sparkle", Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(IconView.ForegroundProperty, "Brush.Accent");

        var revert = new Button { MinWidth = 120, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        revert.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Secondary");
        var keep = new Button { Content = L.T("Retina.Keep"), MinWidth = 120, IsDefault = true };
        keep.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Primary");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(revert);
        buttons.Children.Add(keep);

        var stack = new StackPanel();
        stack.Children.Add(icon);
        stack.Children.Add(title);
        stack.Children.Add(body);
        stack.Children.Add(buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(28, 22, 28, 22),
            Margin = new Thickness(24),
            BorderThickness = new Thickness(1),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.3, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.CardBg");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        TextElement_SetFont(card);
        w.Content = card;

        int left = seconds;
        void UpdateRevert() => revert.Content = L.F("Retina.Revert", left);
        UpdateRevert();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bool done = false;
        void Finish(bool result)
        {
            if (done) return;
            done = true;
            timer.Stop();
            w.Close();
            tcs.TrySetResult(result);
        }
        timer.Tick += (_, _) => { left--; if (left <= 0) Finish(false); else UpdateRevert(); };
        keep.Click += (_, _) => Finish(true);
        revert.Click += (_, _) => Finish(false);
        w.Closed += (_, _) => Finish(false);
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Finish(false); e.Handled = true; } };
        w.Loaded += (_, _) =>
        {
            // centre on the primary monitor
            var work = SystemParameters.WorkArea;
            w.Left = work.Left + (work.Width - w.ActualWidth) / 2;
            w.Top = work.Top + (work.Height - w.ActualHeight) / 2.4;
            w.Activate();
            keep.Focus();
        };
        w.SourceInitialized += (_, _) => Native.SetForegroundWindow(new WindowInteropHelper(w).Handle);
        w.Show();
        timer.Start();
        return tcs.Task;
    }

    private static void TextElement_SetFont(FrameworkElement e)
    {
        e.SetResourceReference(TextElement.FontFamilyProperty, "Font.UI");
        e.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        TextElement.SetFontSize(e, 13);
    }
}
