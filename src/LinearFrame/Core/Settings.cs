using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace LinearFrame.Core;

public enum ExportSize { Original, FullHD, QHD, UHD4K, UHD8K }

public enum ImageFormatKind { Png, Jpg }

public sealed class HotkeyGesture
{
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }

    public HotkeyGesture() { }
    public HotkeyGesture(ModifierKeys modifiers, Key key) { Modifiers = modifiers; Key = key; }

    [JsonIgnore] public bool IsEmpty => Key == Key.None;

    public uint Win32Modifiers
    {
        get
        {
            uint m = 0;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= 0x1;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= 0x2;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= 0x4;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) m |= 0x8;
            return m;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (IsEmpty) return L.T("Hotkey.None");
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
        Key.Snapshot => "PrintScreen",
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemBackslash or Key.OemPipe => "\\",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        _ => key.ToString()
    };

    public HotkeyGesture Clone() => new(Modifiers, Key);
}

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";              // auto | pl | en
    public HotkeyGesture RegionHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.D4);
    public HotkeyGesture FullscreenHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.D3);

    public bool CopyToClipboard { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public string? SaveFolder { get; set; }
    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;
    public ExportSize DefaultExportSize { get; set; } = ExportSize.Original;

    public bool DetectWindows { get; set; } = true;
    public bool ShowThumbnail { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public bool RunAtStartup { get; set; } = true;
    public bool FirstRunDone { get; set; }

    public bool AutoUpdate { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? SkippedUpdate { get; set; }

    public double PlayerVolume { get; set; } = 100;
    public bool PlayerMuted { get; set; }
    public List<string> RecentMedia { get; set; } = new();

    [JsonIgnore] public string ScreenshotFolder => string.IsNullOrWhiteSpace(SaveFolder) ? AppPaths.DefaultScreenshotDir : SaveFolder!;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                if (s != null)
                {
                    s.RegionHotkey ??= new(ModifierKeys.Control | ModifierKeys.Shift, Key.D4);
                    s.FullscreenHotkey ??= new(ModifierKeys.Control | ModifierKeys.Shift, Key.D3);
                    s.RecentMedia ??= new();
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Error("Settings load failed", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Error("Settings save failed", ex); }
    }

    public void AddRecentMedia(string path)
    {
        RecentMedia.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentMedia.Insert(0, path);
        if (RecentMedia.Count > 12) RecentMedia.RemoveRange(12, RecentMedia.Count - 12);
    }
}
