using System.Text.RegularExpressions;

namespace SchoolPdfSorter.Engine;

/// <summary>A high-confidence, suggestion-only guess for a file the strict rule left alone.</summary>
public sealed record CourseSuggestion(string Course, string Code, double Score, string Why);

/// <summary>
/// Suggests a course for a file that strict matching left alone, but only when the evidence is
/// very strong. Suggestions never move a file: they are shown beside the untouched files so a
/// weird or misspelled name is easy to spot without guessing.
///
/// The score is intentionally blunt and conservative:
///   - the file name has no code, and every meaningful word in it appears in the course label
///     (prefix / one-letter-typo tolerant), or
///   - the code is a known OCR/name misspelling (EIE 3O1, ElE 301, EIE 3I5), or
///   - the same title evidence appears in the PDF title / biggest header line.
/// It also requires a clear margin over the runner-up, so two similar courses produce no suggestion.
/// </summary>
public static class CourseSuggester
{
    /// <summary>Only suggestions at or above this score are shown.</summary>
    public const double MinScore = 0.92;

    /// <summary>The best course must beat the runner-up by at least this much.</summary>
    public const double MinMargin = 0.08;

    // Codes split by spaces, dots, dashes, etc. Prefix and digits may contain the letters that
    // OCR commonly confuses with digits (O/0, I/1/l, S/5, B/8, Z/2, G/6), so "EIE 3O1" and
    // "ElE 301" are still recognised as EIE 301.
    private static readonly Regex FuzzyCodePattern = new(
        @"(?<![A-Za-z0-9])([A-Za-z0-9]{2,6})[\s\-_./]*([0-9OoIlZzSsBbGg]{2,5})([A-Za-z]?)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WordPattern = new(
        @"[A-Za-z]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Filler that says how a file is used, not which course it belongs to.
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "file", "copy", "download", "downloaded", "telegram", "whatsapp",
        "scan", "scanned", "document", "school", "course", "outline", "syllabus",
        "solution", "solutions", "answer", "answers", "past", "question", "questions",
        "paper", "papers", "exam", "exams", "test", "tests", "quiz", "quizzes",
        "assignment", "assignments", "note", "notes", "lecture", "lectures", "slide", "slides",
        "material", "materials", "final", "midterm", "mid", "semester", "first", "second", "third",
        "week", "chapter", "part", "unit", "module", "book", "handout", "handouts",
        "level", "lvl", "st", "nd", "rd", "th", "alpha", "omega", "rain", "harmattan",
        "summary", "revision", "study", "practice", "tutorial", "lab", "project", "report",
        "introduction", "intro", "introductory", "principles", "fundamentals", "fundamental",
        "advanced", "applied", "basic", "general", "elementary", "essential", "essentials",
        "modern", "topics", "selected", "special",
        "the", "and", "of", "in", "for", "to", "a", "an", "on", "with", "by", "from",
        "at", "or", "n", "amp",
        "i", "ii", "iii", "iv", "v", "vi",
    };

    public static CourseSuggestion? Suggest(FileSignals file, IReadOnlyList<CourseCode> courses)
    {
        if (courses.Count == 0)
            return null;

        var prefixes = Prefixes(courses);
        var nameTokens = Tokens(file.FileName, prefixes);
        var nameCodes = ExtractCodeTokens(file.FileName, prefixes);

        // A code in the name means the title words cannot decide on their own: "EIE 315 signals
        // and systems" must not be suggested for EIE 301 just because the title matches.
        bool nameHasCode = nameCodes.Count > 0;

        var otherText = string.Join("\n", new[] { file.MetadataTitle, file.BiggestLine });
        var otherTokens = Tokens(otherText, prefixes);
        bool otherHasCode = ExtractCodeTokens(otherText, prefixes).Count > 0;

        var scored = new List<(CourseCode Course, double Score, string Why)>();

        foreach (var course in courses)
        {
            var labelTokens = Tokens(course.Pattern.Replace(course.Course, " "), prefixes);

            double best = 0;
            string why = "";

            if (!nameHasCode)
            {
                var score = TitleScore(nameTokens, labelTokens);
                if (score > best)
                {
                    best = score;
                    why = "the file name matches its course title";
                }
            }

            if (!otherHasCode)
            {
                var score = TitleScore(otherTokens, labelTokens);
                if (score > best)
                {
                    best = score;
                    why = "the document title matches its course title";
                }
            }

            // A near-miss code is the strongest signal, but only when normalising OCR-style
            // confusions makes it exactly this course's code. A different real code never counts.
            var codeScore = CodeScore(nameCodes, course, out var codeWhy);
            if (codeScore == 0)
                codeScore = CodeScore(ExtractCodeTokens(otherText, prefixes), course, out codeWhy);

            if (codeScore > best)
            {
                best = codeScore;
                why = codeWhy;
            }

            if (best > 0)
                scored.Add((course, best, why));
        }

        if (scored.Count == 0)
            return null;

        var ordered = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Course.Code, StringComparer.Ordinal)
            .ToList();

