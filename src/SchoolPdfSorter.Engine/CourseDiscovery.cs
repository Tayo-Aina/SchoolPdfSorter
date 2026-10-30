using System.IO;
using System.Text.RegularExpressions;

namespace SchoolPdfSorter.Engine;

/// <summary>
/// Builds a course list from the user's files, so step 2 can be filled in without typing.
/// <see cref="FromFileNames"/> reads only names; <see cref="FromFiles"/> also reads the
/// metadata title, the heading and the first page/slide of each file. A code counts once
/// per file, and codes used by more files come first so the likeliest courses lead the
/// list. Labels are code-only ("EIE 326") because a file rarely has the full course title.
/// Personal documents such as SIWES reports are skipped even when a name and a level number
/// happen to look like a course code.
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
    /// Course code labels found in the contents of <paramref name="filePaths"/>, ordered by
    /// how many files mention each code and then by the code itself. The file name, metadata
    /// title, heading and first page/slide are read without OCR; personal documents are still
    /// skipped whole. Each line is searched separately so a heading cannot pair with a number
    /// on the next line, and a code counts once per file. Spelling variants of the same number
    /// ("GEC 321" and a typo "CEG 321" inside a file) collapse to the spelling most often seen
    /// in file names, so a typo cannot become a course of its own.
    /// </summary>
    public static List<string> FromFiles(
        IEnumerable<string> filePaths,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (filePaths is null)
            return new List<string>();

        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        int total = paths.Length;
        int done = 0;

        Parallel.For(0, total,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            i =>
            {
                var path = paths[i];
                var name = Path.GetFileNameWithoutExtension(path);
                var nameCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(name) && !LooksPersonal(name))
                {
                    AddCodes(nameCodes, name);
                    codes.UnionWith(nameCodes);

                    var signals = Signals.ExtractSafe(path);
                    AddCodes(codes, signals.MetadataTitle);
                    AddCodes(codes, signals.BiggestLine);
                    AddCodes(codes, signals.FirstPages.FirstOrDefault());

                    if (codes.Count > 0)
                    {
                        lock (counts)
                        {
                            foreach (var code in codes)
                                counts[code] = counts.GetValueOrDefault(code) + 1;

                            foreach (var code in nameCodes)
                                nameCounts[code] = nameCounts.GetValueOrDefault(code) + 1;
                        }
                    }
                }

                int finished = Interlocked.Increment(ref done);
                progress?.Invoke(finished, total, Path.GetFileName(path));
            });

        return MergeVariants(counts, nameCounts)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .ToList();
    }

    /// <summary>
    /// Drops misspelled spellings of a code that share its number. Within each number, codes
    /// whose prefixes are anagrams ("GEC"/"CEG") or one letter apart ("CVE"/"CGE") are one
    /// group; the spelling with the most file-name hits wins, then the most total hits, then
    /// the alphabetically first. Codes with different numbers or prefix lengths never merge.
    /// </summary>
    private static List<KeyValuePair<string, int>> MergeVariants(
        Dictionary<string, int> counts,
        Dictionary<string, int> nameCounts)
    {
        var kept = new List<KeyValuePair<string, int>>();

        foreach (var byNumber in counts.Keys
                     .GroupBy(code => NumberPart(code), StringComparer.OrdinalIgnoreCase))
        {
            var variants = byNumber.ToList();
            var parent = Enumerable.Range(0, variants.Count).ToArray();

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }

                return i;
            }

            for (int i = 0; i < variants.Count; i++)
            {
                for (int j = i + 1; j < variants.Count; j++)
                {
                    if (!IsVariant(PrefixPart(variants[i]), PrefixPart(variants[j])))
                        continue;

                    int a = Find(i);
                    int b = Find(j);
                    if (a != b)
                        parent[b] = a;
                }
            }

            foreach (var cluster in Enumerable.Range(0, variants.Count).GroupBy(Find))
            {
                var best = cluster
                    .Select(i => variants[i])
                    .OrderByDescending(code => nameCounts.GetValueOrDefault(code))
                    .ThenByDescending(code => counts[code])
                    .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .First();

                kept.Add(new KeyValuePair<string, int>(best, counts[best]));
            }
        }

        return kept;
    }

    /// <summary>"GEC 321" -> "GEC".</summary>
    private static string PrefixPart(string code)
    {
        int space = code.IndexOf(' ');
        return space < 0 ? code : code[..space];
    }

    /// <summary>"GEC 321" -> "321"; the optional trailing letter is part of the number (CS 61A).</summary>
    private static string NumberPart(string code)
    {
        int space = code.IndexOf(' ');
        return space < 0 ? "" : code[(space + 1)..];
    }

    /// <summary>Same letters in another order, or one letter changed in place, with equal length.</summary>
    private static bool IsVariant(string a, string b)
    {
        if (a.Length != b.Length)
            return false;

        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;

        var sortedA = a.ToUpperInvariant().OrderBy(c => c);
        var sortedB = b.ToUpperInvariant().OrderBy(c => c);
        if (sortedA.SequenceEqual(sortedB))
            return true;

        int differences = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i]))
                continue;

            if (++differences > 1)
                return false;
        }

        return differences == 1;
    }

    /// <summary>Codes on each line of <paramref name="text"/>, added to <paramref name="codes"/>.</summary>
    private static void AddCodes(ISet<string> codes, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (var line in text.Split('\n'))
        {
            foreach (var code in CourseCodes.FindCourseLikeCodes(line))
                codes.Add(code);
        }
    }

    /// <summary>
    /// True for a name/label that is about a person or an administrative document, not a
    /// course. A level marker such as "300 level" also counts: it is not a course code.
    /// </summary>
    public static bool LooksPersonal(string name)
        => PersonalDocument.IsMatch(name) || LevelMark.IsMatch(name);
}

