using System.IO;
using System.Text;

namespace SchoolPdfSorter.Engine;

/// <summary>File-side execution: moving decided PDFs into course folders.</summary>
public static class FileFiler
{
    /// <summary>Course names become folder names: strip what Windows doesn't allow.</summary>
    public static string SanitizeFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '-' : c);

        var sanitized = sb.ToString().Trim().TrimEnd('.');
        return sanitized.Length == 0 ? "Course" : sanitized;
    }

    /// <summary>A free destination name; "file (2).pdf" when the name is taken.</summary>
    public static string UniqueTarget(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);

        if (!System.IO.File.Exists(path))
            return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");

            if (!System.IO.File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Could not find a free name for {fileName} in {directory}.");
    }

    /// <summary>Creates target\&lt;course&gt; and moves the PDF in. Returns the new full path.</summary>
    public static string MoveInto(string sourcePath, string fileName, string course, string targetDir)
    {
        var directory = Path.Combine(targetDir, SanitizeFolder(course));
        Directory.CreateDirectory(directory);
        var destination = UniqueTarget(directory, fileName);
        System.IO.File.Move(sourcePath, destination);
        return destination;
    }

    /// <summary>Move a sorter decision into its decided course folder.</summary>
    public static string MoveInto(SorterFileResult item, string targetDir)
        => MoveInto(item.File.Path, item.File.FileName, item.Course!, targetDir);
}
