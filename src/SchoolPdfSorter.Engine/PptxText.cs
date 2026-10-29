using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace SchoolPdfSorter.Engine;

/// <summary>The text a PowerPoint file gives up without Office: its slides and title.</summary>
public sealed record PptxTextInfo(
    int SlideCount,
    IReadOnlyList<string> SlideTexts,
    string FirstLine,
    string MetadataTitle)
{
    public string FullText => string.Join("\n", SlideTexts);
    public int TextChars => SlideTexts.Sum(t => t.Length);
}

/// <summary>
/// Reads .pptx and .ppsx natively: the file is a zip of slide XML, so the first slides' text is
/// available without Office, a converter or OCR. Only text is read; a PowerPoint file made of
/// pictures has no text to find, and notes pages are not read (the slides are what a student sees).
/// </summary>
public static class PptxText
{
    /// <summary>How many slides the strict matcher reads, the same budget as a PDF's pages.</summary>
    public const int MaxSlides = 5;

    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public static bool IsPresentation(string path)
        => path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ppsx", StringComparison.OrdinalIgnoreCase);

    public static PptxTextInfo Extract(string path, int maxSlides = MaxSlides)
    {
        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var order = SlideOrder(zip);
        var texts = new List<string>();

        for (var i = 0; i < order.Count; i++)
        {
            if (i >= Math.Max(1, maxSlides))
                break;

            texts.Add(ReadSlide(zip, order[i]));
        }

        var firstLine = texts
            .SelectMany(text => text.Split('\n'))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "";

        return new PptxTextInfo(order.Count, texts, firstLine, ReadTitle(zip));
    }

    /// <summary>
    /// Slide paths in the order the presentation shows them. presentation.xml lists the slides by
    /// relationship id, and the rels file maps those to slide XML; falling back to numeric entry
    /// names only when that structure is missing or malformed.
    /// </summary>
    private static List<string> SlideOrder(ZipArchive zip)
    {
        var presentation = zip.GetEntry("ppt/presentation.xml");
        var rels = zip.GetEntry("ppt/_rels/presentation.xml.rels");

        if (presentation is not null && rels is not null)
        {
            try
            {
                var slideIds = Load(presentation).Descendants(P + "sldId")
                    .Select(element => element.Attribute(R + "id")?.Value)
                    .Where(id => id is not null)
                    .ToList();

                var targets = Load(rels).Descendants(Rel + "Relationship")
                    .Where(element =>
                        element.Attribute("Type")?.Value.EndsWith("/slide", StringComparison.Ordinal) == true)
                    .Select(element => (
                        Id: element.Attribute("Id")?.Value,
                        Target: element.Attribute("Target")?.Value))
                    .Where(pair => pair.Id is not null && pair.Target is not null)
                    .ToDictionary(pair => pair.Id!, pair => pair.Target!);

                var ordered = slideIds
                    .Where(id => id is not null && targets.ContainsKey(id))
                    .Select(id => Normalize("ppt", targets[id!]))
                    .Where(path => zip.GetEntry(path) is not null)
                    .ToList();

                if (ordered.Count > 0)
                    return ordered;
            }
            catch
            {
                // A malformed presentation part falls through to the numeric order below.
            }
        }

        return zip.Entries
            .Where(entry =>
                entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains("/_rels/", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.FullName)
            .OrderBy(SlideNumber)
            .ToList();
    }

    private static string ReadSlide(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
            return "";

        try
        {
            var lines = new List<string>();

            // One paragraph per line. Runs inside a paragraph are joined with no separator,
            // because PowerPoint often splits a single word across several runs.
            foreach (var paragraph in Load(entry).Descendants(A + "p"))
            {
                var text = string.Concat(paragraph.Descendants(A + "t").Select(run => run.Value)).Trim();
                if (text.Length > 0)
                    lines.Add(text);
            }

            return string.Join("\n", lines);
        }
        catch
        {
            return "";
        }
    }

    private static string ReadTitle(ZipArchive zip)
    {
        var entry = zip.GetEntry("docProps/core.xml");
        if (entry is null)
            return "";

        try
        {
            return Load(entry).Descendants(Dc + "title").FirstOrDefault()?.Value.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static int SlideNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    /// <summary>Resolves a relationship target relative to its part's folder, collapsing "..".</summary>
    private static string Normalize(string baseDir, string target)
    {
        var combined = target.StartsWith('/')
            ? target.TrimStart('/')
            : baseDir + "/" + target;

        var parts = new List<string>();

        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join("/", parts);
    }
}
