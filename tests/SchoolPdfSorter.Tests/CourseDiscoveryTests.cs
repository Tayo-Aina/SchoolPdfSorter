using SchoolPdfSorter.Engine;
using Xunit;

namespace SchoolPdfSorter.Tests;

public class CourseDiscoveryTests
{
    [Fact]
    public void Code_used_by_the_most_files_comes_first()
    {
        var found = CourseDiscovery.FromFileNames(new[]
        {
            @"C:\school\MTH 101 notes.pdf",
            @"C:\school\EIE 326 week 1.pdf",
            @"C:\school\EIE 326 week 2.pptx",
            @"C:\school\EIE 326 past questions.ppsx",
        });

        Assert.Equal(new[] { "EIE 326", "MTH 101" }, found);
    }

    [Fact]
    public void Different_spellings_of_a_code_merge_into_one_course()
    {
        var found = CourseDiscovery.FromFileNames(new[]
        {
            "EIE326 notes.pdf",
            "EIE 326 slides.pptx",
            "eie-326 past questions.ppsx",
        });

        Assert.Equal(new[] { "EIE 326" }, found);
    }

    [Fact]
    public void Two_codes_in_one_file_both_appear_and_each_count_once()
    {
        var found = CourseDiscovery.FromFileNames(new[]
        {
            "EIE 326 and MTH 101 merged.pdf",
            "EIE 326 week 1.pdf",
        });

        Assert.Equal(new[] { "EIE 326", "MTH 101" }, found);
    }

    [Fact]
    public void Years_sessions_page_furniture_and_lecture_numbers_are_ignored()
    {
        var found = CourseDiscovery.FromFileNames(new[]
        {
            "EIE 2024 timetable.pdf",
            "SC 2425 session handbook.pdf",
            "Page 12 of 40.pdf",
            "Lecture 101 the human person.pdf",
            "Room 301 booking.pptx",
            "EIE 326 notes.pdf",
        });

        Assert.Equal(new[] { "EIE 326" }, found);
    }

    [Fact]
    public void Personal_documents_do_not_add_a_course_code()
    {
        var found = CourseDiscovery.FromFileNames(new[]
        {
            "Dara 400 siwes.pdf",
            "Tayo 300 level result.pdf",
            "EIE 326 notes.pdf",
        });

        Assert.Equal(new[] { "EIE 326" }, found);
    }

    [Fact]
    public void No_codes_means_an_empty_list()
    {
        Assert.Empty(CourseDiscovery.FromFileNames(new[]
        {
            "lecture notes.pdf",
            "2024 timetable.pptx",
            "page 1.pdf",
        }));
    }

    [Fact]
    public void Codes_with_the_same_count_are_ordered_by_code()
    {
        var found = CourseDiscovery.FromFileNames(new[] { "MTH 101.pdf", "EIE 326.pdf" });

        Assert.Equal(new[] { "EIE 326", "MTH 101" }, found);
    }

    [Fact]
    public void FromFiles_finds_a_code_on_the_first_slide_when_the_name_has_none()
    {
        var root = NewDirectory();

        try
        {
            var path = PptxFixture.Create(root, "highway lecture.pptx", "Highway Engineering",
                "CVE 435 Highway Engineering");

            var found = CourseDiscovery.FromFiles(new[] { path });

            Assert.Equal(new[] { "CVE 435" }, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FromFiles_skips_a_personal_name_even_when_its_slides_name_a_course()
    {
        var root = NewDirectory();

        try
        {
            var personal = PptxFixture.Create(root, "Dara 400 siwes.pptx", "SIWES Report",
                "CVE 435 Highway Engineering");
            var real = PptxFixture.Create(root, "EIE 326 notes.pptx", "Data Structures",
                "Week 1");

            var found = CourseDiscovery.FromFiles(new[] { personal, real });

            Assert.Equal(new[] { "EIE 326" }, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FromFiles_merges_a_misspelled_code_into_the_one_used_by_the_file_names()
    {
        var root = NewDirectory();

        try
        {
            var real = PptxFixture.Create(root, "GEC 321 notes.pptx", "Sources of Finance", "");
            var finance = PptxFixture.Create(root, "finance.pptx", "Finance",
                "CEG 321 Sources of Finance");

            var found = CourseDiscovery.FromFiles(new[] { real, finance });

            Assert.Contains("GEC 321", found);
            Assert.DoesNotContain("CEG 321", found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "SchoolPdfSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
