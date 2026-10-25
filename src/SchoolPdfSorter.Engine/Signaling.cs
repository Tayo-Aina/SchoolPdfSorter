using System.Text.RegularExpressions;

namespace SchoolPdfSorter.Engine;

/// <summary>Everything cheap we can read from a PDF without OCR.</summary>
public sealed record FileSignals(
    string Path,
    string FileName,
    string ParentFolder,
    string MetadataTitle,
    int PageCount,
    string BiggestLine,
    double BiggestLinePointSize,
    IReadOnlyList<string> FirstPages,
    bool HasTextLayer)
{
    /// <summary>Text layer of the first pages (up to <see cref="PdfText.MaxPages"/>).</summary>
    public string FirstPagesText => string.Join("\n", FirstPages);

    public bool HasAnyText => FirstPages.Any(p => p.Length >= 40);
}

public static class Signals
{
    /// <summary>A path-only signal for files already settled by their name or folder.</summary>
    public static FileSignals Light(string path)
    {
        var fileName = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
        return new FileSignals(path, fileName, parent, "", 0, "", 0, Array.Empty<string>(), false);
    }

    public static FileSignals Extract(string path)
    {
        PdfTextInfo text;
        try
        {
            text = PdfText.Extract(path);
        }
        catch
        {
            text = new PdfTextInfo(0, Array.Empty<string>(), "", 0, "");
        }

        var fileName = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";

        return new FileSignals(path, fileName, parent, text.MetadataTitle, text.PageCount,
            text.BiggestLineText, text.BiggestLinePointSize, text.PageTexts, text.TextChars >= 40);
    }

    public static FileSignals ExtractSafe(string path)
    {
        try
        {
            return Extract(path);
        }
        catch
        {
            return Light(path);
        }
    }
}

/// <summary>One course from the user's list, with the code matcher built from its label.</summary>
public sealed record CourseCode(string Course, string Code, Regex Pattern);

/// <summary>
/// Course-code extraction and matching. A code is any letter prefix plus digits, with an optional
/// trailing letter (MTH 101, MEE3016, CS61A, ece-301). Matching allows spaces, dots, dashes and
/// underscores between the letters and digits so PDFs and OCR can split them either way.
/// </summary>
public static class CourseCodes
{
    private static readonly Regex ExtractPattern = new(
        @"(?<![A-Za-z0-9])([A-Za-z]{2,6})[\s\-_./]*(\d{2,5})([A-Za-z]?)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Normalize "MTH 101" / "mth-101" / "MTH101" -> "mth101".</summary>
    public static string Normalize(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Extract a normalized course code like "mth101" from a course label.</summary>
    public static string? ExtractCode(string courseLabel) => TryCreate(courseLabel)?.Code;

    /// <summary>Build the course + its matcher from a label. Null when the label has no code.</summary>
    public static CourseCode? TryCreate(string courseLabel)
    {
        var m = ExtractPattern.Match(courseLabel);
        if (!m.Success)
            return null;

        string letters = m.Groups[1].Value;
        string digits = m.Groups[2].Value;
        string suffix = m.Groups[3].Value;
        string code = (letters + digits + suffix).ToLowerInvariant();

        string pattern =
            $@"(?<![A-Za-z0-9]){Regex.Escape(letters)}[\s\-_./]*{Regex.Escape(digits)}" +
            $@"{Regex.Escape(suffix)}(?![A-Za-z0-9])";

        return new CourseCode(courseLabel, code,
            new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant));
    }

    /// <summary>Every listed course whose code appears in the text; each course once.</summary>
    public static List<CourseCode> Find(string haystack, IReadOnlyList<CourseCode> courses)
    {
        var hits = new List<CourseCode>();
        if (string.IsNullOrEmpty(haystack))
            return hits;

        foreach (var course in courses)
        {
            if (course.Pattern.IsMatch(haystack))
                hits.Add(course);
        }

        return hits;
    }

    /// <summary>
    /// Codes that are not in the user's course list. With <paramref name="includeUnknownPrefixes"/>
    /// false, only codes sharing a prefix with a listed course are reported (EIE 315 when EIE 301 is
    /// listed). With true, a code from any department is reported (CSC 201 when only EIE courses are
    /// listed), which is what lets a timetable or a multi-course past-questions pack be recognized
    /// as multi-course. Set <paramref name="requireUpperUnknown"/> for prose/page text so that
    /// lowercase phrases like "the answer is 180" are not mistaken for course IS 180. Common page
    /// furniture (Page 12, Table 3, Room 301, ISO 9001, ISBN 978, ...) is ignored.
    /// </summary>
    public static List<string> FindUnlisted(string haystack, IReadOnlyList<CourseCode> courses,
        bool includeUnknownPrefixes = false, bool requireUpperUnknown = false)
    {
        var hits = new List<string>();
        if (string.IsNullOrEmpty(haystack) || courses.Count == 0)
            return hits;

        var listed = new HashSet<string>(courses.Select(c => c.Code), StringComparer.OrdinalIgnoreCase);
        var prefixes = new HashSet<string>(
            courses.Select(c => new string(c.Code.TakeWhile(char.IsLetter).ToArray()))
                   .Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (prefixes.Count == 0)
            return hits;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ExtractPattern.Matches(haystack))
        {
            var prefix = m.Groups[1].Value;
            var digits = m.Groups[2].Value;
            var suffix = m.Groups[3].Value;

            bool knownPrefix = prefixes.Contains(prefix);
            if (!knownPrefix && !includeUnknownPrefixes)
                continue;

            // "EIE 2024" in a header is almost always a year, not a sibling course.
            // Ignoring it only costs OCR time; it never causes a wrong move.
            if (digits.Length == 4 && int.TryParse(digits, out var year) && year is >= 1900 and <= 2099)
                continue;

            // "SC 2425" is the 2024/2025 session, not a course.
            if (!knownPrefix && LooksLikeAcademicSession(digits))
                continue;

            var code = (prefix + digits + suffix).ToLowerInvariant();
            if (listed.Contains(code) || !seen.Add(code))
                continue;

            if (!knownPrefix)
            {
                // Unknown prefixes are riskier. Keep them short and course-like, and
                // reject words that commonly precede numbers in ordinary documents.
                if (prefix.Length > 4 || digits.Length > 4 || NonCoursePrefixes.Contains(prefix))
                    continue;

                // In prose, "the answer is 180" must not look like course IS 180.
                // Real course codes in timetables and question papers are normally uppercase.
                if (requireUpperUnknown && prefix != prefix.ToUpperInvariant())
                    continue;

                // Two-letter + two-digit OCR fragments such as "AB 80" or "II 25" are
                // usually grades, answer options, or noise. A suffix (CS 61A) is kept.
                if (requireUpperUnknown && prefix.Length < 3 && digits.Length < 3 &&
                    suffix.Length == 0)
                    continue;
            }

            hits.Add($"{prefix.ToUpperInvariant()} {digits}{suffix.ToUpperInvariant()}");
        }

        return hits;
    }

    /// <summary>"2425" and "2526" are academic sessions, not four-digit course numbers.</summary>
    private static bool LooksLikeAcademicSession(string digits)
    {
        if (digits.Length != 4 || !int.TryParse(digits, out _))
            return false;

        int first = int.Parse(digits[..2]);
        int second = int.Parse(digits[2..]);
        return first is >= 20 and <= 40 && second == first + 1;
    }

    /// <summary>Words that look like a code prefix but are normally page furniture or product names.</summary>
    private static readonly HashSet<string> NonCoursePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pages", "pg", "fig", "figs", "figure", "table", "tables", "section",
        "sections", "sect", "unit", "units", "chapter", "chap", "part", "parts", "week",
        "weeks", "level", "module", "modules", "volume", "vol", "no", "number", "isbn",
        "issn", "doi", "iso", "ieee", "ansi", "who", "covid", "sars", "html", "css",
        "php", "sql", "api", "usb", "hdmi", "pdf", "doc", "docs", "word", "excel",
        "office", "windows", "android", "iphone", "gb", "mb", "kb", "tb", "mhz", "ghz",
        "kg", "cm", "mm", "km", "ms", "sec", "min", "question", "questions", "ans",
        "answer", "exercise", "ex", "example", "lecture", "lec", "note", "notes", "slide",
        "slides", "course", "code", "test", "exam", "exams", "quiz", "assignment",
        "semester", "sem", "session", "group", "scan", "scans", "scanned", "img", "imgs",
        "pic", "pics", "photo", "photos", "dsc", "file", "files", "filename", "attachment",
        "download", "downloads", "screenshot", "screenshots", "ch", "book", "books", "form",
        "forms", "sep", "sept", "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug",
        "oct", "nov", "dec", "wa", "to", "room", "hall", "gate", "seat", "lab", "labs",
        "lt", "aud", "team", "case", "slip", "pin", "otp", "id", "num", "sn", "tel", "ext",
        "ref", "reg", "eq", "ii", "iii", "iv", "vi", "vii", "viii", "ix", "xi", "xii",
        "xiii", "xiv", "xv", "xvi", "xvii", "xviii", "xix", "xx", "xxi", "xxii", "xxiii",
        "xxiv", "xxv",
    };

