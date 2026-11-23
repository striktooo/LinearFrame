using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinearFrame.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, string? ShaUrl, long Size, string PageUrl)
{
    /// <summary>The package without the engine folder - the app and its .NET (and its checksum list), if the release has one.</summary>
    public string? SmallZipUrl { get; init; }
    public string? SmallShaUrl { get; init; }
    public long SmallSize { get; init; }

    /// <summary>The package with nothing but the app's own files, a few MB (and its checksum list), if the release has one.</summary>
    public string? AppZipUrl { get; init; }
    public string? AppShaUrl { get; init; }
    public long AppSize { get; init; }
}

/// <summary>
/// Self-update from GitHub Releases - no server of our own needed.
/// 1) ask the GitHub API for the latest release, 2) download the smallest package that fits the installed copy and
/// verify its SHA-256: "-app.zip" (only the app's own files, a few MB) when the installed .NET and engines are still the
/// right ones, "-update.zip" (the app and its .NET) when only the engines are, otherwise the full win-x64 ZIP,
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

        string? zip = null, sha = null, smallZip = null, smallSha = null, appZip = null, appSha = null;
        long size = 0, smallSize = 0, appSize = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            long bytes = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) { zip = url; size = bytes; }
            else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
            else if (name.EndsWith("-update.zip", StringComparison.OrdinalIgnoreCase)) { smallZip = url; smallSize = bytes; }
            else if (name.EndsWith("-update.zip.sha256", StringComparison.OrdinalIgnoreCase)) smallSha = url;
            else if (name.EndsWith("-app.zip", StringComparison.OrdinalIgnoreCase)) { appZip = url; appSize = bytes; }
            else if (name.EndsWith("-app.zip.sha256", StringComparison.OrdinalIgnoreCase)) appSha = url;
        }
        if (zip == null) return null;
        if (!Trusted(zip) || (sha != null && !Trusted(sha)))
        {
            Log.Warn("Update asset from an unexpected location ignored");
            return null;
        }
        // a smaller package only counts with its checksum list, and only from the same trusted place
        if (smallZip == null || smallSha == null || !Trusted(smallZip) || !Trusted(smallSha)) { smallZip = null; smallSha = null; }
        if (appZip == null || appSha == null || !Trusted(appZip) || !Trusted(appSha)) { appZip = null; appSha = null; }
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(ver, tag, notes, zip, sha, size, page)
        {
            SmallZipUrl = smallZip, SmallShaUrl = smallSha, SmallSize = smallSize,
            AppZipUrl = appZip, AppShaUrl = appSha, AppSize = appSize,
        };
    }

    private static async Task<string> ReadTextAsync(string url, CancellationToken ct) =>
        IsLocal(url) ? await File.ReadAllTextAsync(url, ct) : await Http.GetStringAsync(url, ct);

    /// <summary>The folders a smaller package leaves out: "-update.zip" has no engine folder, "-app.zip" neither that nor .NET.</summary>
    internal static readonly string[] EngineOnly = { "engine" }, EngineAndRuntime = { "engine", "runtime" };

    /// <summary>
    /// Reads a smaller package's checksum list ("hash  name" per line; the first line is the ZIP itself, the others the
    /// files this version expects to find installed already - they may only be in <paramref name="folders"/>) and checks
    /// the installed copy against it. Returns the ZIP's hash if the package can be used, otherwise null.
    /// </summary>
    internal static async Task<string?> PackageHashAsync(string listText, string appDir, string[] folders, CancellationToken ct)
    {
        string? zipHash = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in listText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length != 64) return null;
            if (zipHash == null) { zipHash = parts[0]; continue; }
            string rel = parts[1].Replace('/', Path.DirectorySeparatorChar);
            string? folder = folders.FirstOrDefault(f => rel.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (folder == null || rel.Contains("..")) return null;
            seen.Add(folder);
            string local = Path.Combine(appDir, rel);
            if (!File.Exists(local)) return null;
            string actual;
            await using (var fs = File.OpenRead(local)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) return null; // a different mpv / FFmpeg / .NET build
        }
        // a list that says nothing about a folder the package leaves out proves nothing about it
        return zipHash != null && seen.Count == folders.Length ? zipHash : null;
    }

    /// <summary>Downloads the update (the smallest package the installed copy allows) and checks its SHA-256. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        var smaller = new (string? Zip, string? Sha, long Size, string Suffix, string[] Left, string What)[]
        {
            (u.AppZipUrl, u.AppShaUrl, u.AppSize, "app", EngineAndRuntime, "the app alone (.NET and the engine unchanged)"),
            (u.SmallZipUrl, u.SmallShaUrl, u.SmallSize, "update", EngineOnly, "the app and .NET (engine unchanged)"),
        };
        foreach (var pkg in smaller)
        {
            if (pkg.Zip == null || pkg.Sha == null) continue;
            try
            {
                string? hash = await PackageHashAsync(await ReadTextAsync(pkg.Sha, ct), AppPaths.BaseDir, pkg.Left, ct);
                if (hash == null) continue; // this copy has other files than the package counts on: the next bigger one
                var path = await FetchAsync(pkg.Zip, pkg.Size, Path.Combine(UpdatesDir, $"LinearFrame-{u.Version}-{pkg.Suffix}.zip"), hash, progress, ct);
                Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Update {u.Version}: {pkg.What}, {new FileInfo(path).Length / 1048576.0:0.#} MB"));
                return path;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"Update package '{pkg.Suffix}' failed, taking a bigger one: " + ex.Message); }
        }
        Log.Info($"Update {u.Version}: the full package");
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
        bool small = zipPath.EndsWith("-update.zip", StringComparison.OrdinalIgnoreCase) // no engine folder by design
                     || zipPath.EndsWith("-app.zip", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// The versions of .NET in <paramref name="runtimeDir"/> that are older than the one this process runs on (an update
    /// brought a newer one; each is some 150 MB) are removed. <paramref name="coreLibDir"/> is the folder .NET's core
    /// library was loaded from - for a build that does not run on its own "runtime" folder nothing is touched.
    /// </summary>
    internal static int RemoveOldRuntimes(string runtimeDir, string? coreLibDir)
    {
        int removed = 0;
        try
        {
            if (string.IsNullOrEmpty(coreLibDir) || !Directory.Exists(runtimeDir)) return 0;
            string root = Path.GetFullPath(runtimeDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(coreLibDir).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return 0;
            if (!Version.TryParse(Path.GetFileName(coreLibDir.TrimEnd(Path.DirectorySeparatorChar)), out var current)) return 0;

            var parents = new List<string> { Path.Combine(runtimeDir, "host", "fxr") };
            string shared = Path.Combine(runtimeDir, "shared");
            if (Directory.Exists(shared)) parents.AddRange(Directory.EnumerateDirectories(shared));
            foreach (var parent in parents.Where(Directory.Exists))
                foreach (var dir in Directory.EnumerateDirectories(parent))
                {
                    if (!Version.TryParse(Path.GetFileName(dir), out var v) || v >= current) continue;
                    try { Directory.Delete(dir, true); removed++; }
                    catch { /* still in use by the exiting process - next time */ }
                }
        }
        catch { /* ignore */ }
        return removed;
    }

    /// <summary>
    /// Removes the libraries a one-file LinearFrame.exe (1.2.0 and earlier) unpacked into <paramref name="dir"/> - one
    /// folder per build. Only whole folders nobody uses: if a running copy of that build has even one of the files open
    /// or loaded (Windows then refuses to open it for writing), the folder is left exactly as it is - taking away only
    /// the files that happen not to be loaded yet would break that copy later. (Renaming the folder is no test: Windows
    /// allows that with loaded libraries inside.)
    /// </summary>
    internal static int RemoveUnpackedLibraries(string dir)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var build in Directory.EnumerateDirectories(dir))
            {
                try
                {
                    bool inUse = false;
                    foreach (var file in Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories))
                    {
                        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                        catch { inUse = true; break; }
                    }
                    if (inUse) continue;
                    Directory.Delete(build, true);
                    removed++;
                }
                catch { /* became busy meanwhile - its own business */ }
            }
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { /* ignore */ }
        return removed;
    }

    /// <summary>Removes leftovers of a previous update (old binaries, downloaded ZIP, extracted files, replaced .NET versions).</summary>
    public static void Cleanup()
    {
        string? coreLibDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        int old = RemoveOldRuntimes(Path.Combine(AppPaths.BaseDir, "runtime"), coreLibDir);
        if (old > 0) Log.Info($"Removed {old} folders of a replaced .NET version");
        // up to 1.2.0 LinearFrame.exe was one big file that unpacked a few libraries into the temp folder
        if (!string.IsNullOrEmpty(coreLibDir)) RemoveUnpackedLibraries(Path.Combine(Path.GetTempPath(), ".net", AppPaths.AppName));
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
