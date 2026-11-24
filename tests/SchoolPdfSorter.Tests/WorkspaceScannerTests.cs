using SchoolPdfSorter.Engine;

namespace SchoolPdfSorter.Tests;

/// <summary>
/// The single-pass scanner must classify every bucket in one walk without changing the old
/// Scan(...) behaviour that the incremental tests already cover.
/// </summary>
public sealed class WorkspaceScannerTests
{
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "SchoolPdfSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static PdfScanEntry Touch(string directory, string name, int bytes = 16)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)1, bytes).ToArray());
        var info = new FileInfo(path);
        return new PdfScanEntry(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    [Fact]
    public void ScanWorkspace_buckets_selected_legacy_and_unsupported_files_in_one_pass()
    {
        var root = NewDirectory();
        try
        {
            var pdf = Touch(root, "EIE 301 notes.pdf");
            var legacy = Touch(root, "EIE 301 old.ppt");
            var other = Touch(root, "EIE 301 notes.doc");
            Touch(root, "~$EIE 301 notes.pdf");
            Touch(root, "empty.pdf", 0);
            Touch(root, "EIE 301 notes.pdfx");

            var workspace = PdfScanner.ScanWorkspace(root, root, true,
                Array.Empty<string>(), new[] { ".pdf" });

            Assert.Equal(new[] { pdf.Path }, workspace.Files.Select(f => f.Path));
            Assert.Equal(new[] { legacy.Path }, workspace.LegacyPowerPoints.Select(f => f.Path));
            Assert.Equal(new[] { other.Path }, workspace.UnsupportedFiles.Select(f => f.Path));
            Assert.Equal(1, workspace.TemporaryFiles);
            Assert.Empty(workspace.SortedFolders);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ScanWorkspace_keeps_Scan_semantics_for_sorted_folders_and_zero_byte_files()
    {
        var root = NewDirectory();
        try
        {
            var target = root;
            var courseFolder = Path.Combine(root, "EIE 301");
            var inside = Touch(courseFolder, "inside.pdf");
            var outside = Touch(root, "outside.pdf");

            var courses = new[] { "EIE 301 Signals and Systems" };
            var workspace = PdfScanner.ScanWorkspace(root, target, true, courses, new[] { ".pdf" });
            var scan = PdfScanner.Scan(root, target, true, courses, new[] { ".pdf" });

            Assert.Equal(new[] { outside.Path }, workspace.Files.Select(f => f.Path));
            Assert.Equal(scan.Files.Select(f => f.Path), workspace.Files.Select(f => f.Path));
            Assert.Equal(scan.SortedFolders, workspace.SortedFolders);
            Assert.Single(workspace.SortedFolders);
            Assert.Equal(0, workspace.TemporaryFiles);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ScanWorkspace_uses_PathGetExtension_not_a_wildcard_match()
    {
        var root = NewDirectory();
        try
        {
            var good = Touch(root, "notes.pdf");
            Touch(root, "notes.PDFX");
            Touch(root, "notes.pdf.bak");

            var workspace = PdfScanner.ScanWorkspace(root, root, false,
                Array.Empty<string>(), new[] { ".pdf" });

            Assert.Equal(new[] { good.Path }, workspace.Files.Select(f => f.Path));
        }
        finally { TryDelete(root); }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* temp cleanup is best effort */ }
    }
}
