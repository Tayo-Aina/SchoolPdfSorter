using SchoolPdfSorter.Engine;

namespace SchoolPdfSorter.Tests;

public sealed class CourseStructureTests
{
    // ------------------------------------------------------------ a clean dump

    private const string PortalDump = """
        Course structure for B.Eng. Information and Communication Engineering:- for 100 Level In Alpha Semester
        Course Code Title Unit Type Prerequisitehomeprogram
        Industrial CHM111 General Physical Chemistry 3 C Chemistry
        CHM119 General Chemistry Practical I 1 C Industrial Chemistry
        Microsoft Office Specialist on Computer CIT111 Microsoft Office 2013 in 0 C Science WORD
        GEC117 Technical Drawing I 1 C Mechanical Engineering
        MAT111 Algebra 3 C Industrial Mathematics
        PHY112 Heat, Sound and Optics 3 C Industrial Physics
        Compulsory TOTAL 18 18
        GST111 Communication in English I 2 U English
        NUC TOTAL 4 4
        TMC111 Total Man Concept I 1 V ALDC
        GRAND TOTAL 24 24
        https://cuportal.covenantuniversity.edu.ng/administration.php 1/8
        """;

    [Fact]
    public void Parse_reads_courses_out_of_a_portal_dump()
    {
        var courses = CourseStructure.Parse(PortalDump);

        Assert.Equal(
            new[]
            {
                "CHM 111", "CIT 111", "GST 111", "MAT 111", "TMC 111",
                "PHY 112", "GEC 117", "CHM 119"
            },
            courses.Select(c => CodePart(c.Label)).ToArray());
    }

    [Fact]
    public void Parse_keeps_the_course_titles()
    {
        var courses = CourseStructure.Parse(PortalDump).ToDictionary(c => c.Code);

        // The department column glued in front of the code is not part of the title,
        // and the units column glued behind it is not either.
        Assert.Equal("CHM 111 General Physical Chemistry", courses["chm111"].Label);
        Assert.Equal("GST 111 Communication in English I", courses["gst111"].Label);
        Assert.Equal("TMC 111 Total Man Concept I", courses["tmc111"].Label);
    }

    [Fact]
    public void Parse_does_not_let_a_year_look_like_a_second_course()
    {
        var courses = CourseStructure.Parse(PortalDump);
        var course = Assert.Single(courses, c => c.Code == "cit111");

        Assert.Equal("CIT 111 Microsoft Office 2013 in", course.Label);
        Assert.DoesNotContain(courses, c => c.Code is "office2013" or "specialist2013");
    }

    [Fact]
    public void Parse_ignores_totals_urls_and_page_furniture()
    {
        Assert.DoesNotContain(CourseStructure.Parse(PortalDump), c => c.Label.Contains("TOTAL"));
        Assert.DoesNotContain(CourseStructure.Parse(PortalDump), c => c.Label.Contains("cuportal"));
        Assert.DoesNotContain(CourseStructure.Parse(PortalDump), c => c.Label.Contains("1/8"));
    }

    [Fact]
    public void Parse_orders_courses_by_course_number()
    {
        var numbers = CourseStructure.Parse(PortalDump)
            .Select(c => int.Parse(CodePart(c.Label).Split(' ')[1]))
            .ToList();

        Assert.Equal(numbers.OrderBy(n => n), numbers);
    }

    [Fact]
    public void LooksLikeStructure_separates_a_dump_from_a_hand_typed_list()
    {
        Assert.True(CourseStructure.LooksLikeStructure(PortalDump));
        Assert.False(CourseStructure.LooksLikeStructure("ECON 301 Microeconomics; GST 102 Communication in English"));
    }

    [Fact]
    public void Parse_produces_labels_the_sorter_can_use()
    {
        var labels = CourseStructure.Parse(PortalDump).Select(c => c.Label).ToList();
        var sorter = new Sorter(labels);

        Assert.Equal(labels.Count, sorter.Codes.Count);
        Assert.Empty(sorter.CoursesWithoutCode);
    }

