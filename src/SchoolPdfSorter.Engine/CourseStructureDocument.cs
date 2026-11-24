using System.Text;
using NanoOcr;
using NanoOcr.Pipeline;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// Reads the course-structure table a student pastes or drops in. Plain text is read directly;
/// a born-digital PDF uses its text layer; a scanned PDF uses the same offline OCR as the sorter.
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
            return (TextExtensions.Contains(extension) || PdfExtensions.Contains(extension))
                   && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

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

        // A scanned structure: read every page with the shared offline text reader. Pages run
        // on a bounded worker pool over one model store, then join in ascending page index so
        // CourseStructure.ParseDetailed sees the same table rows as the old sequential read.
        progress?.Invoke("Getting the text reader ready");

        int budget = ResourceBudget.TotalBudget(0);
        int workers = ResourceBudget.OcrWorkers(budget);
        var ocr = OcrModelHost.Get(Math.Clamp(dpi, 150, 500), budget, ConvertMode.Auto, out _);

        var sb = new StringBuilder();

        foreach (var (_, pages, error) in ocr.PredictMany(
                     new[] { path }, cores: workers, maxPages: MaxPages))
        {
            if (error is not null)
                throw error;

            foreach (var page in pages!.OrderBy(p => p.PageIndex))
            {
                ct.ThrowIfCancellationRequested();

                var text = page.Markdown;
                if (string.IsNullOrWhiteSpace(text))
                    text = string.Join("\n", page.RecTexts);

                sb.AppendLine(text);
                progress?.Invoke($"Page {page.PageIndex + 1}");
            }
        }

        return sb.ToString();
    }
}
