using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace LinearFrame.Core;

public enum ExportSize { Original, FullHD, QHD, UHD4K, UHD8K }

public enum ImageFormatKind { Png, Jpg }

/// <summary>Mouse buttons that can trigger a shortcut (side buttons alone, the wheel button only with a modifier).</summary>
public enum HotMouse { None, Middle, XButton1, XButton2 }

public sealed class HotkeyGesture
{
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }
    public HotMouse Mouse { get; set; }

    public HotkeyGesture() { }
    public HotkeyGesture(ModifierKeys modifiers, Key key) { Modifiers = modifiers; Key = key; }
    public HotkeyGesture(ModifierKeys modifiers, HotMouse mouse) { Modifiers = modifiers; Mouse = mouse; }

    [JsonIgnore] public bool IsEmpty => Key == Key.None && Mouse == HotMouse.None;
    [JsonIgnore] public bool IsMouse => Mouse != HotMouse.None;
    /// <summary>A key on its own (e.g. End) - it stops doing its usual job in other apps.</summary>
    [JsonIgnore] public bool IsLoneKey => !IsMouse && Key != Key.None && Modifiers == ModifierKeys.None;

    public bool SameAs(HotkeyGesture? other) =>
        other != null && !IsEmpty && other.Modifiers == Modifiers && other.Mouse == Mouse && (IsMouse || other.Key == Key);

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
        parts.Add(IsMouse ? MouseName(Mouse) : KeyName(Key));
        return string.Join(" + ", parts);
    }

    public static string MouseName(HotMouse m) => m switch
    {
        HotMouse.XButton1 => L.T("Hotkey.Mouse4"),
        HotMouse.XButton2 => L.T("Hotkey.Mouse5"),
        HotMouse.Middle => L.T("Hotkey.MouseMiddle"),
        _ => "",
    };

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
        Key.Scroll => "ScrollLock",
        Key.Capital => "CapsLock",
        Key.Apps => "Menu",
        _ => key.ToString()
    };

    public HotkeyGesture Clone() => new() { Modifiers = Modifiers, Key = Key, Mouse = Mouse };
}

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";              // auto | pl | en
    public HotkeyGesture RegionHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.D4);
    /// <summary>Optional second shortcut for the same thing - e.g. End on the keyboard and the side button on the mouse.</summary>
    public HotkeyGesture RegionHotkey2 { get; set; } = new();
    /// <summary>Empties the clipboard - e.g. the screenshots collected with Ctrl+C+C. Empty = no shortcut.</summary>
    public HotkeyGesture ClearClipboardHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.Back);

    public bool CopyToClipboard { get; set; } = true;
    /// <summary>Ctrl+C+C in the capture overlay adds the screenshot to the ones already in the clipboard (up to 10).</summary>
    public bool MultiCopy { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public string? SaveFolder { get; set; }
    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;
    public ExportSize DefaultExportSize { get; set; } = ExportSize.Original;

    public bool DetectWindows { get; set; } = true;
    public bool ShowThumbnail { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public bool RunAtStartup { get; set; } = true;
    public bool FirstRunDone { get; set; }
    /// <summary>The main window has been seen once - after that a tray start never opens it by itself.</summary>
    public bool WelcomeAutoShown { get; set; }
    public bool StartMenuShortcutDone { get; set; }

    /// <summary>HD captures are set up (the virtual screen is installed) and may be used.</summary>
    public bool HdEnabled { get; set; }
    /// <summary>How many times more pixels per side an HD capture is rendered with: 2, 3 or 4.</summary>
    public int HdScale { get; set; } = 4;
    /// <summary>The "HD" switch in the capture toolbar (remembered).</summary>
    public bool HdCapture { get; set; } = true;

    public bool AutoUpdate { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? SkippedUpdate { get; set; }

    public double PlayerVolume { get; set; } = 100;
    public bool PlayerMuted { get; set; }
    public List<string> RecentMedia { get; set; } = new();
    /// <summary>Emoji picked lately in the video editor (newest first).</summary>
    public List<string> RecentEmoji { get; set; } = new();
    /// <summary>Colours picked lately for captions from the full palette (#RRGGBB, newest first).</summary>
    public List<string> RecentColors { get; set; } = new();
    /// <summary>The banner file the editor's "Banner" button adds.</summary>
    public string? BannerPath { get; set; }

    [JsonIgnore] public string ScreenshotFolder => string.IsNullOrWhiteSpace(SaveFolder) ? AppPaths.DefaultScreenshotDir : SaveFolder!;

    /// <summary>
    /// How the settings file is written and read. The code for it is generated when LinearFrame is built
    /// (<see cref="SettingsJson"/>): working it out at every start by looking through the classes took over a tenth of
    /// a second - before the screenshot shortcut could be set up.
    /// </summary>
    internal static JsonSerializerOptions Json => SettingsJson.Default.Options;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), SettingsJson.Default.AppSettings);
                if (s != null)
                {
                    s.RegionHotkey ??= new(ModifierKeys.Control | ModifierKeys.Shift, Key.D4);
                    s.RegionHotkey2 ??= new();
                    s.ClearClipboardHotkey ??= new();
                    s.RecentMedia ??= new();
                    s.RecentEmoji ??= new();
                    s.RecentColors ??= new();
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
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

/// <summary>The settings file's reading and writing code, generated at build time (same format as before: indented, enums by name).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext
{
}
