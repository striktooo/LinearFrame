using System.Runtime.InteropServices;

namespace LinearFrame.Core;

/// <summary>A soft, synthesized camera-shutter sound (no audio files shipped).</summary>
internal static class Sound
{
    private static byte[]? _wav;
    private static GCHandle _pin;

    public static void Shutter()
    {
        try
        {
            if (_wav == null)
            {
                _wav = BuildShutter();
                _pin = GCHandle.Alloc(_wav, GCHandleType.Pinned);
            }
            Native.PlaySound(_pin.AddrOfPinnedObject(), IntPtr.Zero, Native.SND_MEMORY | Native.SND_ASYNC | Native.SND_NODEFAULT);
        }
        catch (Exception ex) { Log.Error("Sound failed", ex); }
    }

    private static byte[] BuildShutter()
    {
        const int rate = 44100;
        int n = (int)(rate * 0.16);
        var samples = new float[n];
        var rng = new Random(7);

        void Click(double start, double decay, double gain, double tone)
        {
            int s0 = (int)(start * rate);
            double lp = 0, prev = 0;
            for (int i = s0; i < n; i++)
            {
                double t = (i - s0) / (double)rate;
                double env = Math.Exp(-t / decay) * (1 - Math.Exp(-t / 0.0004));
                if (env < 0.0005 && t > 0.01) break;
                double noise = rng.NextDouble() * 2 - 1;
                lp += (noise - lp) * 0.35;           // low-pass the noise -> mechanical "thock"
                double hp = lp - prev; prev = lp;    // light high-pass for crispness
                double body = Math.Sin(2 * Math.PI * tone * t) * 0.35;
                samples[i] += (float)((hp * 1.6 + lp * 0.6 + body) * env * gain);
            }
        }

        Click(0.000, 0.012, 0.55, 1800);
        Click(0.055, 0.018, 0.42, 1200);

        // normalize gently
        float max = samples.Max(Math.Abs);
        if (max > 0) for (int i = 0; i < n; i++) samples[i] = samples[i] / max * 0.38f;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = n * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var f in samples) w.Write((short)Math.Clamp(f * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}
