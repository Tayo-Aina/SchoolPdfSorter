using SchoolPdfSorter.Engine;
using Xunit;

namespace SchoolPdfSorter.Tests;

public class FileFilerTests
{
    [Fact]
    public void SanitizeFolder_replaces_invalid_chars_and_trims()
    {
        Assert.Equal("ECON- 301-", FileFiler.SanitizeFolder("ECON: 301*"));
        Assert.Equal("Course", FileFiler.SanitizeFolder("..."));
        Assert.Equal("GST 102 Communication in English", FileFiler.SanitizeFolder("GST 102 Communication in English"));
    }

    [Fact]
    public void UniqueTarget_suffixes_on_collision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sps-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "a.pdf"), "x");
            Assert.Equal(Path.Combine(dir, "a (2).pdf"), FileFiler.UniqueTarget(dir, "a.pdf"));

            File.WriteAllText(Path.Combine(dir, "a (2).pdf"), "x");
            Assert.Equal(Path.Combine(dir, "a (3).pdf"), FileFiler.UniqueTarget(dir, "a.pdf"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MoveInto_creates_course_folder_and_moves_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "sps-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "messy");
        var target = Path.Combine(root, "school");
        Directory.CreateDirectory(source);

        try
        {
            var file = Path.Combine(source, "document.pdf");
            File.WriteAllText(file, "pdf-bytes");

            var destination = FileFiler.MoveInto(file, "document.pdf", "ECON 301 Microeconomics", target);

            Assert.False(File.Exists(file));
            Assert.True(File.Exists(destination));
            Assert.Equal(Path.Combine(target, "ECON 301 Microeconomics", "document.pdf"), destination);
            Assert.Equal("pdf-bytes", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
