using SchoolPdfSorter.Engine;
using Xunit;

namespace SchoolPdfSorter.Tests;

public class CourseSuggesterTests
{
    private static FileSignals File(string fileName, string title = "", string biggestLine = "",
        params string[] pages)
        => new($@"C:\tmp\{fileName}", fileName, "tmp", title, 1, biggestLine, 12,
            pages, pages.Length > 0);

    private static CourseCode[] Courses(params string[] labels)
        => labels.Select(l => CourseCodes.TryCreate(l)!).ToArray();

    [Fact]
    public void Suggests_a_course_from_a_weird_filename()
    {
        var suggestion = CourseSuggester.Suggest(
            File("signals and systems past questions.pdf"),
            Courses("EIE 301 Signals and Systems", "MTH 101 Elementary Mathematics I"));

        Assert.NotNull(suggestion);
        Assert.Equal("eie301", suggestion.Code);
        Assert.True(suggestion.Score >= CourseSuggester.MinScore);
    }

    [Fact]
    public void Suggests_from_a_misspelled_code()
    {
        var suggestion = CourseSuggester.Suggest(
            File("EIE 3O1 signals and systems.pdf"),
            Courses("EIE 301 Signals and Systems"));

        Assert.NotNull(suggestion);
        Assert.Equal("eie301", suggestion.Code);
    }

    [Fact]
    public void Suggests_from_a_confused_letter_in_the_prefix()
    {
        var suggestion = CourseSuggester.Suggest(
            File("ElE 301 signals and systems.pdf"),
            Courses("EIE 301 Signals and Systems"));

        Assert.NotNull(suggestion);
        Assert.Equal("eie301", suggestion.Code);
    }

    [Fact]
    public void Suggests_from_a_misspelled_course_title()
    {
        var suggestion = CourseSuggester.Suggest(
            File("thermodynamcs.pdf"),
            Courses("MEE 3016 Thermodynamics"));

        Assert.NotNull(suggestion);
        Assert.Equal("mee3016", suggestion.Code);
    }

    [Fact]
    public void Suggests_from_a_short_form_of_the_title()
    {
        var suggestion = CourseSuggester.Suggest(
            File("maths past questions.pdf"),
            Courses("MTH 101 Elementary Mathematics I"));

        Assert.NotNull(suggestion);
        Assert.Equal("mth101", suggestion.Code);
    }

    [Fact]
    public void A_shared_word_start_is_not_a_match()
    {
        var suggestion = CourseSuggester.Suggest(
            File("computer networks.pdf"),
            Courses("GST 102 Communication in English"));

        Assert.Null(suggestion);
    }

    [Fact]
    public void A_different_code_blocks_a_title_only_suggestion()
    {
        var suggestion = CourseSuggester.Suggest(
            File("EIE 315 signals and systems.pdf"),
            Courses("EIE 301 Signals and Systems"));

        Assert.Null(suggestion);
    }

    [Fact]
    public void Two_courses_with_the_same_title_produce_no_suggestion()
    {
        var suggestion = CourseSuggester.Suggest(
            File("signals and systems.pdf"),
            Courses("EIE 301 Signals and Systems", "EIE 305 Signals and Systems II"));

        Assert.Null(suggestion);
    }

    [Fact]
    public void A_single_generic_word_is_not_enough()
    {
        var suggestion = CourseSuggester.Suggest(
            File("signals.pdf"),
            Courses("EIE 301 Signals and Systems"));

        Assert.Null(suggestion);
    }

    [Fact]
    public void Unrelated_names_produce_no_suggestion()
    {
        var suggestion = CourseSuggester.Suggest(
            File("random weekend notes.pdf"),
            Courses("EIE 301 Signals and Systems"));

        Assert.Null(suggestion);
    }
}
