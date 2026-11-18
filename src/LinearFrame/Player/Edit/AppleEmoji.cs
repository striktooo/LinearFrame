using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// Emoji as on an iPhone: Apple's emoji pictures from the public emoji-datasource-apple package. They are Apple's artwork,
/// so they are not part of LinearFrame - they are fetched once, the first time you use emoji, and kept in
/// %LOCALAPPDATA%\LinearFrame\emoji (the gallery sheet and list are checked against pinned SHA-256 hashes).
/// </summary>
public static class AppleEmoji
{
    public const string Version = "16.0.0";
    private const string Cdn = "https://cdn.jsdelivr.net/npm/emoji-datasource-apple@" + Version + "/";
    private const string CatalogFile = "emoji.json", CatalogSha256 = "1D602E65BE88772BF8CC368CE16B855D719EEDDBAFE128D471B80203F494D29F";
    private const string SheetFile = "img/apple/sheets-256/64.png", SheetSha256 = "A2ACF99BD9C60654458AA5C4068037DBED1FE4FA33641CFCAB8ABEFF8664F483";
    /// <summary>Gallery sheet: 62 × 62 cells of 66 px (64 px picture + 1 px around it).</summary>
    public const int SheetCells = 62, SheetCell = 66;

    public sealed record Entry(string Unified, string NonQualified, string Image, string Name, string Category, int Order, int SheetX, int SheetY)
    {
        /// <summary>The emoji as text.</summary>
        public string Text => string.Concat(Unified.Split('-').Select(h => char.ConvertFromUtf32(Convert.ToInt32(h, 16))));
    }

    /// <summary>iOS order and names of the gallery tabs (emoji-data category → tab).</summary>
    public static readonly (string Key, string[] Sources, string Icon)[] Tabs =
    {
        ("smileys", new[] { "Smileys & Emotion", "People & Body" }, "😀"),
        ("animals", new[] { "Animals & Nature" }, "🐻"),
        ("food", new[] { "Food & Drink" }, "🍔"),
        ("activity", new[] { "Activities" }, "⚽"),
        ("travel", new[] { "Travel & Places" }, "🚗"),
        ("objects", new[] { "Objects" }, "💡"),
        ("symbols", new[] { "Symbols" }, "❤️"),
        ("flags", new[] { "Flags" }, "🏁"),
    };

    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, BitmapSource> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Missing = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<Entry>? _catalog;
    private static Dictionary<string, Entry>? _byCode;
    private static Task<IReadOnlyList<Entry>?>? _catalogTask;

    /// <summary>Raised on the UI thread when downloaded pictures arrive (captions and stickers redraw).</summary>
    public static event Action? ImagesChanged;

