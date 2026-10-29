using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolPdfSorter.Engine;

/// <summary>The decision for a PDF that was left untouched, kept so the next run can skip it.</summary>
public sealed record CachedDump(
    long Length,
    long ModifiedUtcTicks,
    string Reason,
    string Tier,
    CourseSuggestion? Suggestion);

/// <summary>
/// An invisible record of files the app already checked and left in place. The cache lives
/// under LocalAppData, never in the user's folders. A file is reused only when its size and
/// last-write time are unchanged and the course list and app build are the same.
/// </summary>
public sealed class ScanCache
{
    public const int CurrentVersion = 1;

    private static readonly string Rules =
        typeof(ScanCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly CacheFile _data;

    private ScanCache(string path, CacheFile data)
    {
        _path = path;
        _data = data;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SchoolPdfSorter",
        "checked-files.json");

    public static ScanCache Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);
                if (data is { Version: CurrentVersion } && data.Sources is not null)
                    return new ScanCache(path, data);
            }
        }
        catch
        {
            // A damaged or unreadable cache is ignored.
        }

        return new ScanCache(path, new CacheFile());
    }

    /// <summary>
    /// Cached "left alone" decisions that still match the file on disk. Only files from the
    /// same source, with the same course list and the same app build, are considered.
    /// </summary>
    public IReadOnlyDictionary<string, CachedDump> Unchanged(
        string sourceDir,
        IReadOnlyList<string> courses,
        IReadOnlyList<PdfScanEntry> files)
    {
        var hits = new Dictionary<string, CachedDump>(StringComparer.OrdinalIgnoreCase);

        if (!_data.Sources.TryGetValue(SourceKey(sourceDir), out var source) ||
            source.Files is null ||
            !string.Equals(source.Rules, Rules, StringComparison.Ordinal) ||
            !string.Equals(source.Courses, CoursesFingerprint(courses), StringComparison.Ordinal))
            return hits;

        var root = Root(sourceDir);

        foreach (var file in files)
        {
            var key = RelativeKey(root, file.Path);
            if (!source.Files.TryGetValue(key, out var cached) || cached is null)
                continue;

            if (cached.Length != file.Length || cached.ModifiedUtcTicks != file.ModifiedUtcTicks)
                continue;

            hits[file.Path] = cached.ToDump();
        }

        return hits;
    }

    /// <summary>
    /// Replaces the cache for this source with the files that are still left alone. Files that
    /// were moved are naturally dropped; changed files were rechecked and are saved fresh.
    /// When <paramref name="extensions"/> is given, decisions for other file types are kept, so
    /// a PDFs-only run does not throw away what a PowerPoint-only run already learned.
    /// </summary>
    public void Remember(
        string sourceDir,
        IReadOnlyList<string> courses,
        IEnumerable<(PdfScanEntry File, CachedDump Dump)> dumps,
        IEnumerable<string>? extensions = null)
    {
        var root = Root(sourceDir);
        var sourceKey = SourceKey(sourceDir);
        var files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

        if (extensions is not null &&
            _data.Sources.TryGetValue(sourceKey, out var previous) &&
            previous.Files is not null &&
            string.Equals(previous.Rules, Rules, StringComparison.Ordinal) &&
            string.Equals(previous.Courses, CoursesFingerprint(courses), StringComparison.Ordinal))
        {
            var types = new HashSet<string>(
                extensions.Select(extension =>
                    extension.StartsWith('.') ? extension : "." + extension),
                StringComparer.OrdinalIgnoreCase);
            if (types.Count > 0)
            {
                foreach (var (key, entry) in previous.Files)
                {
                    if (!types.Contains(Path.GetExtension(key)))
                        files[key] = entry;
                }
            }
        }

        foreach (var (file, dump) in dumps)
        {
            try
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists ||
                    info.Length != dump.Length ||
                    info.LastWriteTimeUtc.Ticks != dump.ModifiedUtcTicks)
                    continue;
            }
            catch
            {
                continue;
            }

            var key = RelativeKey(root, file.Path);
            if (files.ContainsKey(key))
                continue;

            files[key] = new FileEntry
            {
                Length = dump.Length,
                ModifiedUtcTicks = dump.ModifiedUtcTicks,
                Reason = dump.Reason,
                Tier = dump.Tier,
                SuggestionCourse = dump.Suggestion?.Course,
                SuggestionCode = dump.Suggestion?.Code,
                SuggestionScore = dump.Suggestion?.Score,
                SuggestionWhy = dump.Suggestion?.Why
            };
        }

        _data.Sources[SourceKey(sourceDir)] = new SourceEntry
        {
            Rules = Rules,
            Courses = CoursesFingerprint(courses),
            Files = files
        };

        Save();
    }

    public static CachedDump FromResult(PdfScanEntry file, SorterFileResult result)
        => new(file.Length, file.ModifiedUtcTicks, result.Reason, result.Tier, result.Suggestion);

    /// <summary>A stable hash of the normalized, order-independent course list.</summary>
    public static string CoursesFingerprint(IEnumerable<string> courses)
    {
        var text = string.Join("\n", courses
            .Select(NormalizeLabel)
            .Where(label => label.Length > 0)
            .OrderBy(label => label, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Speed is a convenience; a cache that cannot be written must never fail a run.
        }
    }

    private static string NormalizeLabel(string label)
        => string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static string Root(string directory)
        => Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string SourceKey(string sourceDir)
        => Root(sourceDir).ToLowerInvariant();

    private static string RelativeKey(string root, string path)
        => Path.GetRelativePath(root, path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToLowerInvariant();

    private sealed class CacheFile
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, SourceEntry> Sources { get; set; } = new();
    }

    private sealed class SourceEntry
    {
        public string Rules { get; set; } = "";
        public string Courses { get; set; } = "";
        public Dictionary<string, FileEntry> Files { get; set; } = new();
    }

    private sealed class FileEntry
    {
        public long Length { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public string Reason { get; set; } = "";
        public string Tier { get; set; } = "";
        public string? SuggestionCourse { get; set; }
        public string? SuggestionCode { get; set; }
        public double? SuggestionScore { get; set; }
        public string? SuggestionWhy { get; set; }

        public CachedDump ToDump() => new(
            Length,
            ModifiedUtcTicks,
            Reason,
            Tier,
            SuggestionCourse is { } course && SuggestionCode is { } code
                ? new CourseSuggestion(course, code, SuggestionScore ?? 0, SuggestionWhy ?? "")
                : null);
    }
}
