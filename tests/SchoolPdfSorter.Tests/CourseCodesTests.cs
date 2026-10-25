using SchoolPdfSorter.Engine;
using Xunit;

namespace SchoolPdfSorter.Tests;

public class CourseCodesTests
{
    [Theory]
    [InlineData("MTH 101 Elementary Mathematics I", "mth101")]
    [InlineData("MEE3016 Thermodynamics", "mee3016")]
    [InlineData("cs61a", "cs61a")]
    [InlineData("ECE-301 Signals and Systems", "ece301")]
    [InlineData("GST.102 Communication", "gst102")]
    public void Extracts_a_code_from_a_course_label(string label, string expected)
        => Assert.Equal(expected, CourseCodes.ExtractCode(label));

    [Fact]
    public void Course_without_a_code_returns_null()
        => Assert.Null(CourseCodes.ExtractCode("Thermodynamics II"));

    [Theory]
    [InlineData("econ301_week6.pdf", true)]
    [InlineData("ECON 301 - Week 6.pdf", true)]
    [InlineData("econ-301", true)]
    [InlineData("ECON3010", false)]
    [InlineData("macroeconomics notes.pdf", false)]
    public void Matches_whole_code_tokens_only(string haystack, bool expected)
    {
        var course = CourseCodes.TryCreate("ECON 301 Microeconomics")!;
        Assert.Equal(expected, course.Pattern.IsMatch(haystack));
    }

