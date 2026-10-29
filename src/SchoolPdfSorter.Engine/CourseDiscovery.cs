using System.IO;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// Builds a course list from the names of the user's files, so step 2 can be filled in
/// without typing. Only file names are read here; no document is opened. A code counts
/// once per file, and codes used by more files come first so the likeliest courses lead
/// the list. Labels are code-only ("EIE 326") because a file name rarely has the full
/// course title.
/// </summary>
public static class CourseDiscovery
{
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

            if (string.IsNullOrWhiteSpace(name))
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
}
