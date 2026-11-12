using LinearFrame.Core;

namespace LinearFrame.Player;

internal static class MediaFormats
{
    public static readonly string[] Video =
    {
        ".mp4", ".m4v", ".mov", ".qt", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv", ".f4v", ".ts", ".m2ts", ".mts", ".m2t",
        ".mpg", ".mpeg", ".mpe", ".m2v", ".vob", ".3gp", ".3g2", ".ogv", ".ogm", ".divx", ".xvid", ".rm", ".rmvb", ".mxf", ".dv",
        ".y4m", ".ivf", ".h264", ".264", ".h265", ".265", ".hevc", ".nut", ".wtv", ".amv", ".mk3d",
    };

    public static readonly string[] Audio =
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".wav", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".aifc", ".ape", ".wv",
        ".mka", ".ac3", ".eac3", ".dts", ".mpc", ".tta", ".caf", ".amr", ".au", ".snd", ".mp2", ".m4r", ".alac", ".spx",
    };

    public static readonly string[] Animated = { ".gif", ".webp", ".avif", ".apng" };

    public static readonly string[] StillImages = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".jfif" };

    public static bool IsVideo(string path) => Video.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsAudio(string path) => Audio.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsAnimatedImage(string path) => Animated.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsImage(string path) => StillImages.Contains(Path.GetExtension(path).ToLowerInvariant()) || IsAnimatedImage(path);
    public static bool IsMedia(string path) => IsVideo(path) || IsAudio(path) || IsAnimatedImage(path);

    /// <summary>Formats that ffmpeg can stream-copy back into the same container.</summary>
    public static bool CanTrim(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (IsAnimatedImage(path)) return false;
        return ext is not (".h264" or ".264" or ".h265" or ".265" or ".hevc" or ".y4m" or ".rm" or ".rmvb" or ".wtv" or ".amv" or ".ape" or ".mpc" or ".tta" or ".alac");
    }

    /// <summary>Containers that keep frame-accurate cuts through edit lists.</summary>
    public static bool IsMp4Family(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".qt" or ".m4a" or ".m4b" or ".m4r" or ".3gp" or ".3g2";

    public static string DialogFilter
    {
        get
        {
            static string Pat(IEnumerable<string> e) => string.Join(";", e.Select(x => "*" + x));
            return $"{L.T("Player.FilterAll")}|{Pat(Video.Concat(Audio).Concat(Animated))}" +
                   $"|{L.T("Player.FilterVideo")}|{Pat(Video)}" +
                   $"|{L.T("Player.FilterAudio")}|{Pat(Audio)}" +
                   $"|{L.T("Player.FilterAnim")}|{Pat(Animated)}" +
                   $"|{L.T("Player.FilterAny")}|*.*";
        }
    }

    /// <summary>Extensions offered for "Open with" registration.</summary>
    public static IEnumerable<string> AssociationList =>
        Video.Concat(Audio).Where(e => e is not (".h264" or ".264" or ".h265" or ".265" or ".hevc" or ".y4m" or ".ivf" or ".nut"));
}
