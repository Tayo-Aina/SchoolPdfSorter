using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PdSpike;

internal static class Program
{
    private const int RenderDpi = 150; // A4 @ 150dpi ~= 1240x1754

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--pdf")
                return SingleFile(args[1]);

            if (args.Length >= 2 && args[0] == "--make-text-pdf")
                return MakeTextPdf(args[1]);

            string dir = args.Length >= 2 && args[0] == "--dir"
                ? args[1]
                : ResolveDefaultTestDir();

            return FullSpike(dir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---------------- full spike mode ----------------

    private static int FullSpike(string dir)
    {
        string outDir = Path.Combine(dir, "..", "out");
        Directory.CreateDirectory(outDir);

        foreach (var pdf in Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(pdf);
            bool synthetic = name.StartsWith("synth", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"== {name} ({new FileInfo(pdf).Length} B)");

            // --- render (page 1) ---
            long ms1, ms2;
            SKBitmap bmp;
            {
                var sw = Stopwatch.StartNew();
                (bmp, _) = RenderPage(pdf);
                ms1 = sw.ElapsedMilliseconds;
            }
            {
                var sw = Stopwatch.StartNew();
                using var bmp2 = RenderPage(pdf).Bitmap;
                ms2 = sw.ElapsedMilliseconds;
            }

            using (bmp)
            {
                double mean = MeanLuma(bmp);
                string png = Path.Combine(outDir, Path.GetFileNameWithoutExtension(name) + ".png");
                SavePng(bmp, png);
                bool blank = !(mean > 0 && mean < 255);
                Console.WriteLine(
                    $"RENDER {bmp.Width}x{bmp.Height} color={bmp.ColorType}/{bmp.AlphaType} stride={bmp.RowBytes} " +
                    $"mean={mean:F2}{(blank ? " BLANK!" : " OK")} ms1={ms1} ms2={ms2}");
            }

            // --- text ---
            var swT = Stopwatch.StartNew();
            byte[] bytes = File.ReadAllBytes(pdf);
            using (var doc = PdfDocument.Open(bytes))
            {
                int pages = doc.NumberOfPages;
                var lines = new List<string>();
                for (int p = 1; p <= Math.Min(3, pages); p++)
                {
                    Page page = doc.GetPage(p);
                    var (size, line) = BiggestFontLine(page.Letters);
                    string tail = synthetic
                        ? $" text=\"{Truncate(line, 60)}\""
                        : $" linechars={line.Length}";
                    lines.Add($"  p{p} chars={page.Text.Length} biggest={size:F1}pt{tail}");
                }
                Console.WriteLine($"TEXT pages={pages} total={swT.ElapsedMilliseconds}ms");
                lines.ForEach(Console.WriteLine);
            }
        }

        return 0;
    }

    // ---------------- single-file mode (used for publish proof) ----------------

    private static int SingleFile(string pdf)
    {
        SKBitmap bmp;
        long ms;
        var sw = Stopwatch.StartNew();
        (bmp, _) = RenderPage(pdf);
        ms = sw.ElapsedMilliseconds;
        using (bmp)
        {
            byte[] rgb = ToRgbBytes(bmp);
            Console.WriteLine($"RENDER OK {bmp.Width}x{bmp.Height} ms={ms}");
            Console.WriteLine($"RENDER COLOR {bmp.ColorType}/{bmp.AlphaType} rgbbytes={rgb.Length} px0={rgb[0]},{rgb[1]},{rgb[2]}");
        }

        byte[] bytes = File.ReadAllBytes(pdf);
        using var doc = PdfDocument.Open(bytes);
        int pages = doc.NumberOfPages;
        int chars = 0;
        for (int p = 1; p <= Math.Min(3, pages); p++)
            chars += doc.GetPage(p).Text.Length;
        Console.WriteLine($"TEXT OK chars={chars} pages={pages}");
        return 0;
    }

    // ---------------- text-layer synthetic PDF (for PdfPig demo) ----------------

    private static int MakeTextPdf(string path)
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);

        PdfPageBuilder p1 = builder.AddPage(PageSize.A4);
        p1.AddText("UNILAG", 28, new PdfPoint(60, 760), font);
        p1.AddText("ECON 301 MICROECONOMICS", 20, new PdfPoint(60, 720), font);
        p1.AddText("Week 6: Elasticity", 20, new PdfPoint(60, 692), font);
        double y = 650;
        foreach (string line in new[]
        {
            "1. Define price elasticity of demand.",
            "2. Compute elasticity from the given table.",
            "3. Discuss total revenue and elasticity.",
            "4. Explain income and cross-price elasticity.",
        })
        {
            p1.AddText(line, 11, new PdfPoint(60, y), font);
            y -= 18;
        }

        PdfPageBuilder p2 = builder.AddPage(PageSize.A4);
        p2.AddText("CSC 305 ALGORITHMS", 22, new PdfPoint(60, 760), font);
        p2.AddText("Tutorial sheet 4: graphs and shortest paths.", 11, new PdfPoint(60, 720), font);

