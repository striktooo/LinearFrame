using System.Diagnostics;
using System.Globalization;
using LinearFrame.Core;

namespace LinearFrame.Player.Edit;

/// <summary>
/// The banner's picture for the live preview, decoded while it is needed: FFmpeg reads the banner from the moment asked
/// for, at exactly the size it is shown, into a short queue (about half a second). Playing takes frames off the front;
/// a jump elsewhere starts the decoder there again. However big the window, memory stays at a handful of frames, and a
/// paused decoder lets its FFmpeg go after a moment.
/// </summary>
internal sealed class BannerStream : IDisposable
{
    public const double Fps = 30;
    private const long QueueBytes = 24L << 20;

    private readonly BannerInfo _b;
    private readonly int _capacity, _last, _frameBytes;
    private readonly object _lock = new();
    private readonly Queue<(int Index, byte[] Px)> _queue = new();
    private readonly Stack<byte[]> _pool = new();
    private int _generation;
    private bool _running;
    private int _next;              // the index the running decoder delivers next
    private byte[]? _held;          // the frame given out last (it stays valid until another one is given out)
    private int _heldIndex = -1;
    private Process? _proc;
    private long _lastAsk;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    /// <summary>A decoded frame arrived (raised on the decoder's thread).</summary>
    public event Action? FrameArrived;

    public BannerStream(BannerInfo b, int width)
    {
        _b = b;
        Width = Math.Max(16, width & ~1);
        Height = Math.Max(2, (int)Math.Round(Width * b.Box.Height / (double)Math.Max(1, b.Box.Width)) & ~1);
        _frameBytes = Width * Height * 4;
        _capacity = (int)Math.Clamp(QueueBytes / _frameBytes, 4, 16);
        _last = Math.Max(0, (int)Math.Ceiling(b.Duration * Fps) - 1);
    }

    /// <summary>
    /// The frame for <paramref name="t"/> seconds into the banner, or the nearest one before it while that is still
    /// being decoded (null when there is none yet). The array stays untouched until the next call.
    /// </summary>
    public byte[]? Frame(double t, bool playing)
    {
        int idx = Math.Clamp((int)Math.Floor(t * Fps + 1e-6), 0, _last);
        lock (_lock)
        {
            _lastAsk = Environment.TickCount64;
            // frames before this moment are done with; the newest of them stands in until the right one arrives
            (int Index, byte[] Px)? before = null;
            while (_queue.Count > 0 && _queue.Peek().Index < idx)
            {
                var old = _queue.Dequeue();
                if (before is { } b) Recycle(b.Px);
                before = old;
            }
            bool inQueue = _queue.Count > 0 && _queue.Peek().Index == idx;
            if (inQueue)
            {
                if (before is { } b) Recycle(b.Px);
                Give(idx, _queue.Peek().Px);
            }
            else if (before is { } b && idx - b.Index <= 3) Give(b.Index, b.Px);
            else if (before is { } b2) Recycle(b2.Px);

            if (inQueue)
            {
                // playing on from frames a paused decoder left: start it again behind them, in time
                int end = _queue.Last().Index + 1;
                if (playing && !_running && end <= _last) Start(end, clear: false);
            }
            else
            {
                // the decoder is on its way here (a little behind is fine - it catches up), or it starts again here
                bool coming = _running && _queue.Count == 0 && idx >= _next - 1 && idx <= _next + 15;
                if (!coming) Start(idx, clear: true);
            }
            Monitor.PulseAll(_lock);
            return _heldIndex >= 0 && Math.Abs(_heldIndex - idx) <= 3 ? _held : null;
        }
    }

    private void Give(int index, byte[] px)
    {
        if (_held != null && !ReferenceEquals(_held, px) && !_queue.Any(q => ReferenceEquals(q.Px, _held))) Recycle(_held);
        _held = px;
        _heldIndex = index;
    }

    private void Recycle(byte[] px)
    {
        if (ReferenceEquals(px, _held) || _pool.Count >= 4) return;
        _pool.Push(px);
    }

    /// <summary>Starts FFmpeg at frame <paramref name="index"/>; <paramref name="clear"/> drops what was queued (called under the lock).</summary>
    private void Start(int index, bool clear)
    {
        if (_disposed || index > _last) return;
        Kill();
        if (clear) while (_queue.Count > 0) Recycle(_queue.Dequeue().Px);
        int gen = ++_generation;
        _running = true;
        _next = index;
        var t = new Thread(() => Decode(gen, index)) { IsBackground = true, Name = "banner preview", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    private void Decode(int gen, int start)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg.Exe!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var box = _b.Box;
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-threads", "4" };
            if (start > 0) args.AddRange(new[] { "-ss", F(start / Fps) });
            args.AddRange(new[] { "-i", _b.Path, "-an", "-sn", "-dn",
                "-vf", $"crop={box.Width}:{box.Height}:{box.X}:{box.Y},scale={Width}:{Height}:flags=bicubic,fps={F(Fps)},format=bgra",
                "-f", "rawvideo", "pipe:1" });
            foreach (var a in args) psi.ArgumentList.Add(a);
            p = Process.Start(psi);
            if (p == null) return;
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { /* not allowed - fine */ }
            lock (_lock)
            {
                if (gen != _generation) { Kill(p); return; }
                _proc = p;
            }
            _ = p.StandardError.ReadToEndAsync();
            var stream = p.StandardOutput.BaseStream;
            int index = start;
            while (true)
            {
                byte[] buf;
                lock (_lock)
                {
                    if (gen != _generation) return;
                    buf = _pool.Count > 0 ? _pool.Pop() : new byte[_frameBytes];
                }
                int got = 0;
                while (got < _frameBytes)
                {
                    int n = stream.Read(buf, got, _frameBytes - got);
                    if (n == 0) break;
                    got += n;
                }
                if (got < _frameBytes) break; // the end of the banner
                Banners.Premultiply(buf);
                lock (_lock)
                {
                    // wait for room; nobody asking for a while (paused) = let FFmpeg go, it starts again when needed
                    while (_queue.Count >= _capacity && gen == _generation)
                    {
                        if (Environment.TickCount64 - _lastAsk > 1500) { Recycle(buf); Kill(); _running = false; return; }
                        Monitor.Wait(_lock, 250);
                    }
                    if (gen != _generation) return;
                    _queue.Enqueue((index, buf));
                    _next = ++index;
                }
                FrameArrived?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // killed for a restart
        }
        catch (Exception ex) { Log.Error("Banner preview decode", ex); }
        finally
        {
            lock (_lock)
            {
                if (gen == _generation) { _running = false; _proc = null; }
            }
            if (p != null) { Kill(p); p.Dispose(); }
        }
    }

    private void Kill()
    {
        var p = _proc;
        _proc = null;
        if (p != null) Kill(p);
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { /* gone */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _generation++;
            _running = false;
            Kill();
            _queue.Clear();
            _pool.Clear();
            _held = null;
            Monitor.PulseAll(_lock);
        }
    }
}
