using System.IO;
using System.Text.RegularExpressions;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// Builds a course list from the names of the user's files, so step 2 can be filled in
/// without typing. Only file names are read here; no document is opened. A code counts
/// once per file, and codes used by more files come first so the likeliest courses lead
/// the list. Labels are code-only ("EIE 326") because a file name rarely has the full
/// course title. Personal documents such as SIWES reports are skipped even when a name
/// and a level number happen to look like a course code.
/// </summary>
public static class CourseDiscovery
{
    private static readonly Regex PersonalDocument = new(
        @"\b(siwes|swep|internship|industrial[ _-]?training|curriculum[ _-]?vitae|resume|cv|" +
        @"letter|certificate|receipt|clearance|transcript|admission|invoice|payment|birth|" +
        @"origin|medical|fitness|testimonial|statement[ _-]?of[ _-]?purpose|logbook|defence|defense)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LevelMark = new(
        @"(?<![A-Za-z0-9])(100|200|300|400|500|600)\s*(lvl|level)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Course code labels found in <paramref name="filePaths"/>, ordered by how many files
    /// mention each code and then by the code itself.
    /// </summary>
    public static List<string> FromFileNames(IEnumerable<string> filePaths)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (filePaths is null)
            return new List<string>();

        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var name = Path.GetFileNameWithoutExtension(path);

            if (string.IsNullOrWhiteSpace(name) || LooksPersonal(name))
                continue;

            foreach (var code in CourseCodes.FindCourseLikeCodes(name)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                counts[code] = counts.GetValueOrDefault(code) + 1;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .ToList();
    }

    /// <summary>
    /// True for a name/label that is about a person or an administrative document, not a
    /// course. A level marker such as "300 level" also counts: it is not a course code.
    /// </summary>
    public static bool LooksPersonal(string name)
        => PersonalDocument.IsMatch(name) || LevelMark.IsMatch(name);
}

