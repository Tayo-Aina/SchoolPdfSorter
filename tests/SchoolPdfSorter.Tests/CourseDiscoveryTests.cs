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
}