    public static string Dir => Path.Combine(AppPaths.LocalDir, "emoji", "apple-" + Version);

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LinearFrame");
        return h;
    }

    // ------------------------------------------------------------------ catalog

    public static IReadOnlyList<Entry>? Catalog => _catalog;

    public static Task<IReadOnlyList<Entry>?> CatalogAsync() => _catalogTask ??= Task.Run(LoadCatalog);

    private static async Task<IReadOnlyList<Entry>?> LoadCatalog()
    {
        try
        {
            var path = await Fetch(CatalogFile, CatalogSha256);
            if (path == null) { _catalogTask = null; return null; }
            using var fs = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(fs);
            var list = new List<Entry>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("has_img_apple", out var has) || has.ValueKind != JsonValueKind.True) continue;
                string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                int I(string k) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out int x) ? x : 0;
                var cat = S("category");
                if (cat == "Component") continue;
                list.Add(new Entry(S("unified").ToLowerInvariant(), S("non_qualified").ToLowerInvariant(), S("image"), S("short_name"), cat, I("sort_order"), I("sheet_x"), I("sheet_y")));
            }
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            var by = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in list)
            {
                by.TryAdd(x.Unified, x);
                if (x.NonQualified.Length > 0) by.TryAdd(x.NonQualified, x);
                by.TryAdd(x.Unified.Replace("-fe0f", ""), x);
            }
            _byCode = by;
            _catalog = list;
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Emoji catalog: " + ex.Message);
            _catalogTask = null;
            return null;
        }
    }

    /// <summary>The gallery sheet, decoded so that one emoji is <paramref name="cellPx"/> pixels (memory stays small).</summary>
    public static async Task<BitmapSource?> SheetAsync(int cellPx)
    {
        try
        {
            var path = await Fetch(SheetFile, SheetSha256);
            if (path == null) return null;
            return await Task.Run(() =>
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = Math.Clamp(SheetCells * (cellPx + 2), 512, SheetCells * SheetCell);
                bi.EndInit();
                bi.Freeze();
                return (BitmapSource)bi;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Emoji sheet: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ single pictures (64 px)

    /// <summary>A sticker/caption picture by its code ("1f602"), from memory or disk; null while it is downloading.</summary>
    public static BitmapSource? Image(string unified)
    {
        unified = unified.ToLowerInvariant();
        if (Images.TryGetValue(unified, out var img)) return img;
        if (Missing.ContainsKey(unified)) return null;
        _ = ImageAsync(unified);
        return null;
    }

    public static Task<BitmapSource?> ImageAsync(string unified) =>
        Pending.GetOrAdd(unified.ToLowerInvariant(), u => Task.Run(() => LoadImage(u)));

    private static async Task<BitmapSource?> LoadImage(string unified)
    {
        try
        {
            if (Images.TryGetValue(unified, out var have)) return have;
            var entry = _byCode != null && _byCode.TryGetValue(unified, out var e) ? e : null;
            string file = entry?.Image ?? unified + ".png";
            var local = Path.Combine(Dir, "64", file);
            if (!File.Exists(local))
            {
                var bytes = await Http.GetByteArrayAsync(Cdn + "img/apple/64/" + file);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await File.WriteAllBytesAsync(local + ".tmp", bytes);
                File.Move(local + ".tmp", local, true);
            }
            var bmp = Decode(local);
            if (bmp == null) { Missing.TryAdd(unified, 0); return null; }
            Images[unified] = bmp;
            if (entry != null) Images[entry.Unified] = bmp;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ImagesChanged?.Invoke()));
            return bmp;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Missing.TryAdd(unified, 0);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Emoji {unified}: {ex.Message}");
            return null;
        }
        finally { Pending.TryRemove(unified, out _); }
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi.PixelWidth is > 8 and <= 512 ? bi : null;
        }
        catch { try { File.Delete(path); } catch { /* broken download */ } return null; }
    }

    /// <summary>Code of one emoji written as text (an emoji typed into a caption), e.g. "😂" → "1f602".</summary>
    public static string Code(string cluster) => string.Join('-', cluster.EnumerateRunes().Select(r => r.Value.ToString("x")));

    /// <summary>Picture for an emoji typed into a caption (null if it is still on its way or Apple has none).</summary>
    public static BitmapSource? ImageForText(string cluster)
    {
        foreach (var code in Candidates(cluster))
        {
            if (Images.TryGetValue(code, out var img)) return img;
        }
        if (_byCode == null) { _ = CatalogAsync(); return null; }
        foreach (var code in Candidates(cluster))
            if (_byCode.TryGetValue(code, out var e)) return Image(e.Unified);
        return null;
    }

    private static IEnumerable<string> Candidates(string cluster)
    {
        var cps = cluster.EnumerateRunes().Select(r => r.Value.ToString("x")).ToList();
        var noVs = cps.Where(c => c != "fe0f").ToList();
        yield return string.Join('-', cps);
        yield return string.Join('-', noVs);
        if (noVs.Count > 0) yield return string.Join('-', new[] { noVs[0], "fe0f" }.Concat(noVs.Skip(1)));
    }

    /// <summary>Splits a run of emoji into single emoji (keeping joiners, skin tones, flags and keycaps together).</summary>
    public static IEnumerable<string> Clusters(string run)
    {
        var cps = run.EnumerateRunes().Select(r => r.Value).ToArray();
        static bool Skin(int c) => c is >= 0x1F3FB and <= 0x1F3FF;
        static bool Regional(int c) => c is >= 0x1F1E6 and <= 0x1F1FF;
        static bool Tag(int c) => c is >= 0xE0020 and <= 0xE007F;
        int i = 0;
        while (i < cps.Length)
        {
            int j = i + 1;
            if (Regional(cps[i]) && j < cps.Length && Regional(cps[j])) j++;
            else
                while (j < cps.Length)
                {
                    int c = cps[j];
                    if (c is 0xFE0F or 0x20E3 || Skin(c) || Tag(c)) j++;
                    else if (c == 0x200D && j + 1 < cps.Length) j += 2;
                    else break;
                }
            var piece = string.Concat(cps[i..j].Select(char.ConvertFromUtf32));
            if (piece.Trim('‍', '️').Length > 0) yield return piece;
            i = j;
        }
    }

    // ------------------------------------------------------------------ downloading

    /// <summary>A package file in the cache folder (downloaded once and checked against its pinned hash).</summary>
    private static async Task<string?> Fetch(string relative, string sha256)
    {
        var local = Path.Combine(Dir, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(local)) return local;
        var bytes = await Http.GetByteArrayAsync(Cdn + relative);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Emoji: {relative} has hash {hash}, expected {sha256} - not used");
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllBytesAsync(local + ".tmp", bytes);
        File.Move(local + ".tmp", local, true);
        return local;
    }

    /// <summary>The gallery and the list are on disk already (no download needed to open it).</summary>
    public static bool IsDownloaded => File.Exists(Path.Combine(Dir, CatalogFile)) && File.Exists(Path.Combine(Dir, SheetFile.Replace('/', Path.DirectorySeparatorChar)));
}
