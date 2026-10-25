using System.Text;
using NanoOcr;
using NanoOcr.Pipeline;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// Reads a course-structure file the user pointed at: plain text and markdown are read as
/// they are, a PDF goes through its text layer first and through the built-in OCR only when
/// that layer is empty (a printed web page saved as pictures).
/// </summary>
public static class CourseStructureDocument
{
    /// <summary>Structures run to several pages; the 5-page limit for sorting files does not apply here.</summary>
    public const int MaxPages = 40;

    private static readonly string[] TextExtensions = { ".txt", ".text", ".md", ".markdown", ".csv", ".rtf" };
    private static readonly string[] PdfExtensions = { ".pdf" };

    public static bool IsSupportedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return (TextExtensions.Contains(extension) || PdfExtensions.Contains(extension)) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The document's text, one cell per line or as OCR'd markdown tables. A structure is read
    /// once and has small, tightly packed course codes, so it is rendered finer than the 200 DPI
    /// used when sorting files.
    /// </summary>
    public static string ReadText(string path, Action<string>? progress = null,
        CancellationToken ct = default, int dpi = 300)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (TextExtensions.Contains(extension))
        {
            progress?.Invoke(Path.GetFileName(path));
            return File.ReadAllText(path);
        }

        var layered = PdfText.Extract(path, MaxPages);

        if (layered.TextChars >= Math.Max(120, layered.PageTexts.Count * 40))
            return layered.FullText;

        // A scanned structure: read every page with the offline OCR. Its markdown keeps the
        // table rows, which is what lets the parser tell a code from its department column.
        progress?.Invoke("Loading the built-in OCR models…");

        using var ocr = new NanoOCR(new NanoOcrOptions
        {
            Dpi = Math.Clamp(dpi, 150, 500),
            Threads = Environment.ProcessorCount,
            Mode = ConvertMode.Auto,
        });

        var sb = new StringBuilder();

        foreach (var page in ocr.Predict(path, maxPages: MaxPages))
        {
            ct.ThrowIfCancellationRequested();

            var text = page.Markdown;
            if (string.IsNullOrWhiteSpace(text))
                text = string.Join("\n", page.RecTexts);

            sb.AppendLine(text);
            progress?.Invoke($"Read page {page.PageIndex + 1}");
        }

        return sb.ToString();
    }
}
