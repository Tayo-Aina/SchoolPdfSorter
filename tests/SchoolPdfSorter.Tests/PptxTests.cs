using System.IO.Compression;
using System.Xml.Linq;
using SchoolPdfSorter.Engine;

namespace SchoolPdfSorter.Tests;

public sealed class PptxTests
{
    private static readonly string[] Courses =
    {
        "EIE 326 Data Structures",
        "MTH 101 Elementary Mathematics I"
    };

    // ============================================================== extraction

    [Fact]
    public void Extract_reads_slides_in_presentation_order_and_the_title()
    {
        var root = NewDirectory();

        try
        {
            var path = PptxFixture.Create(root, "deck.pptx", "Signals and Systems",
                "EIE 326 Data Structures", "Week 3", "MTH 101 Elementary Mathematics I");

            var info = PptxText.Extract(path);

            Assert.Equal(3, info.SlideCount);
            Assert.Equal(
                new[] { "EIE 326 Data Structures", "Week 3", "MTH 101 Elementary Mathematics I" },
                info.SlideTexts);
            Assert.Equal("EIE 326 Data Structures", info.FirstLine);
            Assert.Equal("Signals and Systems", info.MetadataTitle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_stops_at_five_slides_but_still_counts_the_deck()
    {
        var root = NewDirectory();

        try
        {
            var slides = Enumerable.Range(1, 7).Select(n => $"slide {n} text").ToArray();
            var path = PptxFixture.Create(root, "long.pptx", "Long deck", slides);

            var info = PptxText.Extract(path);

            Assert.Equal(7, info.SlideCount);
            Assert.Equal(5, info.SlideTexts.Count);
            Assert.Equal("slide 5 text", info.SlideTexts[^1]);
            Assert.DoesNotContain("slide 6 text", info.FullText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Signals_read_a_presentation_as_pages()
    {
        var root = NewDirectory();

        try
        {
            var path = PptxFixture.Create(root, "deck.ppsx", "Operating Systems",
                "Lecture 1", "EIE 414 Operating Systems");

            var signals = Signals.ExtractSafe(path);

            Assert.True(signals.IsPresentation);
            Assert.Equal(2, signals.PageCount);
            Assert.Equal(2, signals.FirstPages.Count);
            Assert.Equal("Operating Systems", signals.MetadataTitle);
            Assert.Equal("Lecture 1", signals.BiggestLine);
            Assert.Contains("EIE 414", signals.FirstPagesText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Signals_read_a_macro_enabled_presentation_as_pages()
    {
        var root = NewDirectory();

        try
        {
            var path = PptxFixture.Create(root, "macro.pptm", "Signals and Systems",
                "EIE 326 Data Structures", "Week 3", "MTH 101 Elementary Mathematics I");

            var signals = Signals.ExtractSafe(path);

            Assert.True(signals.IsPresentation);
            Assert.Equal(3, signals.PageCount);
            Assert.Equal(
                new[] { "EIE 326 Data Structures", "Week 3", "MTH 101 Elementary Mathematics I" },
                signals.FirstPages);
            Assert.Equal("Signals and Systems", signals.MetadataTitle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ================================================================= sorter

    [Fact]
    public void Sorter_moves_a_deck_by_its_slide_text()
    {
        var root = NewDirectory();

        try
        {
            var path = PptxFixture.Create(root, "lecture notes.pptx", "Data Structures",
                "Lecture 3", "EIE 326 Data Structures");

            var result = new Sorter(Courses).Run(new[] { path });

            var item = Assert.Single(result.Items);
            Assert.True(item.IsMove);
            Assert.Equal("eie326", item.Code);
            Assert.Equal("slide", item.Tier);
            Assert.Contains("slide 2", item.Reason);
            Assert.Contains("EIE 326", item.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Sorter_maps_a_spelling_slip_to_the_listed_course()
    {
        var root = NewDirectory();

        try
        {
            var path = PptxFixture.Create(root, "finance.pptx", "Finance",
                "CEG 321 Sources of Finance");

            var result = new Sorter(new[] { "GEC 321 Entrepreneurial Studies" }).Run(new[] { path });

            var item = Assert.Single(result.Items);
            Assert.True(item.IsMove);
            Assert.Equal("gec321", item.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Sorter_leaves_a_picture_only_deck_alone()
    {
        var root = NewDirectory();

        try
        {
            var path = PptxFixture.Create(root, "week 3 slides.pptx", "Week 3", "", "");

            var result = new Sorter(Courses).Run(new[] { path });

            var item = Assert.Single(result.Items);
            Assert.False(item.IsMove);
            Assert.Equal("slides", item.Tier);
            Assert.Contains("pictures", item.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Sorter_leaves_picture_only_decks_that_cannot_be_verified_alone()
    {
        var root = NewDirectory();

        try
        {
            var folderDeck = PptxFixture.Create(
                Path.Combine(root, "EIE 326 Data Structures"), "week 3 slides.pptx", "Week 3", "", "");

            var combinedDeck = PptxFixture.Create(
                root, "EIE 326 combined slides.pptx", "Week 3", "", "");

            var result = new Sorter(Courses).Run(new[] { folderDeck, combinedDeck });

            Assert.Equal(2, result.Items.Count);
            Assert.All(result.Items, item => Assert.False(item.IsMove));
            Assert.Contains("no text to verify", result.Items.Single(i => i.File.Path == folderDeck).Reason);
            Assert.Contains("no text to check", result.Items.Single(i => i.File.Path == combinedDeck).Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ================================================================ scanner

    [Fact]
    public void Scanner_returns_only_the_requested_extensions()
    {
        var root = NewDirectory();

        try
        {
            Touch(root, "notes.pdf");
            Touch(root, "deck.pptx");
            Touch(root, "macro.pptm");
            Touch(root, "show.ppsx");
            Touch(root, "legacy.ppt");
            Touch(root, "~$deck.pptx");
            Touch(root, "list.txt");

            var pdfs = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);
            Assert.Equal(new[] { "notes.pdf" }, Names(pdfs));
            Assert.Equal(0, pdfs.TemporaryFiles);

            var decks = PdfScanner.Scan(root, root, includeSubfolders: true, Courses,
                new[] { ".pptx", ".ppsx", ".pptm" });
            Assert.Equal(new[] { "deck.pptx", "macro.pptm", "show.ppsx" }, Names(decks));
            Assert.Equal(1, decks.TemporaryFiles);

            var both = PdfScanner.Scan(root, root, includeSubfolders: true, Courses,
                new[] { ".pdf", ".pptx", ".ppsx", ".pptm" });
            Assert.Equal(new[] { "deck.pptx", "macro.pptm", "notes.pdf", "show.ppsx" }, Names(both));
            Assert.DoesNotContain("legacy.ppt", Names(both));
            Assert.Equal(1, both.TemporaryFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ================================================================ helpers

    private static string[] Names(PdfScanResult result)
        => result.Files.Select(file => Path.GetFileName(file.Path))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "SchoolPdfSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Touch(string directory, string name)
        => File.WriteAllBytes(Path.Combine(directory, name), new byte[] { 1, 2, 3, 4 });
}

/// <summary>Builds the smallest valid .pptx/.ppsx/.pptm a reader needs: parts, rels, slides and title.</summary>
internal static class PptxFixture
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public static string Create(string directory, string name, string title, params string[] slides)
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, name);
        if (File.Exists(path))
            File.Delete(path);

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        Add(zip, "ppt/presentation.xml", Presentation(slides.Length));
        Add(zip, "ppt/_rels/presentation.xml.rels", Relationships(slides.Length));

        // Write the slides in reverse so passing cannot depend on zip entry order.
        for (var i = slides.Length; i >= 1; i--)
            Add(zip, $"ppt/slides/slide{i}.xml", Slide(slides[i - 1]));

        Add(zip, "docProps/core.xml", Core(title));

        return path;
    }

    private static string Presentation(int count)
    {
        var ids = Enumerable.Range(0, count)
            .Select(i => new XElement(P + "sldId",
                new XAttribute("id", 256 + i),
                new XAttribute(R + "id", $"rId{i + 2}")));

        var doc = new XDocument(
            new XElement(P + "presentation",
                new XAttribute(XNamespace.Xmlns + "p", P),
                new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(P + "sldIdLst", ids)));

        return doc.ToString();
    }

    private static string Relationships(int count)
    {
        var rels = Enumerable.Range(0, count)
            .Select(i => new XElement(Rel + "Relationship",
                new XAttribute("Id", $"rId{i + 2}"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide"),
                new XAttribute("Target", $"slides/slide{i + 1}.xml")));

        var doc = new XDocument(
            new XElement(Rel + "Relationships", rels));

        return doc.ToString();
    }

    private static string Slide(string text)
    {
        var body = new XElement(P + "txBody");

        if (text.Length > 0)
        {
            body.Add(new XElement(A + "p",
                new XElement(A + "r", new XElement(A + "t", text))));
        }

        var doc = new XDocument(
            new XElement(P + "sld",
                new XAttribute(XNamespace.Xmlns + "a", A),
                new XAttribute(XNamespace.Xmlns + "p", P),
                new XElement(P + "cSld",
                    new XElement(P + "spTree",
                        new XElement(P + "sp", body)))));

        return doc.ToString();
    }

    private static string Core(string title)
    {
        var doc = new XDocument(
            new XElement(Cp + "coreProperties",
                new XAttribute(XNamespace.Xmlns + "cp", Cp),
                new XAttribute(XNamespace.Xmlns + "dc", Dc),
                new XElement(Dc + "title", title)));

        return doc.ToString();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
