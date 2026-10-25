using System.Text;
using System.Text.RegularExpressions;

namespace SchoolPdfSorter.Engine;

/// <summary>One course pulled out of a pasted course structure.</summary>
/// <param name="Code">Normalized code, e.g. "eie326".</param>
/// <param name="Label">Ready-to-use course label, e.g. "EIE 326 Software Development Techniques".</param>
/// <param name="Repaired">True when the code's department letters had to be corrected.</param>
public sealed record ParsedCourse(string Code, string Label, bool Repaired);

/// <summary>A course the parser left alone but suspects the scan misread, and why.</summary>
public sealed record StructureNote(string Label, string Why);

/// <summary>What a structure dump produced, plus the readings worth a second look.</summary>
public sealed record CourseStructureResult(
    IReadOnlyList<ParsedCourse> Courses,
    IReadOnlyList<ParsedCourse> Repaired,
    IReadOnlyList<StructureNote> Suspects);

/// <summary>
/// Turns a course structure into a course list: a portal page copy, a printed table, an
/// exported spreadsheet, or the OCR of any of those. Such a dump is a stream of table cells —
/// code and title in one cell or split across several, with unit numbers, type letters,
/// department names, "TOTAL" rows and page furniture in between.
///
/// Two readings are accepted. A code that opens a cell is a course row and is always taken
/// (that covers a copied table). A code sitting inside a cell is taken only when the document
/// spells that department cleanly at least twice — that covers rows where every column ended
/// up on one line ("Course Code: EIE 311, Electromagnetic Fields, 3, C"), while words that
/// merely look like codes ("Page 12", "Office 2013", "Level 100") stay out of the list.
///
/// Course codes are what matter. A title only decorates the folder name, so a title that is
/// cut short or never found is harmless — and a department is only corrected when the reading
/// could not be a real one ("ElE314" is EIE 314), never by guessing between two plausible
/// spellings ("CST" stays CST even though "CIT" is also listed).
/// </summary>
public static class CourseStructure
{
    /// <summary>
    /// A course code: two to six letters, an optional separator, two to five digits and an
    /// optional trailing letter ("EIE326", "EIE 326", "eie-326", "MTH 101A", "CS 61A").
    /// </summary>
    private static readonly Regex CodePattern = new(
        @"(?<![A-Za-z0-9])([A-Za-z]{2,6})[\s\-–—_:./]*(\d{2,5})([A-Za-z]?)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A row number or bullet in front of a code: "1.", "12)", "(1)", "- ", "* ", "• ".</summary>
    private static readonly Regex Decoration = new(
        @"^(?:\d{1,3}[.)\]]|\(\d{1,3}\)|[-–—*•·]+)[\s:]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RowPattern = new(
        @"<tr[^>]*>(.*?)</tr>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex CellPattern = new(
        @"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex TagPattern = new(
        @"<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The units column, alone or merged with other columns: "3", "1.", "2 C", "2 1", "009".</summary>
    private static readonly Regex NumberCell = new(
        @"^\d{1,4}(?:[.,]\d)?\.?(?:\s+\d{1,4}(?:[.,]\d)?\.?)?(?:\s+[A-Za-z]{1,3})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A labelled single-column cell: "Unit: 3", "Type: C", "Cr. Unit 3", "Code EIE 311".</summary>
    private static readonly Regex LabelledColumn = new(
        @"^(?:unit|units|type|cr\.?\s*unit|course\s*code|code|title|level)\s*[.:]?\s*(?:\d{1,4}|[A-Za-z]{1,3})\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A leading "Title:" / "Course Title –" label in front of a real title.</summary>
    private static readonly Regex LeadLabel = new(
        @"^(?:[A-Za-z][A-Za-z .']{0,15}[:;–-]\s*)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Row-number column headers: "Sl. No", "S/N", "Lgo", "No.".</summary>
    private static readonly Regex RowNumberHeader = new(
        @"^(?:s\.?l\.?|sr\.?|s/n|sn|lgo|no\.?|n\.?)\s*\.?(?:no\.?|number)?\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PageNumber = new(
        @"^\d{1,3}\s*/\s*\d{1,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DateLine = new(
        @"^\d{1,2}[/-]\d{1,2}[/-]\d{2,4}([ ,].*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A standalone units number inside a merged cell.</summary>
    private static readonly Regex UnitsNumber = new(
        @"(?<![A-Za-z0-9])\d{1,2}(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whole-cell header words that are never part of a title.</summary>
    private static readonly HashSet<string> HeaderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "course", "code", "title", "unit", "units", "type", "level", "semester",
        "prerequisite", "corequisite", "print", "sr", "no", "no.", "#"
    };

    /// <summary>Course "type" columns: Compulsory, University, Elective, SIWES.</summary>
    private static readonly HashSet<string> TypeLetters = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "u", "v", "e", "s", "i", "ii", "iii", "iv", "vi", "vii", "viii", "ix", "x"
    };

    /// <summary>How many cells after a bare code cell may belong to its title.</summary>
    private const int TitleLookahead = 4;

    private const int MaxTitleLength = 90;

    public static IReadOnlyList<ParsedCourse> Parse(string? text) => ParseDetailed(text).Courses;

    private static readonly CourseStructureResult Empty =
        new(Array.Empty<ParsedCourse>(), Array.Empty<ParsedCourse>(), Array.Empty<StructureNote>());

    /// <summary>
    /// Every course found in the dump, ordered by course number, duplicates merged, plus the
    /// readings that were corrected or that look like a scan misread of another department.
    /// Empty when the text holds no course codes at all.
    /// </summary>
    public static CourseStructureResult ParseDetailed(string? text)
    {
        var cells = Cellize(text);
        if (cells.Count == 0)
            return Empty;

        var candidates = Scan(cells);
        if (candidates.Count == 0)
            return Empty;

        // Departments this document spelled cleanly, and how often. Repairs and the codes that
        // sit inside a cell are judged against that vocabulary, never against a fixed list.
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<Row>();

        foreach (var code in candidates)
        {
            if (code.Votes)
                votes[code.Letters.ToUpperInvariant()] =
                    votes.GetValueOrDefault(code.Letters.ToUpperInvariant()) + 1;
        }

        foreach (var code in candidates)
        {
            // Either the cell opens with the code (a course row in any layout) or the code sits
            // in a cell that still carries its units column (a row whose columns were merged).
            if (!code.AtStart && !code.Units)
                continue;

            var (prefix, repaired) = ResolvePrefix(code.Letters, code.Digits, votes);
            rows.Add(new Row(code, prefix, repaired));
        }

        if (rows.Count == 0)
            return Empty;

        var vocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            vocabulary.Add(row.Prefix);

        var byCode = new Dictionary<string, ParsedCourse>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var title = TitleOf(row, cells, vocabulary);
            var label = BuildLabel(row.Prefix, row.Code.Number, row.Code.Suffix, title);
            if (label.Length == 0)
                continue;

            var course = new ParsedCourse(
                (row.Prefix + row.Code.Number + row.Code.Suffix).ToLowerInvariant(), label, row.Repaired);

            if (!byCode.TryGetValue(course.Code, out var existing))
            {
                byCode[course.Code] = course;
                continue;
            }

            var better = Choose(existing, course);
            if (!ReferenceEquals(better, existing))
                byCode[course.Code] = better;
        }

        var courses = byCode.Values
            .OrderBy(course => Number(course.Label))
            .ThenBy(course => Letters(course.Code), StringComparer.Ordinal)
            .ThenBy(course => course.Code, StringComparer.Ordinal)
            .ToList();

        return new CourseStructureResult(
            courses,
            courses.Where(c => c.Repaired).ToList(),
            FindSuspects(courses, votes));
    }

    /// <summary>
    /// True when the pasted text reads as a structure rather than a hand-typed course list:
    /// several course rows, and the columns a structure table has (units, types, totals) —
    /// a typed list has neither, so it keeps its exact wording.
    /// </summary>
    public static bool LooksLikeStructure(string? text)
    {
        var cells = Cellize(text);
        if (cells.Count == 0)
            return false;

        var rows = 0;
        var columns = 0;

        foreach (var cell in cells)
        {
            if (CodeAtStartOf(cell) is not null)
                rows++;
            else if (NumberCell.IsMatch(cell) || TypeLetters.Contains(cell) || TotalRow.IsMatch(cell))
                columns++;
        }

        if (rows < 4)
            return false;

        var markers = cells.Count(cell =>
            TotalRow.IsMatch(cell) ||
            cell.Contains("structure", StringComparison.OrdinalIgnoreCase) ||
            cell.Contains("semester", StringComparison.OrdinalIgnoreCase) ||
            cell.Contains("prerequisite", StringComparison.OrdinalIgnoreCase) ||
            cell.Contains("course code", StringComparison.OrdinalIgnoreCase) ||
            HeaderWords.Contains(cell));

        return markers > 0 || (columns > 0 && rows >= 8);
    }

    // ================================================================ internals

    /// <summary>
    /// One code occurrence found in the dump. <paramref name="Votes"/> marks the readings
    /// trusted to name the document's departments; <paramref name="AtStart"/> marks a code
    /// that opens its cell, which is a course row in any table layout;
    /// <paramref name="Units"/> says the row still carries its own units column.
    /// </summary>
    private sealed record CodeHit(int Cell, int Start, int Length, string Text,
        string Letters, string Digits, string Suffix, bool AtStart, bool Units, bool Votes)
    {
        /// <summary>True when a two-letter tag swallowed the first digit of the number ("GE0211").</summary>
        public bool AbsorbedZero => Digits.Length == 4 && Digits[0] == '0' && Letters.Length <= 2;

        /// <summary>The course number as it should be written, without that absorbed zero.</summary>
        public string Number => AbsorbedZero ? Digits[1..] : Digits;

        /// <summary>Whatever follows the code in the same cell: usually the course title.</summary>
        public string SameCell => Collapse(Text[Math.Min(Start + Length, Text.Length)..]);
    }

    /// <summary>A course row: an accepted code and the department prefix it resolved to.</summary>
    private sealed record Row(CodeHit Code, string Prefix, bool Repaired);

    /// <summary>
    /// The title of a course: the rest of its cell, or, when the code sits alone there, the
    /// next few cells until the units column or the next course. A title is only ever taken
    /// from the cells that follow the code — the cells before it are the department column,
    /// and putting those in the label would name folders after the wrong thing.
    /// </summary>
    private static string TitleOf(Row row, List<string> cells, HashSet<string> vocabulary)
    {
        var rest = row.Code.SameCell;

        if (rest.Length >= 3 && !IsStopCell(rest))
            return TrimTitle(rest, vocabulary);

        var sb = new StringBuilder();

        for (var next = row.Code.Cell + 1; next < cells.Count && next <= row.Code.Cell + TitleLookahead; next++)
        {
            var candidate = cells[next];

            // The next course row, the units column, or a type letter: the title ended.
            if (IsStopCell(candidate) || CodeAtStartOf(candidate) is not null ||
                HasRealCode(candidate, vocabulary))
                break;

            if (sb.Length > 0)
                sb.Append(' ');

            sb.Append(candidate);

            if (sb.Length >= MaxTitleLength)
                break;
        }

        return TrimTitle(sb.ToString(), vocabulary);
    }

    /// <summary>A cell carrying a course code of a department this document actually uses.</summary>
    private static bool HasRealCode(string cell, HashSet<string> vocabulary)
    {
        foreach (Match m in CodePattern.Matches(cell))
        {
            if (m.Index == 0)
                continue;

            var letters = m.Groups[1].Value;

            if (!vocabulary.Contains(MapScanConfusions(letters)) ||
                CourseCodes.IsFurniturePrefix(letters) ||
                CourseCodes.IsYearNumber(m.Groups[2].Value))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>Every plausible code occurrence in every cell, in reading order.</summary>
    private static List<CodeHit> Scan(List<string> cells)
    {
        var hits = new List<CodeHit>();

        for (var i = 0; i < cells.Count; i++)
        {
            var text = StripDecoration(cells[i]);

            if (TotalRow.IsMatch(text))
                continue;

            foreach (Match m in CodePattern.Matches(text))
            {
                var letters = m.Groups[1].Value;
                var digits = m.Groups[2].Value;
                var atStart = m.Index == 0;

                if (CourseCodes.IsFurniturePrefix(letters))
                    continue;

                // "Office 2013" inside a title is a product year, not a course. A number that
                // opens the cell is kept even in the year range: some schools number courses
                // that way ("CSC 2018"), and a heading never opens with letters plus a number.
                if (!atStart && CourseCodes.IsYearNumber(digits))
                    continue;

                var units = HasUnitsColumn(text[(m.Index + m.Length)..]);

                // A code in the middle of a cell is only a course row when the cell still looks
                // like one: it carries the row's own columns ("Industrial CHM111 General Physical
                // Chemistry 3 C Chemistry" — a copied table with the department glued in front of
                // the code) or it carries the labels of a per-course line ("Course Code: EIE 311,
                // Title: Signals and Systems, Unit: 3"). Capital letters, three or four digits and
                // no furniture word ("Office 2013", "Page 12") are required either way, so prose
                // and headers stay out of the course list.
                if (!atStart && (!IsPlausible(letters) || digits.Length is < 3 or > 4 ||
                                 !(units || IsLabelledRow(text))))
                    continue;

                hits.Add(new CodeHit(i, m.Index, m.Length, text, letters, digits, m.Groups[3].Value,
                    atStart, units || IsLabelledRow(text), IsConfident(letters, digits)));
            }
        }

        return hits;
    }

    /// <summary>The units column of a course row: a small standalone number (" 3 C", " 18 18").</summary>
    private static bool HasUnitsColumn(string rest)
        => UnitsNumber.IsMatch(rest);

    /// <summary>
    /// True when the cell is one labelled column of a per-course line ("Code: EIE 311",
    /// "Title: Signals and Systems", "Course Code: EIE 311") rather than a sentence that
    /// merely mentions a code. A stray label only ever re-states a course the table already
    /// lists, because a code in such a cell must still be capitals plus three or four digits.
    /// </summary>
    private static bool IsLabelledRow(string text)
        => LabelMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] LabelMarkers =
    {
        "code", "title", "unit", "type", "course", "prerequisite"
    };

    /// <summary>The code a cell starts with — a course row in any table layout — or null.</summary>
    private static Match? CodeAtStartOf(string cell)
    {
        var text = StripDecoration(cell);
        var m = CodePattern.Match(text);

        if (!m.Success || m.Index != 0 || CourseCodes.IsFurniturePrefix(m.Groups[1].Value))
            return null;

        return m;
    }

    /// <summary>Drops a row number or bullet so that "1. EIE 311 Algebra" reads as a course row.</summary>
    private static string StripDecoration(string cell)
    {
        var m = Decoration.Match(cell);
        return m.Success ? cell[m.Length..].TrimStart() : cell;
    }

    /// <summary>Splits a dump into table cells, in reading order.</summary>
    private static List<string> Cellize(string? text)
    {
        var cells = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return cells;

        if (text.Contains("<td", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<th", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<tr", StringComparison.OrdinalIgnoreCase))
        {
            // OCR markdown keeps the table as HTML rows; every cell becomes one entry.
            foreach (Match row in RowPattern.Matches(text))
                foreach (Match cell in CellPattern.Matches(row.Groups[1].Value))
                    AddCell(cells, CleanHtml(cell.Groups[1].Value));

            if (cells.Count > 0)
                return cells;
        }

        // Plain text: one cell per line, per semicolon (the chat box turns pasted line breaks
        // into semicolons), per tab or comma (a copied table or exported sheet), or per pipe.
        foreach (var part in text.Split(new[] { '\n', '\r', ';', '\t', '|', ',' },
                                        StringSplitOptions.RemoveEmptyEntries))
            AddCell(cells, part);

        return cells;
    }

    private static void AddCell(List<string> cells, string raw)
    {
        var cell = Collapse(raw);

        // A cell with no letters and no digits is a table border or a rule: "---", "::".
        if (cell.Length == 0 || !cell.Any(char.IsLetterOrDigit) || IsNoise(cell))
            return;

        cells.Add(cell);
    }

    private static string TrimTitle(string rest, HashSet<string> vocabulary)
    {
        // "Title: Electromagnetic Fields" is the same cell as "Electromagnetic Fields".
        rest = LeadLabel.Replace(rest, "");

        // A title stops where the next course's code begins. "Microsoft Office 2013" and
        // "AutoCAD 2017" are not courses, so only a department this document uses can cut it.
        foreach (Match m in CodePattern.Matches(rest))
        {
            if (m.Index == 0)
                return "";

            var letters = m.Groups[1].Value;
            if (!vocabulary.Contains(MapScanConfusions(letters)) ||
                CourseCodes.IsYearNumber(m.Groups[2].Value) ||
                CourseCodes.IsFurniturePrefix(letters))
                continue;

            rest = rest[..m.Index];
            break;
        }

        // A standalone small number in the middle of the text is the units column that ended
        // up inside the same cell.
        var units = UnitsNumber.Match(rest);
        if (units.Success && units.Index > 0)
            rest = rest[..units.Index];

        return Collapse(rest.Trim(' ', '-', ',', '.', ':', '|', '/', '(', ')', '–', '—', '’', '\''));
    }

    private static string BuildLabel(string prefix, string digits, string suffix, string title)
    {
        var sb = new StringBuilder(prefix.ToUpperInvariant());
        sb.Append(' ').Append(digits);

        if (suffix.Length > 0)
            sb.Append(suffix.ToUpperInvariant());

        if (title.Length > 0)
            sb.Append(' ').Append(title);

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Corrects a department the scan could not have read correctly: a lowercase or mixed-case
    /// reading ("ElE314"), a lowercase l standing where a capital I belongs, or a digit that ate
    /// the tag's third letter ("py111" is PHY 111, "GE0220" is GEC 220).
    ///
    /// A reading that is spelled like a real department is always left alone, even when the
    /// document mentions it only once: "CST 111" is never folded into "CIT 111", and a one-off
    /// "GRC 219" is reported for the user to check instead of being guessed at.
    /// </summary>
    private static (string Prefix, bool Repaired) ResolvePrefix(string letters, string digits,
        Dictionary<string, int> votes)
    {
        var raw = letters.ToUpperInvariant();

        if (votes.Count == 0)
            return (raw, false);

        var absorbed = digits.Length == 4 && digits[0] == '0' && letters.Length <= 2;

        if (IsPlausible(letters) && !absorbed)
            return (raw, false);

        var mapped = MapScanConfusions(letters);

        foreach (var candidate in new[] { raw, mapped, absorbed ? raw + "O" : null })
        {
            if (candidate is not null && votes.ContainsKey(candidate))
                return (candidate, candidate != raw);
        }

        foreach (var candidate in new[] { mapped, absorbed ? mapped + "O" : null })
        {
            if (candidate is not null && Nearest(candidate, votes) is { } nearest)
                return (nearest, true);
        }

        return (raw, false);
    }

    /// <summary>The one department this scan reading can only have meant, or null if ambiguous.</summary>
    private static string? Nearest(string prefix, Dictionary<string, int> votes)
    {
        string? best = null;

        foreach (var known in votes.Keys)
        {
            if (Distance(prefix, known) != 1)
                continue;

            // Two candidates one letter apart: the reading is too damaged to choose between them.
            if (best is not null)
                return null;

            best = known;
        }

        return best;
    }

    /// <summary>Could this be a department's real tag, spelled properly?</summary>
    private static bool IsPlausible(string letters)
        => letters.All(char.IsLetter) && letters == letters.ToUpperInvariant();

    /// <summary>
    /// Upper-cases a reading and puts a lowercase l back where a capital I belongs. An
    /// uppercase L is left alone: only a lowercase one is a scan artefact of the letter I.
    /// </summary>
    private static string MapScanConfusions(string letters)
        => new(letters.Select(c => c == 'l' ? 'I' : char.ToUpperInvariant(c)).ToArray());

    /// <summary>A reading trusted enough to define the document's departments: capitals and a normal number.</summary>
    private static bool IsConfident(string letters, string digits)
        => letters.Length >= 3 && IsPlausible(letters) &&
           (digits.Length == 3 || (digits.Length == 4 && !CourseCodes.IsYearNumber(digits)));

    private static bool IsStopCell(string cell)
        => cell.Length == 0 ||
           NumberCell.IsMatch(cell) ||
           TypeLetters.Contains(cell) ||
           HeaderWords.Contains(cell) ||
           LabelledColumn.IsMatch(cell) ||
           RowNumberHeader.IsMatch(cell) ||
           PageNumber.IsMatch(cell) ||
           TotalRow.IsMatch(cell) ||
           cell.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoise(string cell)
    {
        var lower = cell.ToLowerInvariant();

        if (DateLine.IsMatch(cell))
            return true;

        if (lower.Contains("course structure", StringComparison.Ordinal) ||
            lower.Contains("covenant portal", StringComparison.Ordinal))
            return true;

        // Column headings, and the browser's own furniture, never carry a course.
        if (!CodePattern.IsMatch(cell) &&
            (lower.Contains("prerequisite", StringComparison.Ordinal) ||
             lower.Contains("home program", StringComparison.Ordinal) ||
             lower.Contains("homeprogram", StringComparison.Ordinal) ||
             lower.Contains("graduation", StringComparison.Ordinal) ||
             lower.Contains("requirement", StringComparison.Ordinal) ||
             lower.Contains("www.", StringComparison.Ordinal)))
            return true;

        return false;
    }

    /// <summary>
    /// Codes the user should glance at before approving, in two flavours:
    /// a department tag this document never spells cleanly anywhere else ("NE 414"), which is
    /// what a misread column looks like, and a tag that is plausible but appears only once or
    /// twice while a near-identical one appears all over ("GRC 219" beside 17 clean "GEC").
    /// Nothing is changed on their account — they are reported, never guessed at.
    /// </summary>
    private static List<StructureNote> FindSuspects(List<ParsedCourse> courses, Dictionary<string, int> votes)
    {
        var result = new List<StructureNote>();

        foreach (var course in courses)
        {
            var prefix = Letters(course.Code).ToUpperInvariant();
            var seen = votes.GetValueOrDefault(prefix, 0);

            // The document never spelled this tag in a clean course row anywhere: either it is
            // a real one-off department, or it is a misread. The user decides.
            if (seen == 0)
            {
                var closest = votes.Keys
                    .Select(known => (known, distance: Distance(prefix, known)))
                    .Where(pair => pair.distance <= 2)
                    .OrderBy(pair => pair.distance)
                    .ThenByDescending(pair => votes[pair.known])
                    .Take(2)
                    .Select(pair => $"{pair.known} ({votes[pair.known]})")
                    .ToList();

                result.Add(new StructureNote(course.Label, closest.Count == 0
                    ? $"the tag {prefix} appears in no clean course row of this document"
                    : $"the tag {prefix} appears in no clean course row — closest here: " +
                      string.Join(", ", closest)));
                continue;
            }

            if (seen > 2 || prefix.Length != 3)
                continue;

            var neighbour = votes.Keys
                .Where(known => known.Length == prefix.Length &&
                                Distance(prefix, known) == 1 &&
                                votes[known] >= Math.Max(4, seen * 4))
                .OrderByDescending(known => votes[known])
                .FirstOrDefault();

            if (neighbour is null)
                continue;

            result.Add(new StructureNote(course.Label,
                $"{neighbour} is spelled that way {votes[neighbour]} times in this document"));
        }

        return result;
    }

    /// <summary>The trustworthy reading of a code, and the fuller title of the two.</summary>
    private static ParsedCourse Choose(ParsedCourse existing, ParsedCourse fresh)
    {
        if (existing.Repaired && !fresh.Repaired)
            return TitleLength(fresh.Label) >= TitleLength(existing.Label) ? fresh : existing;

        if (!existing.Repaired && fresh.Repaired)
            return TitleLength(fresh.Label) > TitleLength(existing.Label) * 2 ? fresh : existing;

        return TitleLength(fresh.Label) > TitleLength(existing.Label) ? fresh : existing;
    }

    private static int TitleLength(string label)
    {
        var m = CodePattern.Match(label);
        return m.Success ? label.Length - (m.Index + m.Length) : label.Length;
    }

    private static int Number(string label)
    {
        var m = CodePattern.Match(label);
        return m.Success && int.TryParse(m.Groups[2].Value, out var n) ? n : int.MaxValue;
    }

    private static string Letters(string code) => new(code.TakeWhile(char.IsLetter).ToArray());

    private static string CleanHtml(string cell)
        => System.Net.WebUtility.HtmlDecode(TagPattern.Replace(cell, " "));

    private static string Collapse(string text)
        => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>
    /// A totals row: "Compulsory TOTAL", "NUC TOTAL", "GRAND TOTAL 24 24". A course titled
    /// "Total Man Concept I" is not a totals row, so this is a shape, not a substring test.
    /// </summary>
    private static readonly Regex TotalRow = new(
        @"^(?:compulsory|nuc|university|siwes|elective|grand|semester|level)?\s*total(?:\s+\d{1,3}){0,3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Levenshtein distance, small strings only.</summary>
    private static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
