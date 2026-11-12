using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LinearFrame.Core;

/// <summary>Light/dark theme following the Windows app theme, switched live.</summary>
internal static class Theme
{
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Init(Application app, bool? forceDark = null)
    {
        IsDark = forceDark ?? ReadSystemDark();
        Apply(app);
    }

    public static void Refresh()
    {
        bool dark = ReadSystemDark();
        if (dark == IsDark || Application.Current == null) return;
        IsDark = dark;
        Apply(Application.Current);
        foreach (Window w in Application.Current.Windows) ApplyFrame(w);
        Changed?.Invoke();
    }

    private static void Apply(Application app)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri($"/LinearFrame;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        var merged = app.Resources.MergedDictionaries;
        if (_current != null)
        {
            int i = merged.IndexOf(_current);
            if (i >= 0) merged[i] = dict; else merged.Insert(0, dict);
        }
        else merged.Insert(0, dict);
        _current = dict;
    }

    /// <summary>Rounded corners + dark/light DWM border for a window (Windows 11; harmless on 10).</summary>
    public static void ApplyFrame(Window w, bool? forceDark = null)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        ApplyFrame(hwnd, forceDark ?? IsDark);
    }

    public static void ApplyFrame(IntPtr hwnd, bool dark)
    {
        int d = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
        int corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }
}
