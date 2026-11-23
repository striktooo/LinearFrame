using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LinearFrame.Capture;
using LinearFrame.Core;
using LinearFrame.Imaging;
using LinearFrame.Player;
using LinearFrame.UI;
using LinearFrame.Viewer;

namespace LinearFrame;

/// <summary>
/// The first use must not be the one that loads everything. A few seconds after LinearFrame has started (with Windows,
/// at sign-in) it goes through the motions once, out of sight: the playback engine and FFmpeg are loaded, a player
/// window plays a tiny silent clip, the picture viewer opens a small picture - and all of it is closed again. The first
/// film, song or photo you open then finds the libraries, the graphics card's video decoder and the window code already
/// there, like every later one. (The capture overlay has its own warm-up: <see cref="CaptureSession.Prewarm"/>.)
/// Nothing of this is ever visible, takes the focus or makes a sound; it waits while a game or a film fills the screen
/// and while you are doing something with LinearFrame, and a thing you have already opened yourself is not rehearsed.
/// </summary>
public partial class App
{
    private DispatcherTimer? _rehearsalTimer;
    private int _rehearsalStep;
    private bool _rehearsalWaited;
    private bool _playerUsed, _viewerUsed;
    private string? _rehearsalClip, _rehearsalPicture;
    private long _rehearsalBegan;

    private void RehearseIn(int delayMs)
    {
        if (_rehearsalTimer == null)
        {
            _rehearsalTimer = new DispatcherTimer(DispatcherPriority.Background);
            _rehearsalTimer.Tick += (_, _) => RehearsalTick();
        }
        _rehearsalTimer.Stop();
        _rehearsalTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _rehearsalTimer.Start();
    }

    private void RehearsalTick()
    {
        _rehearsalTimer!.Stop();
        if (_rehearsalStep > 2) return;
        // never under a game or a film, never while something of LinearFrame is on screen
        // (a ghost test copy exists to be tried out while somebody plays: nothing of it can be seen anyway)
        if (CaptureSession.Current != null || (WindowHelper.FullScreenAppInFront() && !AppPaths.Ghost)
            || Windows.Cast<Window>().Any(w => w.IsVisible && !WindowHelper.IsUnseen(w)))
        {
            if (!_rehearsalWaited) Log.Info("Rehearsal waits: the screen is in use");
            _rehearsalWaited = true;
            RehearseIn(Environment.TickCount64 < 5 * 60 * 1000 ? 8000 : 30000);
            return;
        }

        switch (_rehearsalStep++)
        {
            case 0:
                // the libraries and the two small files: off this thread
                _rehearsalBegan = Environment.TickCount64;
                Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    bool mpv = MpvNative.TryLoad(out _);
                    long tMpv = sw.ElapsedMilliseconds;
                    bool ffmpeg = Ffmpeg.Warm();
                    long tFfmpeg = sw.ElapsedMilliseconds - tMpv;
                    var (clip, picture) = RehearsalFiles();
                    return (clip: mpv ? clip : null, picture,
                            how: $"player library {(mpv ? tMpv + " ms" : "missing")}, FFmpeg {(ffmpeg ? tFfmpeg + " ms" : "missing")}, files {sw.ElapsedMilliseconds - tMpv - tFfmpeg} ms");
                }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        (_rehearsalClip, _rehearsalPicture) = (t.Result.clip, t.Result.picture);
                        Log.Info("Rehearsal: " + t.Result.how);
                    }
                    else Log.Warn("Rehearsal (libraries): " + t.Exception?.GetBaseException().Message);
                    RehearseIn(150);
                })));
                break;

            case 1:
                if (_playerUsed || _player != null || _rehearsalClip == null) { RehearseIn(1); break; }
                try
                {
                    PlayerWindow.Rehearse(_rehearsalClip, how =>
                    {
                        Log.Info("Rehearsal: a clip played out of sight in " + how);
                        RehearseIn(400);
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn("Rehearsal (player): " + ex.Message);
                    RehearseIn(400);
                }
                break;

            case 2:
                void Done()
                {
                    Log.Info($"Rehearsal over after {Environment.TickCount64 - _rehearsalBegan} ms");
                    Memory.TrimSoon(3000);
                }
                if (_viewerUsed || _viewer != null || _rehearsalPicture == null) { Done(); break; }
                try
                {
                    ImageViewerWindow.Rehearse(_rehearsalPicture, how =>
                    {
                        Log.Info("Rehearsal: a picture opened out of sight in " + how);
                        Done();
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn("Rehearsal (viewer): " + ex.Message);
                    Done();
                }
                break;
        }
    }

    /// <summary>
    /// The two small files the rehearsals open: a silent one-second clip (built into the app, 2 KB) and a picture
    /// (drawn and written here - which is the first use of the picture encoders, too).
    /// </summary>
    internal static (string? Clip, string? Picture) RehearsalFiles()
    {
        string? clip = null, picture = null;
        try
        {
            string dir = Path.Combine(AppPaths.LocalDir, "rehearsal");
            Directory.CreateDirectory(dir);

            using (var res = typeof(App).Assembly.GetManifestResourceStream("LinearFrame.rehearsal.mp4"))
            {
                if (res != null)
                {
                    string path = Path.Combine(dir, "clip.mp4");
                    if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
                    {
                        using var file = File.Create(path);
                        res.CopyTo(file);
                    }
                    clip = path;
                }
            }

            // a soft gradient, 960 x 600 - big enough to be scaled and drawn like a photo
            const int W = 960, H = 600;
            var px = new PixelBuffer(W, H);
            var d = px.Data;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    d[i] = (byte)(40 + 90 * x / W);
                    d[i + 1] = (byte)(36 + 60 * y / H);
                    d[i + 2] = (byte)(48 + 40 * (x + y) / (W + H));
                    d[i + 3] = 255;
                }
            var bmp = px.ToBitmap();
            byte[] png = ImageIO.EncodePng(bmp); // (the format of every screenshot: its encoder is loaded now)
            byte[] jpg = ImageIO.EncodeJpg(bmp);
            string pic = Path.Combine(dir, "picture.jpg");
            if (!File.Exists(pic) || new FileInfo(pic).Length != jpg.Length) File.WriteAllBytes(pic, jpg);
            if (png.Length > 0) picture = pic;
        }
        catch (Exception ex) { Log.Warn("Rehearsal files: " + ex.Message); }
        return (clip, picture);
    }
}