        var top = ordered[0];
        if (top.Score < MinScore)
            return null;

        if (ordered.Count > 1 && top.Score - ordered[1].Score < MinMargin)
            return null;

        return new CourseSuggestion(top.Course.Course, top.Course.Code, top.Score, top.Why);
    }

    private static double TitleScore(IReadOnlyList<string> nameTokens, IReadOnlyList<string> labelTokens)
    {
        if (nameTokens.Count == 0 || labelTokens.Count == 0)
            return 0;

        var name = nameTokens.Distinct(StringComparer.Ordinal).ToList();
        var label = labelTokens.Distinct(StringComparer.Ordinal).ToList();

        // Every meaningful word in the file must point at this course. One unmatched word is
        // enough to walk away; this is what keeps "signals and communication" from matching
        // a Signals and Systems course.
        foreach (var token in name)
        {
            if (!label.Any(l => TokenMatches(token, l)))
                return 0;
        }

        int matchedLabels = label.Count(l => name.Any(n => TokenMatches(n, l)));
        double labelCoverage = (double)matchedLabels / label.Count;

        // All name words match, and at least a good part of the course title is covered.
        return 0.8 + 0.2 * labelCoverage;
    }

    private static double CodeScore(IReadOnlyList<string> codes, CourseCode course, out string why)
    {
        why = "";
        foreach (var code in codes)
        {
            if (!string.Equals(code, course.Code, StringComparison.Ordinal))
                continue;

            why = $"its code looks like a misspelling of {course.Code.ToUpperInvariant()}";
            return 0.99;
        }

        return 0;
    }

    /// <summary>Two words match when equal, share most of the shorter word (maths/mathematics,
    /// signal/signals), or are one edit apart at length 6+ (thermodynamcs/thermodynamics).
    /// The "most of the shorter word" rule keeps computer/communication apart.</summary>
    private static bool TokenMatches(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;

        if (a.Length < 4 || b.Length < 4)
            return false;

        int common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common])
            common++;

        int shorter = Math.Min(a.Length, b.Length);
        if (common >= 4 && common >= shorter * 0.75)
            return true;

        return a.Length >= 6 && b.Length >= 6 &&
               Math.Abs(a.Length - b.Length) <= 1 &&
               Levenshtein(a, b) <= 1;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static List<string> Tokens(string? text, ISet<string> prefixes)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        foreach (Match m in WordPattern.Matches(text))
        {
            var token = m.Value.ToLowerInvariant();
            if (token.Length < 2 || Noise.Contains(token) || prefixes.Contains(token))
                continue;

            tokens.Add(token);
        }

        return tokens;
    }

    private static HashSet<string> Prefixes(IReadOnlyList<CourseCode> courses)
        => new(
            courses.Select(c => new string(c.Code.TakeWhile(char.IsLetter).ToArray()))
                   .Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    private static List<string> ExtractCodeTokens(string? text, ISet<string> prefixes)
    {
        var codes = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return codes;

        foreach (Match m in FuzzyCodePattern.Matches(text))
        {
            var prefix = new string(m.Groups[1].Value.Select(NormalizeLetter).ToArray());
            if (!prefixes.Contains(prefix))
                continue;

            codes.Add(NormalizeCode(m));
        }

        return codes;
    }

    private static string NormalizeCode(Match m)
    {
        var prefix = new string(m.Groups[1].Value.Select(NormalizeLetter).ToArray());
        var digits = new string(m.Groups[2].Value.Select(NormalizeDigit).ToArray());
        var suffix = new string(m.Groups[3].Value.Select(NormalizeLetter).ToArray());
        return (prefix + digits + suffix).ToLowerInvariant();
    }

    // Letters that OCR swaps around. For a letter position, 1/I/l all mean I; for a digit
    // position, O means 0, I/l mean 1, and so on.
    private static char NormalizeLetter(char c) => char.ToLowerInvariant(c) switch
    {
        '1' or 'i' or 'l' => 'i',
        '0' or 'o' => 'o',
        '5' or 's' => 's',
        '8' or 'b' => 'b',
        '2' or 'z' => 'z',
        '6' or 'g' => 'g',
        _ => char.ToLowerInvariant(c),
    };

    private static char NormalizeDigit(char c) => char.ToLowerInvariant(c) switch
    {
        'o' => '0',
        'i' or 'l' => '1',
        'z' => '2',
        's' => '5',
        'b' => '8',
        'g' => '6',
        _ => c,
    };
}