    // ------------------------------------------------------------------ merges

    [Fact]
    public void Parse_merges_a_code_that_appears_twice_and_keeps_the_better_title()
    {
        var dump = """
            Course structure for 100 Level In Alpha Semester
            EIE326 Data Structures
            Course structure for 200 Level In Omega Semester
            EIE326 Software Development Techniques
            """;

        var course = Assert.Single(CourseStructure.Parse(dump), c => c.Code == "eie326");
        Assert.Equal("EIE 326 Software Development Techniques", course.Label);
    }

    // ----------------------------------------------------------------- repairs

    [Fact]
    public void Parse_fixes_a_department_the_scan_read_in_lowercase()
    {
        var dump = """
            Course structure for 300 Level In Alpha Semester
            EIE311 Electromagnetic Fields and Waves
            EIE312 Communication Principles
            ele314 Electric Circuit Theory
            """;

        var result = CourseStructure.ParseDetailed(dump);
        var course = Assert.Single(result.Courses, c => c.Code == "eie314");

        Assert.True(course.Repaired);
        Assert.Equal("EIE 314 Electric Circuit Theory", course.Label);
        Assert.Contains(course, result.Repaired);
    }

    [Fact]
    public void Parse_fixes_a_department_that_lost_a_letter_to_the_number_column()
    {
        var dump = """
            Course structure for 200 Level In Alpha Semester
            GEC210 Engineering Mathematics I
            GEC212 Engineering Graphics
            GE0211 Engineering Mechanics
            """;

        var course = Assert.Single(CourseStructure.Parse(dump), c => c.Code == "gec211");
        Assert.Equal("GEC 211 Engineering Mechanics", course.Label);
    }

    [Fact]
    public void Parse_fixes_a_department_that_lost_its_first_letter_entirely()
    {
        var dump = """
            Course structure for 100 Level In Alpha Semester
            PHY112 Heat, Sound and Optics
            PHY119 Physics Practical I
            py111 Mechanics and Properties of Matter
            """;

        var course = Assert.Single(CourseStructure.Parse(dump), c => c.Code == "phy111");
        Assert.Equal("PHY 111 Mechanics and Properties of Matter", course.Label);
    }

    [Fact]
    public void Parse_never_guesses_between_two_real_departments()
    {
        // CIT and CST are both real, both plausible: CST must stay CST.
        var dump = """
            Course structure for 100 Level In Alpha Semester
            CIT111 Computer Fundamentals
            CIT121 Office Applications
            CIT141 Networking
            CIT151 Programming
            CST111 Use of Library and Study Skills
            """;

        var courses = CourseStructure.Parse(dump).ToDictionary(c => c.Code);

        Assert.Equal("CST 111 Use of Library and Study Skills", courses["cst111"].Label);
        Assert.False(courses["cst111"].Repaired);
    }

    [Fact]
    public void Parse_reports_a_plausible_department_it_refused_to_change()
    {
        var dump = """
            Course structure for 200 Level In Alpha Semester
            GEC210 Engineering Mathematics I
            GEC212 Engineering Graphics
            GEC214 Applied Mechanics
            GEC218 Engineering Laboratory
            GRC219 Advanced Physics Mechanics
            """;

        var result = CourseStructure.ParseDetailed(dump);
        var course = Assert.Single(result.Courses, c => c.Code == "grc219");

        Assert.False(course.Repaired);
        var note = Assert.Single(result.Suspects);
        Assert.Equal("GRC 219 Advanced Physics Mechanics", note.Label);
        Assert.Contains("GEC", note.Why);
    }

    [Fact]
    public void Parse_leaves_an_ambiguous_scan_reading_alone()
    {
        // "GEX" is one letter from both GEC and GET, the two departments this document spells
        // cleanly. Nothing is chosen between them: the reading is kept exactly as it was.
        var dump = """
            Course structure for 200 Level In Alpha Semester
            GEC210 Engineering Mathematics I
            GET212 History of Technology
            gex214 Engineering Mechanics
            """;

        var course = Assert.Single(CourseStructure.Parse(dump), c => c.Code == "gex214");

        Assert.False(course.Repaired);
        Assert.Equal("GEX 214 Engineering Mechanics", course.Label);
    }

