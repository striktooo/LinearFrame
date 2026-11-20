namespace LinearFrame.Player;

/// <summary>
/// The window a video opens in: the shape of its picture, to the pixel - no black bars above, below or beside it until
/// you resize the window yourself. All sizes here are physical pixels.
/// </summary>
internal static class PlayerLayout
{
    /// <summary>
    /// A new video's window: the picture fitted into about two thirds of the screen's work area - smaller videos are
    /// enlarged (at most twice their pixels at 100 % scaling), bigger ones shrunk - and never smaller than the window
    /// may get (the controls need their room: a narrow phone video is then given more height instead).
    /// </summary>
    public static (int Width, int Height) WindowFor(int videoW, int videoH, int workW, int workH, double scale, int minW, int minH)
    {
        if (videoW <= 0 || videoH <= 0) return (Math.Max(minW, 960), Math.Max(minH, 540));
        double boxW = workW * 0.66, boxH = workH * 0.78;
        double k = Math.Min(2 * Math.Max(1, scale), Math.Min(boxW / videoW, boxH / videoH));
        double w = videoW * k, h = videoH * k;
        if (w < minW) { w = minW; h = w * videoH / videoW; }
        if (h < minH) { h = minH; w = h * videoW / videoH; }
        // the screen is the limit; a picture so narrow or so flat that it can't have both its shape and the smallest
        // window keeps the smallest window (then, and only then, there are bars)
        double maxW = workW * 0.96, maxH = workH * 0.94;
        if (w > maxW || h > maxH)
        {
            double s = Math.Min(maxW / w, maxH / h);
            w = Math.Max(minW, w * s);
            h = Math.Max(minH, h * s);
        }
        return Exact(videoW, videoH, (int)Math.Round(w), (int)Math.Round(h), minW, minH);
    }

    /// <summary>
    /// The size nearest to <paramref name="w"/> × <paramref name="h"/> that the picture fills completely. A window
    /// that is "the right shape" only after rounding leaves the player a pixel short on one side - a black line along
    /// an edge; so the sizes around the wanted one are tried against the player's own arithmetic (<see cref="Fitted"/>).
    /// </summary>
    internal static (int Width, int Height) Exact(int videoW, int videoH, int w, int h, int minW, int minH)
    {
        for (int d = 0; d <= 8; d++)
        {
            foreach (int hh in d == 0 ? new[] { h } : new[] { h - d, h + d })
            {
                if (hh < minH) continue;
                int w0 = (int)Math.Round((double)hh * videoW / videoH);
                foreach (int ww in new[] { w0, w0 - 1, w0 + 1 })
                {
                    if (ww < minW) continue;
                    if (Fitted(videoW, videoH, ww, hh) == (ww, hh)) return (ww, hh);
                }
            }
        }
        return (Math.Max(minW, w), Math.Max(minH, h));
    }

    /// <summary>
    /// How big the player (mpv, video/out/aspect.c) draws a picture of the given display size inside a window - in its
    /// own single-precision arithmetic, cut down to whole pixels the way it does. Equal to the window = no bars.
    /// </summary>
    internal static (int Width, int Height) Fitted(int videoW, int videoH, int windowW, int windowH)
    {
        int fw = windowW;
        int fh = (int)(double)((float)windowW / videoW * videoH);
        if (fh > windowH || fh < videoH)
        {
            int tw = (int)(double)((float)windowH / videoH * videoW);
            if (tw <= windowW)
            {
                fh = windowH;
                fw = tw;
            }
        }
        return (fw, fh);
    }
}
