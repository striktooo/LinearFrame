using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinearFrame.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, string? ShaUrl, long Size, string PageUrl)
{
    /// <summary>The small package without the engine folder (and its checksum list), if the release has one.</summary>
    public string? SmallZipUrl { get; init; }
    public string? SmallShaUrl { get; init; }
    public long SmallSize { get; init; }
}

/// <summary>
/// Self-update from GitHub Releases - no server of our own needed.
/// 1) ask the GitHub API for the latest release, 2) download the package and verify its SHA-256 - the small
/// "-update.zip" (no engine folder, ~40 % of the size) when the installed mpv/FFmpeg are still the right ones,
/// otherwise the full win-x64 ZIP, 3) swap the files next to the running exe (Windows lets you rename a running
/// exe/DLL), 4) restart.
/// </summary>
internal static class Updater
{
    public const string Owner = "striktooo";
    public const string Repo = "LinearFrame";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>LINEARFRAME_UPDATE_FEED may point to a local JSON file (testing).</summary>
    private static string Feed => Environment.GetEnvironmentVariable("LINEARFRAME_UPDATE_FEED")
                                  ?? $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0));

    public static string UpdatesDir => Path.Combine(AppPaths.LocalDir, "updates");

    private static HttpClient? _http;
    private static HttpClient Http => _http ??= CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"LinearFrame/{Current}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static bool IsLocal(string s) => !s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                             !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool Trusted(string url) =>
        IsLocal(Feed) || url.StartsWith($"https://github.com/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        string json = IsLocal(Feed) ? await File.ReadAllTextAsync(Feed, ct) : await Http.GetStringAsync(Feed, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
        ver = Normalize(ver);
        if (ver <= Current) return null;

        string? zip = null, sha = null, smallZip = null, smallSha = null;
        long size = 0, smallSize = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            long bytes = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) { zip = url; size = bytes; }
            else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
            else if (name.EndsWith("-update.zip", StringComparison.OrdinalIgnoreCase)) { smallZip = url; smallSize = bytes; }
            else if (name.EndsWith("-update.zip.sha256", StringComparison.OrdinalIgnoreCase)) smallSha = url;
        }
        if (zip == null) return null;
        if (!Trusted(zip) || (sha != null && !Trusted(sha)))
        {
            Log.Warn("Update asset from an unexpected location ignored");
            return null;
        }
        // the small package only counts with its checksum list, and only from the same trusted place
        if (smallZip == null || smallSha == null || !Trusted(smallZip) || !Trusted(smallSha)) { smallZip = null; smallSha = null; }
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(ver, tag, notes, zip, sha, size, page) { SmallZipUrl = smallZip, SmallShaUrl = smallSha, SmallSize = smallSize };
    }

    private static async Task<string> ReadTextAsync(string url, CancellationToken ct) =>
        IsLocal(url) ? await File.ReadAllTextAsync(url, ct) : await Http.GetStringAsync(url, ct);

    /// <summary>
    /// Reads the small package's checksum list ("hash  name" per line; the first line is the ZIP itself, the others the
    /// engine files this version needs) and checks the installed engine against it. Returns the ZIP's hash if the small
    /// package can be used, otherwise null.
    /// </summary>
    internal static async Task<string?> SmallPackageHashAsync(string listText, string appDir, CancellationToken ct)
    {
        string? zipHash = null;
        foreach (var raw in listText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length != 64) return null;
            if (zipHash == null) { zipHash = parts[0]; continue; }
            string rel = parts[1].Replace('/', Path.DirectorySeparatorChar);
            if (!rel.StartsWith("engine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || rel.Contains("..")) return null;
            string local = Path.Combine(appDir, rel);
            if (!File.Exists(local)) return null;
            string actual;
            await using (var fs = File.OpenRead(local)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) return null; // a different mpv / FFmpeg build
        }
        return zipHash;
    }

    /// <summary>Downloads the update (the small package when the installed engine allows it) and checks its SHA-256. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        if (u.SmallZipUrl != null && u.SmallShaUrl != null)
        {
            try
            {
                string? hash = await SmallPackageHashAsync(await ReadTextAsync(u.SmallShaUrl, ct), AppPaths.BaseDir, ct);
                if (hash != null)
                {
                    var path = await FetchAsync(u.SmallZipUrl, u.SmallSize, Path.Combine(UpdatesDir, $"LinearFrame-{u.Version}-update.zip"), hash, progress, ct);
                    Log.Info($"Update {u.Version}: small package ({new FileInfo(path).Length / 1048576} MB, engine unchanged)");
                    return path;
                }
                Log.Info($"Update {u.Version}: the engine changed - taking the full package");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("Small update package failed, taking the full one: " + ex.Message); }
        }
        string? expected = null;
        if (u.ShaUrl != null)
            expected = (await ReadTextAsync(u.ShaUrl, ct)).Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
        return await FetchAsync(u.ZipUrl, u.Size, Path.Combine(UpdatesDir, $"LinearFrame-{u.Version}-win-x64.zip"), expected, progress, ct);
    }

    private static async Task<string> FetchAsync(string url, long size, string zipPath, string? expectedSha, IProgress<double>? progress, CancellationToken ct)
    {
        var part = zipPath + ".part";
        if (IsLocal(url)) File.Copy(url, part, true);
        else
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
            }
        }

        if (expectedSha != null)
        {
            string actual;
            await using (var fs = File.OpenRead(part)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException("Update checksum mismatch");
            }
        }
        File.Move(part, zipPath, true);
        return zipPath;
    }


    /// <summary>Extracts the ZIP and returns the folder holding the new LinearFrame.exe.</summary>
    public static string Extract(string zipPath, Version v)
    {
        var dest = Path.Combine(UpdatesDir, v.ToString());
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        ZipFile.ExtractToDirectory(zipPath, dest); // throws on entries escaping the folder
        var exe = Directory.EnumerateFiles(dest, "LinearFrame.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException("LinearFrame.exe missing in the update");
        var root = Path.GetDirectoryName(exe)!;
        bool small = zipPath.EndsWith("-update.zip", StringComparison.OrdinalIgnoreCase); // no engine folder by design
        if (!small && !File.Exists(Path.Combine(root, "engine", "libmpv-2.dll"))) throw new InvalidDataException("engine folder missing in the update");
        return root;
    }

    /// <summary>Only a portable copy in a writable folder can replace itself (dev builds never do).</summary>
    public static bool CanSelfUpdate()
    {
        if (AppPaths.IsDevBuild) return false;
        try
        {
            var probe = Path.Combine(AppPaths.BaseDir, $".lf-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Replaces the app files with the new ones (renaming the running ones to *.old). Rolls back on failure.</summary>
    public static bool Apply(string newRoot)
    {
        var appDir = AppPaths.BaseDir;
        var moved = new List<(string Target, string Backup)>();
        var copied = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(newRoot, file);
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var backup = target + ".old";
                    if (File.Exists(backup))
                    {
                        try { File.Delete(backup); }
                        catch { backup = $"{target}.{Guid.NewGuid():N}.old"; }
                    }
                    File.Move(target, backup);
                    moved.Add((target, backup));
                }
                File.Copy(file, target);
                copied.Add(target);
            }
            Log.Info($"Update applied: {copied.Count} files");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Update apply failed - rolling back", ex);
            foreach (var c in copied) { try { File.Delete(c); } catch { /* best effort */ } }
            foreach (var (t, bkp) in moved) { try { File.Move(bkp, t, true); } catch { /* best effort */ } }
            return false;
        }
    }

    /// <summary>Removes leftovers of a previous update (old binaries, downloaded ZIP, extracted files).</summary>
    public static void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.BaseDir, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { /* still in use by the exiting process - next time */ }
            }
        }
        catch { /* ignore */ }
        try
        {
            if (Directory.Exists(UpdatesDir))
                foreach (var e in new DirectoryInfo(UpdatesDir).EnumerateFileSystemInfos())
                {
                    try { if (e is DirectoryInfo d) d.Delete(true); else e.Delete(); } catch { /* ignore */ }
                }
        }
        catch { /* ignore */ }
    }
}
