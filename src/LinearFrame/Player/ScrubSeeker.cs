using System.Diagnostics;
using System.Windows.Threading;

namespace LinearFrame.Player;

/// <summary>
/// How the hand on a timeline becomes seeks - one for the player's scrubber and the song's waveform, and for the
/// editor's playhead.
///
/// One seek at a time. The next one is asked for only when the picture of the one before is on screen
/// (<see cref="Landed"/>), and then for where the hand is by now - the spots it passed meanwhile are skipped. A mouse
/// reports its position up to a thousand times a second; asking the player for every one of them (as it was done
/// before) made it start over and over without ever getting a picture out while the hand was moving - the film
/// stood still and jumped once the hand slowed down.
///
/// A press shows the exact frame at once. While dragging, the exact frame under the hand is shown as long as the
/// player gets it out quickly; in a file where that takes long (key frames far apart, 4K) a hand moving fast gets the
/// nearest key frame instead - at once - and the exact frame as soon as it slows down. Letting go jumps only if the
/// picture isn't already the exact frame of that spot.
/// </summary>
internal sealed class ScrubSeeker
{
    /// <summary>An exact seek that takes longer than this (ms) is too slow to follow a fast hand with.</summary>
    private const double Quick = 110;
    /// <summary>Further than this share of the timeline within one seek: the hand is travelling, not looking for a frame.</summary>
    private const double Travelling = 0.03;

    private readonly Action<double, bool> _seek; // (time, exact)
    private readonly Func<double> _clock;        // milliseconds
    private readonly DispatcherTimer _lost = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    private bool _held;              // the hand is on the timeline
    private bool _busy;              // a seek is on its way: its picture hasn't been reported yet
    private bool _busyExact;
    private double _busySince;
    private double _at = -1;         // where the last seek asked for goes
    private bool _atExact;           // ...and whether that is the exact frame
    private double _exactAt = -1;    // the last exact seek asked for (on screen or on its way)
    private double _want = -1;       // where the hand has got to meanwhile (-1: nowhere new)
    private double _exactCost = 40;  // how long exact seeks take in this file (ms, running average)

    /// <summary>The length the timeline stands for (seconds) - tells a hand that travels from one that looks for a frame. 0 = unknown.</summary>
    public double Span { get; set; }

    public ScrubSeeker(Action<double, bool> seek, Func<double>? clock = null)
    {
        _seek = seek;
        _clock = clock ?? (static () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        // a picture that is never reported (the file ended meanwhile, the player was busy with something else) must
        // not hold the next seek back for ever
        _lost.Tick += (_, _) => Finish(measured: false);
    }

    /// <summary>The hand is on the timeline at <paramref name="t"/> (pressed there, or dragged there).</summary>
    public void Move(double t)
    {
        if (!_held)
        {
            // the press: its exact frame, at once
            _held = true;
            _want = -1;
            Start(t, exact: true);
            return;
        }
        if (_busy) _want = t; // the newest spot waits for the picture on its way; the ones before it are skipped
        else if (Math.Abs(t - _at) > 1e-4 || !_atExact) Start(t, ExactFor(t));
    }

    /// <summary>The picture of the seek asked for is on screen (the player reports it).</summary>
    public void Landed() => Finish(measured: true);

    private void Finish(bool measured)
    {
        _lost.Stop();
        if (!_busy) return;
        _busy = false;
        if (measured && _busyExact) _exactCost = _exactCost * 0.6 + Math.Min(1000, _clock() - _busySince) * 0.4;
        if (!_held) return;
        if (_want >= 0 && Math.Abs(_want - _at) > 1e-4)
        {
            double t = _want;
            _want = -1;
            Start(t, ExactFor(t));
            return;
        }
        // the hand has not moved on: it gets the exact frame of where it rests
        _want = -1;
        if (!_atExact) Start(_at, exact: true);
    }

    /// <summary>Let go at <paramref name="t"/>.</summary>
    public void Release(double t)
    {
        _held = false;
        _want = -1;
        if (Math.Abs(t - _exactAt) > 0.004) Start(t, exact: true);
    }

    /// <summary>Something else jumped exactly to <paramref name="t"/> (or, with -1, somewhere unknown).</summary>
    public void Jumped(double t)
    {
        _lost.Stop();
        _held = false;
        _busy = false;
        _want = -1;
        _at = _exactAt = t;
        _atExact = true;
    }

    /// <summary>Exact frames while they come quickly - or while the hand moves slowly enough to be looking for one.</summary>
    private bool ExactFor(double t) =>
        _exactCost <= Quick || Span <= 0 || Math.Abs(t - _at) <= Span * Travelling;

    private void Start(double t, bool exact)
    {
        _at = t;
        _atExact = exact;
        if (exact) _exactAt = t;
        _busy = true;
        _busyExact = exact;
        _busySince = _clock();
        _lost.Stop();
        _lost.Start();
        _seek(t, exact);
    }
}
