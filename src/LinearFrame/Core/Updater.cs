using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinearFrame.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, string? ShaUrl, long Size, string PageUrl);

/// <summary>
/// Self-update from GitHub Releases - no server of our own needed.
/// 1) ask the GitHub API for the latest release, 2) download the win-x64 ZIP and verify its SHA-256,
/// 3) swap the files next to the running exe (Windows lets you rename a running exe/DLL), 4) restart.
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

        string? zip = null, sha = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            {
                zip = url;
                size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            }
            else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (zip == null) return null;
        if (!Trusted(zip) || (sha != null && !Trusted(sha)))
        {
            Log.Warn("Update asset from an unexpected location ignored");
            return null;
        }
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(ver, tag, notes, zip, sha, size, page);
    }

    /// <summary>Downloads the release ZIP and checks its SHA-256. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        var zipPath = Path.Combine(UpdatesDir, $"LinearFrame-{u.Version}-win-x64.zip");
        var part = zipPath + ".part";
        if (IsLocal(u.ZipUrl)) File.Copy(u.ZipUrl, part, true);
        else
        {
            using var resp = await Http.GetAsync(u.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
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

        if (u.ShaUrl != null)
        {
            string shaText = IsLocal(u.ShaUrl) ? await File.ReadAllTextAsync(u.ShaUrl, ct) : await Http.GetStringAsync(u.ShaUrl, ct);
            string expected = shaText.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            string actual;
            await using (var fs = File.OpenRead(part)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
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
        if (!File.Exists(Path.Combine(root, "engine", "libmpv-2.dll"))) throw new InvalidDataException("engine folder missing in the update");
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
