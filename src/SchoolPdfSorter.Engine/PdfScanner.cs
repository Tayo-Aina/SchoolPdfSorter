using System.IO;

namespace SchoolPdfSorter.Engine;

/// <summary>One PDF found by the scanner, with the metadata the cache uses to spot changes.</summary>
public sealed record PdfScanEntry(string Path, long Length, long ModifiedUtcTicks);

/// <summary>PDFs to consider and the course folders that were recognized as already organized.</summary>
public sealed record PdfScanResult(
    IReadOnlyList<PdfScanEntry> Files,
    IReadOnlyList<string> SortedFolders);

/// <summary>
/// Finds PDFs under a source folder. Folders directly under the target that already name
/// exactly one of the listed courses are treated as sorted and skipped, so a later run does
/// not walk back through every file the app already filed. The target tree itself is skipped
/// when it lives inside the source.
/// </summary>
public static class PdfScanner
{
    public static PdfScanResult Scan(
        string sourceDir,
        string targetDir,
        bool includeSubfolders,
        IEnumerable<string> courses)
    {
        var sourceRoot = Path.GetFullPath(sourceDir);
        var targetRoot = Path.GetFullPath(targetDir);

        var sourceFull = WithSeparator(sourceRoot);
        var targetFull = WithSeparator(targetRoot);

        var sameRoot = string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase);
        var excludeTarget = !sameRoot &&
                            targetFull.StartsWith(sourceFull, StringComparison.OrdinalIgnoreCase);

        var sortedFolders = FindSortedFolders(targetRoot, courses);
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var files = new List<PdfScanEntry>();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.pdf", option))
        {
            if (excludeTarget && IsUnder(file, targetFull))
                continue;

            var sorted = sortedFolders.FirstOrDefault(folder => IsUnder(file, folder));
            if (sorted is not null)
            {
                skipped.Add(sorted);
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists || info.Length == 0)
                    continue;
            }
            catch
            {
                continue;
            }

            files.Add(new PdfScanEntry(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        return new PdfScanResult(
            files,
            skipped.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Direct children of the target that identify exactly one listed course. This catches
    /// app-created folders such as "EIE 326" or "EIE 326 Signals and Systems" even when the
    /// course label is written slightly differently. A folder naming two courses is not skipped.
    /// </summary>
    private static List<string> FindSortedFolders(string targetRoot, IEnumerable<string> courses)
    {
        var codes = courses
            .Select(CourseCodes.TryCreate)
            .Where(code => code is not null)
            .Select(code => code!)
            .ToList();

        var folders = new List<string>();
        if (codes.Count == 0 || !Directory.Exists(targetRoot))
            return folders;

        foreach (var directory in Directory.EnumerateDirectories(targetRoot))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (CourseCodes.Find(name, codes).Count != 1)
                continue;

            // A folder that mentions a second course is a mixed pile, not a sorted course folder.
            if (CourseCodes.FindUnlisted(name, codes, includeUnknownPrefixes: true).Count > 0)
                continue;

            folders.Add(WithSeparator(directory));
        }

        return folders;
    }

    private static string WithSeparator(string directory)
    {
        var full = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return full + Path.DirectorySeparatorChar;
    }

    private static bool IsUnder(string path, string directoryFull)
        => path.StartsWith(directoryFull, StringComparison.OrdinalIgnoreCase);
}
