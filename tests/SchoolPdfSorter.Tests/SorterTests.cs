using SchoolPdfSorter.Engine;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SchoolPdfSorter.Tests;

public class SorterTests
{
    [Fact]
    public void Sorter_leaves_an_unlisted_sibling_course_alone_without_ocr()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sorter-{Guid.NewGuid():N}.pdf");

        try
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(PageSize.A4);
            page.AddText("EIE 315 Signals and Systems", 12, new PdfPoint(40, 750), font);
            File.WriteAllBytes(path, builder.Build());

            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("unlisted", item.Tier);
            Assert.Equal(0, item.OcrPages);
            Assert.Equal(0, run.Stats.OcrFiles);
            Assert.Contains("EIE 315", item.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_skips_a_different_course_in_the_file_name_without_opening_the_pdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CSC 201 notes {Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Array.Empty<byte>());

        try
        {
            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("unlisted", item.Tier);
            Assert.Equal(0, item.OcrPages);
            Assert.Equal(0, run.Stats.OcrFiles);
            Assert.Equal(0, run.Stats.OpenedFiles);
            Assert.Contains("CSC 201", item.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_moves_a_listed_code_in_the_file_name_without_opening_the_pdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"EIE 301 notes {Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Array.Empty<byte>());

        try
        {
            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.True(item.IsMove);
            Assert.Equal("filename", item.Tier);
            Assert.Equal(0, run.Stats.OpenedFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_leaves_a_timetable_named_with_one_course_alone_without_opening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"EIE 301 timetable {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path, "EIE 301 Signals and Systems");

            var sorter = new Sorter(new[]
            {
                "EIE 301 Signals and Systems",
                "MTH 101 Elementary Mathematics I",
            });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("timetable", item.Tier);
            Assert.Equal(0, item.OcrPages);
            Assert.Equal(0, run.Stats.OpenedFiles);
            Assert.Contains("timetable", item.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_checks_a_generic_file_inside_a_course_folder_and_leaves_a_timetable()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"EIE 326 {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "4_5767341724984678205.pdf");

        try
        {
            CreatePdf(path, "FIRST SEMESTER EXAMINATION TIME TABLE - EIE 326");

            var sorter = new Sorter(new[] { "EIE 326 Data Structures" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("timetable", item.Tier);
            Assert.Equal(1, run.Stats.OpenedFiles);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Sorter_does_not_move_an_unreadable_file_inside_a_course_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"EIE 326 {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "broken.pdf");
        File.WriteAllBytes(path, Array.Empty<byte>());

        try
        {
            var sorter = new Sorter(new[] { "EIE 326 Data Structures" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Contains("could not be read to verify", item.Reason);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Sorter_leaves_a_schedule_named_with_one_course_alone_without_opening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"EIE_326_schedule_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Array.Empty<byte>());

        try
        {
            var sorter = new Sorter(new[] { "EIE 326 Data Structures" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("timetable", item.Tier);
            Assert.Equal(0, run.Stats.OpenedFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_leaves_a_content_timetable_alone_even_with_one_listed_code()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schedule {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path, "FIRST SEMESTER EXAMINATION TIME TABLE - EIE 301 Signals and Systems");

            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("timetable", item.Tier);
            Assert.Equal(0, item.OcrPages);
            Assert.Contains("timetable", item.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_leaves_a_text_layer_pack_with_two_listed_courses_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"questions {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path,
                "EIE 301 Signals and Systems",
                "MTH 101 Elementary Mathematics I");

            var sorter = new Sorter(new[]
            {
                "EIE 301 Signals and Systems",
                "MTH 101 Elementary Mathematics I",
            });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("multi", item.Tier);
            Assert.Equal(0, item.OcrPages);
            Assert.Contains("EIE 301", item.Reason);
            Assert.Contains("MTH 101", item.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_moves_a_pack_that_repeats_the_same_listed_course()
    {
        var path = Path.Combine(Path.GetTempPath(), $"questions same {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path,
                "EIE 301 Signals and Systems",
                "EIE 301 past questions");

            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.True(item.IsMove);
            Assert.Equal(0, run.Stats.OcrFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_leaves_a_listed_course_plus_a_different_department_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"questions mixed {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path,
                "EIE 301 Signals and Systems",
                "CSC 201 Data Structures");

            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("multi", item.Tier);
            Assert.Equal(0, item.OcrPages);
            Assert.Contains("CSC 201", item.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_does_not_treat_lowercase_prose_as_a_course_code()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maths {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path,
                "EIE 301 Signals and Systems",
                "the answer is 180 and the gain is 20");

            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.True(item.IsMove);
            Assert.Equal(0, run.Stats.OcrFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_checks_the_pages_of_a_pq_named_with_one_course()
    {
        var path = Path.Combine(Path.GetTempPath(), $"EIE 301 PQ {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path,
                "EIE 301 Signals and Systems",
                "MTH 101 Elementary Mathematics I");

            var sorter = new Sorter(new[]
            {
                "EIE 301 Signals and Systems",
                "MTH 101 Elementary Mathematics I",
            });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.False(item.IsMove);
            Assert.Equal("multi", item.Tier);
            Assert.Equal(1, run.Stats.OpenedFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sorter_moves_a_single_course_pq_named_with_one_course()
    {
        var path = Path.Combine(Path.GetTempPath(), $"EIE 301 PQ {Guid.NewGuid():N}.pdf");

        try
        {
            CreatePdf(path,
                "EIE 301 Signals and Systems",
                "EIE 301 past questions");

            var sorter = new Sorter(new[] { "EIE 301 Signals and Systems" });
            var run = sorter.Run(new[] { path });

            var item = Assert.Single(run.Items);
            Assert.True(item.IsMove);
            Assert.Equal(0, run.Stats.OcrFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CreatePdf(string path, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(40, 750), font);
        }

        File.WriteAllBytes(path, builder.Build());
    }
}