    // -------------------------------------------------- other ways to write a table

    [Fact]
    public void Parse_reads_a_tab_separated_copy()
    {
        var dump = "Course Code\tTitle\tUnit\tType\r\n" +
                   "EIE311\tElectromagnetic Fields & Waves\t3\tC\r\n" +
                   "EIE312\tCommunication Principles\t3\tC\r\n" +
                   "GST101\tUse of English\t2\tU\r\n";

        var courses = CourseStructure.Parse(dump);

        Assert.Equal(
            new[] { "GST 101 Use of English", "EIE 311 Electromagnetic Fields & Waves", "EIE 312 Communication Principles" },
            courses.Select(c => c.Label).ToArray());
    }

    [Fact]
    public void Parse_reads_a_spreadsheet_export()
    {
        var csv = """
            Level,Code,Title,Unit,Type
            100,EIE311,Electromagnetic Fields and Waves,3,C
            100,EIE312,Communication Principles,3,C
            200,GEC210,Engineering Mathematics I,3,C
            """;

        var courses = CourseStructure.Parse(csv);

        // Ordered by course number, which is the order the levels come in.
        Assert.Equal(new[] { "gec210", "eie311", "eie312" }, courses.Select(c => c.Code).ToArray());
        Assert.Equal("EIE 311 Electromagnetic Fields and Waves", courses[1].Label);
    }

    [Fact]
    public void Parse_reads_a_pipe_table_whose_borders_are_not_courses()
    {
        var markdown = """
            | Course Code | Title | Unit | Type |
            |---|---|---|---|
            | EIE311 | Electromagnetic Fields & Waves | 3 | C |
            | EIE312 | Communication Principles | 3 | C |
            """;

        var courses = CourseStructure.Parse(markdown);

        Assert.Equal(new[] { "eie311", "eie312" }, courses.Select(c => c.Code).ToArray());
        Assert.Equal("EIE 311 Electromagnetic Fields & Waves", courses[0].Label);
    }

    [Fact]
    public void Parse_reads_a_row_whose_columns_all_landed_on_one_line()
    {
        // A digital PDF's text layer often gives the whole table row as one line.
        var dump = """
            Course Code Title Unit Type Programme
            EIE 311 Electromagnetic Fields & Waves 3 C Electrical and Electronics Engineering
            EIE 322 Signals and Systems 2 C Information and Communication Engineering
            Compulsory TOTAL 22 22
            """;

        var courses = CourseStructure.Parse(dump);

        Assert.Equal("EIE 311 Electromagnetic Fields & Waves", courses[0].Label);
        Assert.Equal("EIE 322 Signals and Systems", courses[1].Label);
    }

    [Fact]
    public void Parse_reads_a_line_per_course_with_labelled_fields()
    {
        var dump = """
            Course structure for 300 Level In Alpha Semester
            Course Code: EIE 311, Title: Electromagnetic Fields and Waves, Unit: 3, Type: C
            Course Code: EIE 312, Title: Communication Principles, Unit: 3, Type: C
            """;

        var courses = CourseStructure.Parse(dump);

        Assert.Equal(new[] { "eie311", "eie312" }, courses.Select(c => c.Code).ToArray());
        Assert.Equal("EIE 311 Electromagnetic Fields and Waves", courses[0].Label);
    }

    [Fact]
    public void Parse_reads_a_table_whose_title_comes_before_the_code()
    {
        var dump = "Electromagnetic Fields & Waves\tEIE311\t3\tC\r\n" +
                   "Signals and Systems\tEIE322\t2\tC\r\n";

        var courses = CourseStructure.Parse(dump);

        Assert.Equal(new[] { "eie311", "eie322" }, courses.Select(c => c.Code).ToArray());

        // The title sits in front of the code, in the home-program column's usual place, so it
        // is not guessed at: the folder name is only the code.
        Assert.Equal("EIE 311", courses[0].Label);
    }