    /// <summary>
    /// True for a word that merely looks like a course prefix but is page furniture, a
    /// product name or a column heading ("Office 2013", "Page 12", "ISO 9001", "Room 301").
    /// Shared with the course-structure reader, which must not turn those into courses.
    /// </summary>
    public static bool IsFurniturePrefix(string prefix) => NonCoursePrefixes.Contains(prefix);

    /// <summary>True for "2019", "2025" and the like: a year, never a course number.</summary>
    public static bool IsYearNumber(string digits)
        => digits.Length == 4 && int.TryParse(digits, out var year) && year is >= 1900 and <= 2099;

    /// <summary>
    /// Every course-like code in a piece of text, written the way a course list writes them
    /// ("GST 311", "EIE 326A"). Used to spot courses the user's files name but their list
    /// leaves out, so years ("2024"), sessions ("2425"), page furniture ("Page 12") and
    /// product names ("Office 2013") are skipped rather than offered as courses.
    /// </summary>
    public static List<string> FindCourseLikeCodes(string text)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(text))
            return found;

        foreach (Match m in ExtractPattern.Matches(text))
        {
            var prefix = m.Groups[1].Value;
            var digits = m.Groups[2].Value;
            var suffix = m.Groups[3].Value;

            if (prefix.Length > 6 || digits.Length > 4 || NonCoursePrefixes.Contains(prefix))
                continue;

            // A course number is at least three digits: "of 40", "in 90" and "room 12" are
            // not courses, and a two-digit offer would only clutter the choice.
            if (digits.Length < 3)
                continue;

            if (IsYearNumber(digits) || LooksLikeAcademicSession(digits))
                continue;

            var code = $"{prefix.ToUpperInvariant()} {digits}{suffix.ToUpperInvariant()}";
            if (seen.Add(code))
                found.Add(code);
        }

        return found;
    }

    /// <summary>Legacy helper kept for the dormant embedding planner.</summary>
    public static bool ContainsCode(string haystack, string normalizedCode)
        => Normalize(haystack).Contains(normalizedCode, StringComparison.Ordinal);
}
