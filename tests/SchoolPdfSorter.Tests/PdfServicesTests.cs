using SchoolPdfSorter.Engine;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SchoolPdfSorter.Tests;

public class PdfServicesTests
{
    private static string? FindFixture(string name)
    {
        var d = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var c = Path.Combine(d, "probe", "pdspike", "testpdfs", name);
            if (File.Exists(c)) return c;
            var p = Directory.GetParent(d);
            if (p is null) break;
            d = p.FullName;
        }
        return null;
    }

    [Fact]
    public void Extract_stops_at_max_pages()
    {
        Assert.Equal(5, PdfText.MaxPages);

        var path = Path.Combine(Path.GetTempPath(), $"pdftext-{Guid.NewGuid():N}.pdf");
        try
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            for (var i = 1; i <= 6; i++)
            {
                var page = builder.AddPage(PageSize.A4);
                page.AddText(i == 6 ? "MTH 101" : $"page {i}", 12, new PdfPoint(40, 750), font);
            }
            File.WriteAllBytes(path, builder.Build());

            var info = PdfText.Extract(path);

            Assert.Equal(6, info.PageCount);
            Assert.Equal(5, info.PageTexts.Count);
            Assert.DoesNotContain("MTH 101", info.FullText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Extract_synth_text_finds_biggest_font_line()
    {
        var path = FindFixture("synth_text.pdf");
        if (path is null) return;
        var info = PdfText.Extract(path);
        Assert.True(info.PageCount >= 2);
        Assert.True(info.PageTexts[0].Length > 100);
        Assert.True(info.BiggestLinePointSize > 20);
        Assert.Contains("UNILAG", info.BiggestLineText);
    }

    [Fact]
    public void Extract_scan_pdf_reports_no_text()
    {
        var path = FindFixture("real2_waec.pdf") ?? FindFixture("synth_school_a4.pdf");
        if (path is null) return;
        var info = PdfText.Extract(path);
        Assert.True(info.PageCount >= 1);
        Assert.True(info.PageTexts.All(t => t.Trim().Length < 50));
    }
}