    [Fact]
    public void Parse_reads_a_numbered_or_bulleted_list_of_courses()
    {
        var dump = """
            Course structure for 100 Level In Alpha Semester
            1. EIE311 Electromagnetic Fields & Waves
            2) EIE312 Communication Principles
            * EIE313 Physical Electronics
            - EIE314 Electric Circuit Theory
            """;

        Assert.Equal(
            new[] { "eie311", "eie312", "eie313", "eie314" },
            CourseStructure.Parse(dump).Select(c => c.Code).ToArray());
    }

    [Fact]
    public void Parse_keeps_a_four_digit_course_number()
    {
        var dump = """
            Course structure for 300 Level In Alpha Semester
            CSC 3011 Data Structures 3 C Computer Science
            GST 1001 Use of English 2 U Arts
            CHM 2019 Organic Chemistry 3 C Chemistry
            """;

        var codes = CourseStructure.Parse(dump).Select(c => c.Code).ToArray();

        Assert.Contains("csc3011", codes);
        Assert.Contains("gst1001", codes);

        // A four-digit number that opens a row is still a course, even inside the year range.
        Assert.Contains("chm2019", codes);
    }

    [Fact]
    public void Parse_never_turns_page_furniture_into_a_course()
    {
        var dump = """
            Course structure for 100 Level In Alpha Semester
            EIE311 Signals and Systems 3 C
            Page 12 of 40
            Office 2013 and Word 2016 3 C
            Level 200 Unit 12
            Table 3 Room 101
            """;

        var codes = CourseStructure.Parse(dump).Select(c => c.Code).ToArray();

        Assert.Equal(new[] { "eie311" }, codes);
    }

    [Fact]
    public void Parse_of_plain_text_finds_nothing()
    {
        Assert.Empty(CourseStructure.Parse("no codes here at all"));
        Assert.Empty(CourseStructure.Parse(""));
        Assert.Empty(CourseStructure.Parse(null));
    }

    // -------------------------------------------------------------- html tables

    [Fact]
    public void Parse_reads_ocr_markdown_tables()
    {
        var markdown = """
            Course structure for 100 Level In Alpha Semester
            <table><tr><td>Course Code</td><td>Title</td><td>Unit</td><td>Type</td></tr>
            <tr><td>CHM111 General Physical Chemistry</td><td>3</td><td>C</td><td>Industrial Chemistry</td></tr>
            <tr><td>MAT111 Mathematics I</td><td>3</td><td>C</td><td>Mathematics</td></tr>
            <tr><td>Total</td><td>6</td><td>6</td><td></td></tr></table>
            """;

        var courses = CourseStructure.Parse(markdown);

        Assert.Equal(2, courses.Count);
        Assert.Equal("CHM 111 General Physical Chemistry", courses[0].Label);
        Assert.Equal("MAT 111 Mathematics I", courses[1].Label);
    }

    // ------------------------------------------------------------- bare codes

    [Fact]
    public void Parse_picks_up_a_code_written_without_its_title()
    {
        var dump = """
            Course structure for 400 Level In Alpha Semester
            EIE411
            3
            C
            Computer Organization and Architecture
            EIE412 Control Systems 3 C Electronics Engineering
            """;

        var courses = CourseStructure.Parse(dump);

        Assert.Contains("EIE 411", courses.Select(c => CodePart(c.Label)));
        Assert.Equal("EIE 412 Control Systems", courses.Single(c => c.Code == "eie412").Label);
    }

    /// <summary>"EIE 326 Software Development Techniques" -> "EIE 326".</summary>
    private static string CodePart(string label)
    {
        var parts = label.Split(' ', 3);
        return parts.Length >= 2 ? string.Join(' ', parts[..2]) : label;
    }
}