    [Fact]
    public void Finds_every_course_in_a_shared_page()
    {
        var courses = new[]
        {
            CourseCodes.TryCreate("MTH 101 Elementary Mathematics I")!,
            CourseCodes.TryCreate("PHY 105 General Physics")!,
            CourseCodes.TryCreate("CSC 201 Data Structures")!,
        };

        var hits = CourseCodes.Find("Timetable: MTH 101, PHY 105 and CSC 201", courses);

        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Matches_across_a_line_break()
    {
        var course = CourseCodes.TryCreate("MEE 3016 Thermodynamics")!;
        Assert.Matches(course.Pattern, "Course:\nMEE\n3016");
    }

    [Fact]
    public void Finds_an_unlisted_code_that_shares_a_listed_prefix()
    {
        var courses = new[]
        {
            CourseCodes.TryCreate("EIE 301 Signals and Systems")!,
            CourseCodes.TryCreate("EIE 305 Control Systems")!,
            CourseCodes.TryCreate("MTH 101 Elementary Mathematics I")!,
        };

        var hits = CourseCodes.FindUnlisted(
            "EIE 315 past questions; MTH 101 notes; PHY 105 lab", courses);

        Assert.Equal(new[] { "EIE 315" }, hits);
    }

    [Fact]
    public void Does_not_flag_listed_codes_or_unknown_prefixes()
    {
        var courses = new[]
        {
            CourseCodes.TryCreate("EIE 301 Signals and Systems")!,
            CourseCodes.TryCreate("MTH 101 Elementary Mathematics I")!,
        };

        var hits = CourseCodes.FindUnlisted(
            "EIE 301 and MTH 101; IEEE 802.11; ISO 9001; PHY 105", courses);

        Assert.Empty(hits);
    }

    [Fact]
    public void Does_not_treat_a_four_digit_year_as_a_sibling_course()
    {
        var courses = new[] { CourseCodes.TryCreate("EIE 301 Signals and Systems")! };

        var hits = CourseCodes.FindUnlisted("EIE 2024 timetable and EIE 315 notes", courses);

        Assert.Equal(new[] { "EIE 315" }, hits);
    }

    [Fact]
    public void Unlisted_code_match_allows_separators_and_reports_once()
    {
        var courses = new[] { CourseCodes.TryCreate("EIE 301 Signals and Systems")! };

        var hits = CourseCodes.FindUnlisted("eie-315 / EIE 315 / EIE.315", courses);

        Assert.Equal(new[] { "EIE 315" }, hits);
    }

    [Fact]
    public void Finds_a_different_department_code_when_unknown_prefixes_are_allowed()
    {
        var courses = new[]
        {
            CourseCodes.TryCreate("EIE 301 Signals and Systems")!,
            CourseCodes.TryCreate("MTH 101 Elementary Mathematics I")!,
        };

        var hits = CourseCodes.FindUnlisted(
            "CSC 201 Data Structures", courses, includeUnknownPrefixes: true);

        Assert.Equal(new[] { "CSC 201" }, hits);
    }

    [Fact]
    public void Unknown_prefixes_are_ignored_by_default()
    {
        var courses = new[] { CourseCodes.TryCreate("EIE 301 Signals and Systems")! };

        Assert.Empty(CourseCodes.FindUnlisted("CSC 201 Data Structures", courses));
    }

    [Fact]
    public void Unknown_prefix_scan_ignores_page_furniture()
    {
        var courses = new[] { CourseCodes.TryCreate("EIE 301 Signals and Systems")! };

        var hits = CourseCodes.FindUnlisted(
            "Page 12; Table 3; Scan 0007; Img 1234; CH 65; Book 17; Form 08; Sep 17; WA 0010; " +
            "ISO 9001; ISBN 978; Office 365; CSC 201 notes",
            courses, includeUnknownPrefixes: true);

        Assert.Equal(new[] { "CSC 201" }, hits);
    }

    [Fact]
    public void Unknown_prefix_scan_ignores_room_and_lab_numbers()
    {
        var courses = new[] { CourseCodes.TryCreate("EIE 301 Signals and Systems")! };

        var hits = CourseCodes.FindUnlisted(
            "Room 301; Hall 204; Lab 12; Gate 07; Seat 45; Ref 123; CSC 201 notes",
            courses, includeUnknownPrefixes: true);

        Assert.Equal(new[] { "CSC 201" }, hits);
    }

    [Fact]
    public void Prose_scan_ignores_lowercase_short_number_and_session_noise()
    {
        var courses = new[] { CourseCodes.TryCreate("EIE 301 Signals and Systems")! };

        var hits = CourseCodes.FindUnlisted(
            "the answer is 180; AB 80; II 25; SC 2425; CSC 201 notes",
            courses, includeUnknownPrefixes: true, requireUpperUnknown: true);

        Assert.Equal(new[] { "CSC 201" }, hits);
    }

    [Fact]
    public void Prose_scan_keeps_a_two_letter_code_with_a_suffix()
    {
        var courses = new[] { CourseCodes.TryCreate("EIE 301 Signals and Systems")! };

        var hits = CourseCodes.FindUnlisted(
            "CS 61A notes", courses, includeUnknownPrefixes: true,
            requireUpperUnknown: true);

        Assert.Equal(new[] { "CS 61A" }, hits);
    }

    // ================================================== codes read out of file names

    [Theory]
    [InlineData("GST311 History and Philosophy", new[] { "GST 311" })]
    [InlineData("GEC 410 past questions", new[] { "GEC 410" })]
    [InlineData("EIE 326A lecture note", new[] { "EIE 326A" })]
    [InlineData("MEE-3016 thermodynamics", new[] { "MEE 3016" })]
    [InlineData("2 GEC324 Module 2 (2025)", new[] { "GEC 324" })]
    [InlineData("Lecture 101 the human person", new string[0])]
    [InlineData("Page 12 of 40", new string[0])]
    [InlineData("Microsoft Office 2013 in WORD", new string[0])]
    [InlineData("session 2425 handbook", new string[0])]
    [InlineData("2024 timetable", new string[0])]
    [InlineData("chapter 3 of the notes", new string[0])]
    [InlineData("normal notes for monday", new string[0])]
    [InlineData("EIE 311 and GST 111 merged", new[] { "EIE 311", "GST 111" })]
    public void FindCourseLikeCodes_reads_courses_without_trusting_furniture(
        string text, string[] expected)
        => Assert.Equal(expected, CourseCodes.FindCourseLikeCodes(text));

    [Fact]
    public void FindCourseLikeCodes_reports_a_code_once_per_text()
        => Assert.Single(CourseCodes.FindCourseLikeCodes("GST311 GST 311 gst-311"));
}
