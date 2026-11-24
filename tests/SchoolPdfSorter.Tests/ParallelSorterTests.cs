using SchoolPdfSorter.Engine;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SchoolPdfSorter.Tests;

/// <summary>
/// Parity tests for the bounded parallel pipeline in <see cref="Sorter"/>. W = 1 is the
/// sequential reference; W = 4 must produce exactly the same decisions, reasons, tiers,
/// OCR page counts, suggestions and ScanCache payloads. Timing fields are deliberately
/// excluded because they vary by machine and load.
/// </summary>
public sealed class ParallelSorterTests
{
    private static readonly string[] Courses =
    {
        "EIE 326 Data Structures",
        "MTH 101 Elementary Mathematics I"
    };

    [Fact]
    public void Workers_1_and_4_produce_bit_for_bit_identical_decisions_on_a_mixed_corpus()
    {
        WithoutWorkerOverride(() =>
        {
            var root = NewDirectory();

            try
            {
                var paths = BuildMixedCorpus(root);

                var sequential = new Sorter(Courses, new StrictOptions { Workers = 1 }).Run(paths);
                var parallel = new Sorter(Courses, new StrictOptions { Workers = 4 }).Run(paths);

                Assert.Equal(Snapshot(sequential), Snapshot(parallel));
                Assert.Equal(ProjectStats(sequential.Stats), ProjectStats(parallel.Stats));

                // Guard against a vacuous parity win: each shape must settle the way it should.
                Assert.Equal(6, sequential.Items.Count);
                Assert.Equal(4, sequential.Stats.OpenedFiles);
                Assert.Equal(new[]
                {
                    ("notes.pdf", PlanAction.Move, "header", 0),
                    ("EIE 326 lecture notes.pdf", PlanAction.Move, "filename", 0),
                    ("EIE 326 past questions mock.pdf", PlanAction.Dump, "multi", 0),
                    ("EIE 326 timetable.pdf", PlanAction.Dump, "timetable", 0),
                    ("lecture notes.pptx", PlanAction.Move, "slide", 0),
                    ("week three slides.pptx", PlanAction.Dump, "slides", 0),
                }, sequential.Items.Select(i => (
                    Path.GetFileName(i.File.Path), i.Action, i.Tier, i.OcrPages)).ToArray());
                Assert.Contains("in slide 2", sequential.Items.Single(i => i.File.Path.EndsWith("lecture notes.pptx")).Reason);
                Assert.Contains("pictures", sequential.Items.Single(i => i.File.Path.EndsWith("week three slides.pptx")).Reason);
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    [Fact]
    public void Workers_1_and_4_commit_multipage_ocr_in_identical_page_order()
    {
        // Exercises the ordered page commit: cheap evidence has zero codes, so every page of
        // this text-layer PDF goes through NanoOCR's PredictMany pool and is added to the same
        // Evidence instance in ascending PageIndex order before the final reason is written.
        WithoutWorkerOverride(() =>
        {
            var root = NewDirectory();

            try
            {
                var dir = Path.Combine(root, "documents");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "two page notes.pdf");
                CreatePdf(path,
                    "Introduction to signals and systems lecture one",
                    "Further notes on digital filters and sampling");

                var sequential = new Sorter(Courses, new StrictOptions { Workers = 1 }).Run(new[] { path });
                var parallel = new Sorter(Courses, new StrictOptions { Workers = 4 }).Run(new[] { path });

                var item = Assert.Single(sequential.Items);
                Assert.Equal(2, item.OcrPages);
                Assert.Equal("ocr", item.Tier);
                Assert.Equal("no course code in the file name, the title or the first 2 pages", item.Reason);

                Assert.Equal(Snapshot(sequential), Snapshot(parallel));
                Assert.Equal(ProjectStats(sequential.Stats), ProjectStats(parallel.Stats));
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    [Fact]
    public void Workers_1_and_4_produce_identical_scan_cache_payloads()
    {
        WithoutWorkerOverride(() =>
        {
            var root = NewDirectory();

            try
            {
                var paths = BuildMixedCorpus(root);

                var sequential = new Sorter(Courses, new StrictOptions { Workers = 1 }).Run(paths);
                var parallel = new Sorter(Courses, new StrictOptions { Workers = 4 }).Run(paths);

                var sequentialDumps = sequential.Dumps
                    .Select(d => ScanCache.FromResult(Entry(d.File.Path), d))
                    .ToArray();
                var parallelDumps = parallel.Dumps
                    .Select(d => ScanCache.FromResult(Entry(d.File.Path), d))
                    .ToArray();

                // CachedDump is a record: Reason, Tier, Suggestion, Length and ModifiedUtcTicks
                // must all agree, not just the file count.
                Assert.NotEmpty(sequentialDumps);
                Assert.Equal(3, sequentialDumps.Length);
                Assert.Equal(sequentialDumps, parallelDumps);
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    [Fact]
    public async Task Cancellation_during_ocr_throws_OperationCanceledException_promptly()
    {
        var root = NewDirectory();

        try
        {
            var dir = Path.Combine(root, "documents");
            Directory.CreateDirectory(dir);

            var ocrPdf = Path.Combine(dir, "two page notes.pdf");
            CreatePdf(ocrPdf,
                "Introduction to signals and systems lecture one",
                "Further notes on digital filters and sampling",
                "Tutorial problems on convolution and Fourier transforms");

            var namedPdf = Path.Combine(dir, "EIE 326 lecture notes.pdf");
            File.WriteAllBytes(namedPdf, Array.Empty<byte>());

            using var cts = new CancellationTokenSource();
            var sorter = new Sorter(Courses, new StrictOptions { Workers = 4 });

            // Cancel once OCR starts committing a page, so the token is observed while the
            // OCR stage is live rather than only in the cheap triage loop.
            var task = Task.Run(() => sorter.Run(
                new[] { ocrPdf, namedPdf },
                p =>
                {
                    if (p.Phase == "ocr" && p.Page > 0)
                        cts.Cancel();
                },
                cts.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await task.WaitAsync(TimeSpan.FromSeconds(20)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ================================================================= helpers

    /// <summary>The fields that must match bit for bit; timing fields are intentionally absent.</summary>
    private sealed record Decision(
        string Path,
        PlanAction Action,
        string? Course,
        string? Code,
        string Reason,
        string Tier,
        int OcrPages,
        CourseSuggestion? Suggestion);

    private sealed record StatsSnapshot(
        int Total,
        int Moved,
        int Dumped,
        int OcrFiles,
        int OcrPages,
        int OpenedFiles);

    private static Decision[] Snapshot(SorterRunResult run)
        => run.Items.Select(i => new Decision(
            i.File.Path, i.Action, i.Course, i.Code, i.Reason, i.Tier, i.OcrPages, i.Suggestion))
            .ToArray();

    private static StatsSnapshot ProjectStats(SorterStats s)
        => new(s.Total, s.Moved, s.Dumped, s.OcrFiles, s.OcrPages, s.OpenedFiles);

    private static string[] BuildMixedCorpus(string root)
    {
        var dir = Path.Combine(root, "documents");
        Directory.CreateDirectory(dir);

        var header = Path.Combine(dir, "notes.pdf");
        CreatePdf(header, "EIE 326 Data Structures");

        // Unreadable but named for exactly one listed course: settled by name, never opened.
        var byName = Path.Combine(dir, "EIE 326 lecture notes.pdf");
        File.WriteAllBytes(byName, Array.Empty<byte>());

        // The name says one course and a multi-course marker forces a read; the heading
        // names another course, so the file must be left alone.
        var conflict = Path.Combine(dir, "EIE 326 past questions mock.pdf");
        CreatePdf(conflict, "MTH 101 Elementary Mathematics I");

        var timetable = Path.Combine(dir, "EIE 326 timetable.pdf");
        CreatePdf(timetable, "EIE 326 Data Structures");

        var deck = PptxFixture.Create(dir, "lecture notes.pptx", "Data Structures",
            "Week three", "EIE 326 Data Structures");

        var pictures = PptxFixture.Create(dir, "week three slides.pptx", "Week three", "", "");

        return new[] { header, byName, conflict, timetable, deck, pictures };
    }

    private static PdfScanEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new PdfScanEntry(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static void CreatePdf(string path, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(40, 750), font);
        }

        File.WriteAllBytes(path, builder.Build());
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "SchoolPdfSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { /* temp cleanup must never fail a test */ }
    }

    private static void WithoutWorkerOverride(Action body)
    {
        var previous = Environment.GetEnvironmentVariable("SCHOOLPDF_WORKERS");
        Environment.SetEnvironmentVariable("SCHOOLPDF_WORKERS", null);

        try { body(); }
        finally { Environment.SetEnvironmentVariable("SCHOOLPDF_WORKERS", previous); }
    }
}
