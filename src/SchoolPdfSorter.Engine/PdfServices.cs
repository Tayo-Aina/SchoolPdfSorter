using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SchoolPdfSorter.Engine;

public sealed record PdfTextInfo(
    int PageCount,
    IReadOnlyList<string> PageTexts,
    string BiggestLineText,
    double BiggestLinePointSize,
    string MetadataTitle)
{
    public string FullText => string.Join("\n", PageTexts);
    public int TextChars => PageTexts.Sum(t => t.Length);
}

/// <summary>
/// Text-layer extraction via PdfPig: the first <see cref="MaxPages"/> pages plus the biggest-font
/// line (likely the title/header). Scanned PDFs come back with little or no text.
/// </summary>
public static class PdfText
{
    /// <summary>How many pages the strict matcher reads (and OCRs when the text layer fails).</summary>
    public const int MaxPages = 5;

    public static PdfTextInfo Extract(string path, int maxPages = MaxPages)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        int pageCount = doc.NumberOfPages;
        var pages = new List<string>();
        string biggest = "";
        double biggestSize = 0;
        int n = Math.Min(Math.Max(1, maxPages), pageCount);
        for (int p = 1; p <= n; p++)
        {
            var page = doc.GetPage(p);
            pages.Add(page.Text ?? "");
            var (line, size) = BiggestFontLine(page);
            if (size > biggestSize)
            {
                biggestSize = size;
                biggest = line;
            }
        }
        string title = "";
        try { title = doc.Information?.Title ?? ""; } catch { /* malformed metadata */ }
        return new PdfTextInfo(pageCount, pages, biggest, biggestSize, title);
    }

    /// <summary>Line whose maximum letter point size is largest; letters grouped by baseline Y.</summary>
    public static (string Text, double Size) BiggestFontLine(Page page)
    {
        var letters = page.Letters;
        if (letters.Count == 0) return ("", 0);

        var horiz = letters
            .Where(l => Math.Abs(l.StartBaseLine.Y - l.EndBaseLine.Y) < 0.5)
            .ToList();
        if (horiz.Count == 0) horiz = letters.ToList();

        double tol = Math.Max(0.5, horiz.Max(l => l.PointSize) * 0.5);
        string bestLine = "";
        double bestSize = 0;
        foreach (var g in horiz.GroupBy(l => Math.Round(l.StartBaseLine.Y / tol) * tol))
        {
            double sz = g.Max(l => l.PointSize);
            if (sz <= bestSize) continue;
            var ordered = g.OrderBy(l => l.StartBaseLine.X).ToList();
            var sb = new StringBuilder();
            double prevEnd = double.NaN;
            foreach (var l in ordered)
            {
                if (!double.IsNaN(prevEnd) && l.StartBaseLine.X - prevEnd > sz * 0.3) sb.Append(' ');
                sb.Append(l.Value);
                prevEnd = l.EndBaseLine.X;
            }
            bestSize = sz;
            bestLine = sb.ToString().Trim();
        }
        return (bestLine, bestSize);
    }
}
