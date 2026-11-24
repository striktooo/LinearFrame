using System.Windows.Media;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// Fonts the user brought along for captions: TrueType / OpenType files copied into the app's own folder (so a caption
/// keeps its font when the original file is moved or deleted) and read straight from there - nothing is installed into
/// Windows. A caption names its font by the file's name; without one it is TikTok Sans.
/// </summary>
public static class FontBook
{
    public sealed record Entry(string Id, string Name, Typeface Face, GlyphTypeface? Glyphs);

    private static readonly object Gate = new();
    private static List<Entry>? _list;

    public static readonly string[] Extensions = { ".ttf", ".otf", ".ttc" };

    /// <summary>For tests: another folder than the user's own.</summary>
    internal static string? FolderOverride;

    public static string Folder => FolderOverride ?? Path.Combine(AppPaths.DataDir, "fonts");

    /// <summary>The imported fonts, by name.</summary>
    public static IReadOnlyList<Entry> Imported
    {
        get
        {
            lock (Gate) return _list ??= Load();
        }
    }

    public static Entry? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var e in Imported)
            if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>What a caption's font is called (for the menu and the button's tip).</summary>
    public static string NameOf(string? id) => Find(id)?.Name ?? "TikTok Sans";

    private static List<Entry> Load()
    {
        var list = new List<Entry>();
        try
        {
            if (Directory.Exists(Folder))
                foreach (var file in Directory.EnumerateFiles(Folder))
                    if (Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && Read(file) is { } e) list.Add(e);
        }
        catch (Exception ex) { Log.Warn("Fonts: " + ex.Message); }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>A font file as WPF reads it; null when it is not a font WPF can draw with.</summary>
    private static Entry? Read(string file)
    {
        try
        {
            string full = Path.GetFullPath(file);
            foreach (var face in Fonts.GetTypefaces(new Uri(full)))
            {
                if (!face.TryGetGlyphTypeface(out var glyphs)) continue;
                // only this very file counts: for a file it can't read WPF quietly hands out a system font instead
                if (glyphs.FontUri is not { IsFile: true } uri || !string.Equals(Path.GetFullPath(uri.LocalPath), full, StringComparison.OrdinalIgnoreCase)) continue;
                string family = Pick(glyphs.Win32FamilyNames) ?? Pick(glyphs.FamilyNames) ?? Path.GetFileNameWithoutExtension(file);
                string style = Pick(glyphs.Win32FaceNames) ?? Pick(glyphs.FaceNames) ?? "";
                string name = style.Length == 0 || style.Equals("Regular", StringComparison.OrdinalIgnoreCase)
                              || family.EndsWith(style, StringComparison.OrdinalIgnoreCase) ? family : family + " " + style;
                return new Entry(Path.GetFileName(file), name, face, glyphs);
            }
        }
        catch (Exception ex) { Log.Warn($"Font {Path.GetFileName(file)}: {ex.Message}"); }
        return null;
    }

    private static string? Pick(IDictionary<System.Globalization.CultureInfo, string> names)
    {
        if (names == null || names.Count == 0) return null;
        foreach (var kv in names)
            if (kv.Key.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)) return kv.Value.Trim();
        foreach (var kv in names)
            if (!string.IsNullOrWhiteSpace(kv.Value)) return kv.Value.Trim();
        return null;
    }

    /// <summary>
    /// Takes a font file in: a copy goes into the app's folder (the same file a second time is simply found again).
    /// Null when the file is not a usable font.
    /// </summary>
    public static Entry? Import(string file)
    {
        try
        {
            if (!File.Exists(file) || !Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) return null;
            Directory.CreateDirectory(Folder);
            string name = Path.GetFileName(file);
            string target = Path.Combine(Folder, name);
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return Find(name);
            if (File.Exists(target) && !SameFile(file, target))
            {
                // another font under the same file name: this one gets a name of its own
                string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
                for (int i = 2; File.Exists(target) && !SameFile(file, target); i++) target = Path.Combine(Folder, $"{stem} ({i}){ext}");
            }
            bool copied = false;
            if (!File.Exists(target)) { File.Copy(file, target); copied = true; }
            var entry = Read(target);
            if (entry == null)
            {
                if (copied) { try { File.Delete(target); } catch { /* it stays; it is never listed */ } }
                return null;
            }
            lock (Gate) _list = null;
            return Find(entry.Id);
        }
        catch (Exception ex)
        {
            Log.Warn($"Font import {Path.GetFileName(file)}: {ex.Message}");
            return null;
        }
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = System.Security.Cryptography.SHA256.Create();
            using var x = File.OpenRead(a);
            using var y = File.OpenRead(b);
            return sa.ComputeHash(x).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(y));
        }
        catch { return false; }
    }

    /// <summary>For tests: forget what was read (another data folder).</summary>
    internal static void Reset()
    {
        lock (Gate) _list = null;
    }
}