        File.WriteAllBytes(path, builder.Build());
        Console.WriteLine($"WROTE {path} pages=2");
        return 0;
    }

    // ---------------- render ----------------

    /// <summary>Renders page 0 (Index 0) at RenderDpi. Caller owns the bitmap.</summary>
    private static (SKBitmap Bitmap, long Ms) RenderPage(string path)
    {
        using var fs = File.OpenRead(path);
        var sw = Stopwatch.StartNew();
        SKBitmap bmp = Conversion.ToImage(
            pdfStream: fs,
            page: 0,                              // System.Index; int 0 == first page
            leaveOpen: true,                      // we own the stream (using)
            password: null,
            options: new RenderOptions(Dpi: RenderDpi));
        sw.Stop();
        return (bmp, sw.ElapsedMilliseconds);
    }

    /// <summary>Mean luma over all pixels (BGRA8888 buffer).</summary>
    private static double MeanLuma(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, rowBytes = bmp.RowBytes;
        int len = checked(rowBytes * h);
        byte[] buf = new byte[len];
        Marshal.Copy(bmp.GetPixels(), buf, 0, len);

        double sum = 0;
        long n = 0;
        for (int y = 0; y < h; y++)
        {
            int off = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int i = off + x * 4; // B,G,R,A
                sum += 0.299 * buf[i + 2] + 0.587 * buf[i + 1] + 0.114 * buf[i];
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>BGRA8888 (stride RowBytes) -> tightly packed interleaved RGB (3 bytes/px).</summary>
    private static byte[] ToRgbBytes(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, rowBytes = bmp.RowBytes;
        byte[] src = new byte[checked(rowBytes * h)];
        Marshal.Copy(bmp.GetPixels(), src, 0, src.Length);
        byte[] dst = new byte[checked(w * h * 3)];
        int di = 0;
        for (int y = 0; y < h; y++)
        {
            int si = y * rowBytes;
            for (int x = 0; x < w; x++, si += 4)
            {
                dst[di++] = src[si + 2]; // R
                dst[di++] = src[si + 1]; // G
                dst[di++] = src[si];     // B
            }
        }
        return dst;
    }

    private static void SavePng(SKBitmap bmp, string path)
    {
        using var fs = File.Create(path);
        if (!bmp.Encode(fs, SKEncodedImageFormat.Png, 100))
            throw new IOException("PNG encode failed");
    }

    // ---------------- text ----------------

    /// <summary>
    /// Groups horizontal letters into visual lines by baseline Y and returns the line
    /// with the largest max letter PointSize, plus its reconstructed text.
    /// </summary>
    private static (double PointSize, string Text) BiggestFontLine(IReadOnlyList<Letter> letters)
    {
        var horiz = new List<Letter>(letters.Count);
        foreach (var l in letters)
        {
            if (l.TextOrientation == TextOrientation.Horizontal && !string.IsNullOrWhiteSpace(l.Value))
                horiz.Add(l);
        }
        if (horiz.Count == 0)
            return (0, string.Empty);

        var lines = new List<List<Letter>>();
        foreach (var l in horiz.OrderByDescending(l => l.StartBaseLine.Y))
        {
            double y = l.StartBaseLine.Y;
            List<Letter>? best = null;
            double bestDelta = double.MaxValue;
            foreach (var cand in lines)
            {
                double refY = cand[0].StartBaseLine.Y;
                double tol = 0.5 * Math.Max(1.0, Math.Max(cand.Max(c => c.PointSize), l.PointSize));
                double delta = Math.Abs(refY - y);
                if (delta <= tol && delta < bestDelta)
                {
                    best = cand;
                    bestDelta = delta;
                }
            }
            if (best is null)
            {
                best = new List<Letter>();
                lines.Add(best);
            }
            best.Add(l);
        }

        List<Letter>? biggest = null;
        double biggestSize = -1;
        foreach (var ln in lines)
        {
            double size = ln.Max(c => c.PointSize);
            if (size > biggestSize)
            {
                biggestSize = size;
                biggest = ln;
            }
        }
        if (biggest is null)
            return (0, string.Empty);

        var sb = new StringBuilder();
        Letter? prev = null;
        foreach (var l in biggest.OrderBy(l => l.StartBaseLine.X))
        {
            if (prev is not null && sb.Length > 0 && sb[^1] != ' ' && l.Value.Length > 0 && !l.Value.StartsWith(' '))
            {
                double gap = l.StartBaseLine.X - prev.EndBaseLine.X;
                if (gap > 0.25 * Math.Max(prev.PointSize, l.PointSize))
                    sb.Append(' ');
            }
            foreach (char c in l.Value)
                if (!char.IsControl(c))
                    sb.Append(c);
            prev = l;
        }

        string text = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (Math.Round(biggestSize, 1), text);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------------- misc ----------------

    private static string ResolveDefaultTestDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            string candidate = Path.Combine(d.FullName, "testpdfs");
            if (Directory.Exists(candidate))
                return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("testpdfs not found; pass --dir <path>");
    }
}
