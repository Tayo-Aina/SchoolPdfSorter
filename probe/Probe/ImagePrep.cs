namespace Probe;

/// <summary>
/// Port of the Gemma4 PIL image preprocessing used by EmbeddingGemma 2:
/// aspect-ratio-preserving bicubic resize (multiples of 48px, 2520-patch budget),
/// rescale 1/255, patchify to [patches, 768] in (patch_row, patch_col, ph, pw, c) order,
/// position ids (x=col, y=row) 0-based row-major padded with -1.
/// Validate pixel/position output against the saved reference buffers.
/// </summary>
public sealed class PreparedPage
{
    public int TargetW;
    public int TargetH;
    public int GridW;
    public int GridH;
    public int Patches;
    public int SoftTokens;
    public required byte[] ResizedU8;    // RGB interleaved, TargetH x TargetW x 3
    public required float[] PixelValues; // [MaxPatches * 768], row-major patch order, rescaled 0..1
    public required long[] PositionIds;  // [MaxPatches * 2], (x, y), padding rows = -1
}

public static class ImagePrep
{
    public const int PatchSize = 16;
    public const int Pooling = 3;
    public const int MaxSoftTokens = 280;
    public const int MaxPatches = MaxSoftTokens * Pooling * Pooling; // 2520
    public const int PatchDims = PatchSize * PatchSize * 3;          // 768
    private const float Rescale = (float)(1.0 / 255.0);

    public static (int H, int W) TargetSize(int h, int w)
    {
        double targetPx = MaxPatches * (double)(PatchSize * PatchSize);
        double factor = Math.Sqrt(targetPx / ((double)h * w));
        int sideMult = Pooling * PatchSize;
        int th = (int)Math.Floor(factor * h / sideMult) * sideMult;
        int tw = (int)Math.Floor(factor * w / sideMult) * sideMult;
        return (th, tw);
    }

    public static PreparedPage Prepare(byte[] rgb, int w, int h)
    {
        var (th, tw) = TargetSize(h, w);
        byte[] resized = (th == h && tw == w) ? rgb : ResizeBicubic(rgb, w, h, tw, th);
        int gh = th / PatchSize, gw = tw / PatchSize;
        int patches = gh * gw;
        var pv = new float[MaxPatches * PatchDims];
        var pos = new long[MaxPatches * 2];

        for (int r = 0; r < gh; r++)
        {
            for (int c = 0; c < gw; c++)
            {
                int pi = r * gw + c;
                pos[pi * 2] = c;
                pos[pi * 2 + 1] = r;
                int dst = pi * PatchDims;
                for (int ph = 0; ph < PatchSize; ph++)
                {
                    int srcRow = (r * PatchSize + ph) * tw * 3 + c * PatchSize * 3;
                    for (int pw = 0; pw < PatchSize; pw++)
                    {
                        int src = srcRow + pw * 3;
                        pv[dst] = resized[src] * Rescale;
                        pv[dst + 1] = resized[src + 1] * Rescale;
                        pv[dst + 2] = resized[src + 2] * Rescale;
                        dst += 3;
                    }
                }
            }
        }
        for (int i = patches; i < MaxPatches; i++)
        {
            pos[i * 2] = -1;
            pos[i * 2 + 1] = -1;
        }

        return new PreparedPage
        {
            TargetH = th, TargetW = tw, GridH = gh, GridW = gw,
            Patches = patches, SoftTokens = (gh / Pooling) * (gw / Pooling),
            ResizedU8 = resized, PixelValues = pv, PositionIds = pos,
        };
    }

    private static double Cubic(double x)
    {
        const double a = -0.5;
        if (x < 0) x = -x;
        if (x < 1.0) return ((a + 2.0) * x - (a + 3.0)) * x * x + 1.0;
        if (x < 2.0) return (((x - 5.0) * x + 8.0) * x - 4.0) * a;
        return 0.0;
    }

    private sealed class CoeffSet
    {
        public required int[] Start;
        public required int[] Count;
        public required double[][] Weights;
    }

    private static CoeffSet Coeffs(int inSize, int outSize)
    {
        var cs = new CoeffSet { Start = new int[outSize], Count = new int[outSize], Weights = new double[outSize][] };
        double scale = (double)inSize / outSize;
        double filterscale = Math.Max(scale, 1.0);
        double support = 2.0 * filterscale; // bicubic filter support
        double ss = 1.0 / filterscale;
        for (int o = 0; o < outSize; o++)
        {
            double center = (o + 0.5) * scale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            int cnt = Math.Max(1, xmax - xmin);
            var wts = new double[cnt];
            double sum = 0;
            for (int i = 0; i < cnt; i++)
            {
                double wv = Cubic((i + xmin - center + 0.5) * ss);
                wts[i] = wv;
                sum += wv;
            }
            if (sum != 0)
                for (int i = 0; i < cnt; i++) wts[i] /= sum;
            cs.Start[o] = xmin;
            cs.Count[o] = cnt;
            cs.Weights[o] = wts;
        }
        return cs;
    }

    /// <summary>Two-pass separable bicubic, uint8 intermediate between passes (Pillow-style).</summary>
    public static byte[] ResizeBicubic(byte[] src, int sw, int sh, int dw, int dh)
    {
        var hc = Coeffs(sw, dw);
        var tmp = new byte[sh * dw * 3];
        for (int y = 0; y < sh; y++)
        {
            int srcRow = y * sw * 3;
            int dstRow = y * dw * 3;
            for (int x = 0; x < dw; x++)
            {
                int st = hc.Start[x], cnt = hc.Count[x];
                var wts = hc.Weights[x];
                double r = 0, g = 0, b = 0;
                int idx = srcRow + st * 3;
                for (int i = 0; i < cnt; i++, idx += 3)
                {
                    double wv = wts[i];
                    r += src[idx] * wv;
                    g += src[idx + 1] * wv;
                    b += src[idx + 2] * wv;
                }
                int o = dstRow + x * 3;
                tmp[o] = Clamp8(r);
                tmp[o + 1] = Clamp8(g);
                tmp[o + 2] = Clamp8(b);
            }
        }

        var vc = Coeffs(sh, dh);
        var dst = new byte[dh * dw * 3];
        for (int y = 0; y < dh; y++)
        {
            int st = vc.Start[y], cnt = vc.Count[y];
            var wts = vc.Weights[y];
            int dstRow = y * dw * 3;
            for (int x = 0; x < dw; x++)
            {
                double r = 0, g = 0, b = 0;
                int idx = (st * dw + x) * 3;
                for (int i = 0; i < cnt; i++, idx += dw * 3)
                {
                    double wv = wts[i];
                    r += tmp[idx] * wv;
                    g += tmp[idx + 1] * wv;
                    b += tmp[idx + 2] * wv;
                }
                int o = dstRow + x * 3;
                dst[o] = Clamp8(r);
                dst[o + 1] = Clamp8(g);
                dst[o + 2] = Clamp8(b);
            }
        }
        return dst;
    }

    private static byte Clamp8(double v)
    {
        long i = (long)Math.Floor(v + 0.5);
        if (i < 0) return 0;
        if (i > 255) return 255;
        return (byte)i;
    }
}
