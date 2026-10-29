using SchoolPdfSorter.Engine;

namespace SchoolPdfSorter.Tests;

public sealed class IncrementalTests
{
    private static readonly string[] Courses =
    {
        "EIE 326 Data Structures",
        "MTH 101 Elementary Mathematics I"
    };

    // ================================================================= scanner

    [Fact]
    public void Scan_skips_course_folders_when_the_target_is_the_source()
    {
        var root = NewDirectory();

        try
        {
            var courseFile = WritePdf(Path.Combine(root, "EIE 326 Data Structures"), "inside.pdf");
            WritePdf(Path.Combine(root, "MTH 101 Elementary Mathematics I"), "inside2.pdf");
            WritePdf(root, "root.pdf");
            WritePdf(Path.Combine(root, "300 lvl EIE pq"), "mixed.pdf");

            var result = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);

            Assert.Equal(new[] { "mixed.pdf", "root.pdf" },
                result.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n));
            Assert.Equal(2, result.SortedFolders.Count);
            Assert.DoesNotContain(courseFile, result.Files.Select(f => f.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_matches_a_course_folder_even_without_spaces_or_with_extra_words()
    {
        var root = NewDirectory();

        try
        {
            WritePdf(Path.Combine(root, "EIE326 tutorial"), "inside.pdf");
            WritePdf(root, "root.pdf");

            var result = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);

            Assert.Single(result.Files);
            Assert.Equal("root.pdf", Path.GetFileName(result.Files[0].Path));
            Assert.Single(result.SortedFolders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_does_not_skip_a_folder_that_names_two_courses()
    {
        var root = NewDirectory();

        try
        {
            WritePdf(Path.Combine(root, "EIE 326 MTH 101"), "inside.pdf");
            WritePdf(root, "root.pdf");

            var result = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);

            Assert.Equal(2, result.Files.Count);
            Assert.Empty(result.SortedFolders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_does_not_skip_course_folders_when_the_target_is_elsewhere()
    {
        var root = NewDirectory();
        var target = NewDirectory();

        try
        {
            WritePdf(Path.Combine(root, "EIE 326 Data Structures"), "inside.pdf");

            var result = PdfScanner.Scan(root, target, includeSubfolders: true, Courses);

            Assert.Single(result.Files);
            Assert.Empty(result.SortedFolders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Scan_excludes_the_target_tree_when_it_is_inside_the_source()
    {
        var root = NewDirectory();

        try
        {
            var target = Path.Combine(root, "Sorted");
            WritePdf(Path.Combine(target, "EIE 326 Data Structures"), "filed.pdf");
            WritePdf(root, "root.pdf");

            var result = PdfScanner.Scan(root, target, includeSubfolders: true, Courses);

            Assert.Single(result.Files);
            Assert.Equal("root.pdf", Path.GetFileName(result.Files[0].Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_ignores_empty_and_zero_byte_pdfs()
    {
        var root = NewDirectory();

        try
        {
            File.WriteAllBytes(Path.Combine(root, "empty.pdf"), Array.Empty<byte>());
            WritePdf(root, "real.pdf");

            var result = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);

            Assert.Single(result.Files);
            Assert.Equal("real.pdf", Path.GetFileName(result.Files[0].Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // =================================================================== cache

    [Fact]
    public void Cache_reuses_an_unchanged_left_alone_file_and_its_note()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var file = WritePdf(root, "ambiguous.pdf");
            var entry = Entry(file);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (entry, new CachedDump(entry.Length, entry.ModifiedUtcTicks,
                    "several course codes", "multi",
                    new CourseSuggestion("EIE 326 Data Structures", "eie326", 0.99, "title")))
            });

            var loaded = ScanCache.Load(cachePath);
            var hits = loaded.Unchanged(root, Courses, new[] { entry });

            Assert.True(hits.TryGetValue(file, out var dump));
            Assert.Equal("several course codes", dump.Reason);
            Assert.Equal("multi", dump.Tier);
            Assert.Equal("EIE 326 Data Structures", dump.Suggestion!.Course);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_rechecks_a_file_when_its_size_changes()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var file = WritePdf(root, "notes.pdf");
            var original = Entry(file);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (original, new CachedDump(original.Length, original.ModifiedUtcTicks,
                    "no code", "ocr", null))
            });

            File.AppendAllText(file, "changed");
            var changed = Entry(file);

            var hits = ScanCache.Load(cachePath).Unchanged(root, Courses, new[] { changed });

            Assert.Empty(hits);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_rechecks_a_file_when_its_write_time_changes()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var file = WritePdf(root, "notes.pdf");
            var original = Entry(file);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (original, new CachedDump(original.Length, original.ModifiedUtcTicks,
                    "no code", "ocr", null))
            });

            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
            var changed = Entry(file);

            Assert.NotEqual(original.ModifiedUtcTicks, changed.ModifiedUtcTicks);
            Assert.Empty(ScanCache.Load(cachePath).Unchanged(root, Courses, new[] { changed }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_is_ignored_when_the_course_list_changes()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var file = WritePdf(root, "notes.pdf");
            var entry = Entry(file);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (entry, new CachedDump(entry.Length, entry.ModifiedUtcTicks,
                    "no code", "ocr", null))
            });

            var other = new[] { "EIE 326 Data Structures" };
            Assert.Empty(ScanCache.Load(cachePath).Unchanged(root, other, new[] { entry }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_ignores_course_order_and_whitespace_changes()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var file = WritePdf(root, "notes.pdf");
            var entry = Entry(file);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (entry, new CachedDump(entry.Length, entry.ModifiedUtcTicks,
                    "no code", "ocr", null))
            });

            var reordered = new[]
            {
                "  MTH   101   Elementary Mathematics I ",
                "EIE 326 Data Structures"
            };

            Assert.Single(ScanCache.Load(cachePath).Unchanged(root, reordered, new[] { entry }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_drops_files_that_are_no_longer_left_alone()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var one = WritePdf(root, "one.pdf");
            var two = WritePdf(root, "two.pdf");
            var oneEntry = Entry(one);
            var twoEntry = Entry(two);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (oneEntry, new CachedDump(oneEntry.Length, oneEntry.ModifiedUtcTicks,
                    "no code", "ocr", null)),
                (twoEntry, new CachedDump(twoEntry.Length, twoEntry.ModifiedUtcTicks,
                    "no code", "ocr", null))
            });

            cache.Remember(root, Courses, new[]
            {
                (oneEntry, new CachedDump(oneEntry.Length, oneEntry.ModifiedUtcTicks,
                    "no code", "ocr", null))
            });

            var hits = ScanCache.Load(cachePath).Unchanged(root, Courses, new[] { oneEntry, twoEntry });

            Assert.True(hits.ContainsKey(one));
            Assert.False(hits.ContainsKey(two));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_does_not_save_a_decision_for_a_file_that_changed_during_the_run()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var file = WritePdf(root, "notes.pdf");
            var entry = Entry(file);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            File.AppendAllText(file, "changed while the scan was running");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (entry, new CachedDump(entry.Length, entry.ModifiedUtcTicks,
                    "no code", "ocr", null))
            });

            var current = Entry(file);
            Assert.Empty(ScanCache.Load(cachePath).Unchanged(root, Courses, new[] { current }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_keeps_decisions_for_file_types_that_were_not_scanned()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            var pdf = WritePdf(root, "notes.pdf");
            var deck = WritePdf(root, "deck.pptx");
            var pdfEntry = Entry(pdf);
            var deckEntry = Entry(deck);
            var cachePath = Path.Combine(cacheDir, "cache.json");

            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (pdfEntry, new CachedDump(pdfEntry.Length, pdfEntry.ModifiedUtcTicks,
                    "no code", "ocr", null))
            }, new[] { ".pdf" });

            cache.Remember(root, Courses, new[]
            {
                (deckEntry, new CachedDump(deckEntry.Length, deckEntry.ModifiedUtcTicks,
                    "no code", "slides", null))
            }, new[] { ".pptx" });

            var hits = ScanCache.Load(cachePath)
                .Unchanged(root, Courses, new[] { pdfEntry, deckEntry });

            Assert.True(hits.ContainsKey(pdf));
            Assert.True(hits.ContainsKey(deck));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Cache_treats_a_corrupt_file_as_empty()
    {
        var cacheDir = NewDirectory();

        try
        {
            var cachePath = Path.Combine(cacheDir, "cache.json");
            File.WriteAllText(cachePath, "{ this is not json");

            var cache = ScanCache.Load(cachePath);

            Assert.Empty(cache.Unchanged(cacheDir, Courses, Array.Empty<PdfScanEntry>()));
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ============================================================== end to end

    [Fact]
    public void Second_scan_skips_the_sorted_course_folder_and_reuses_the_left_alone_file()
    {
        var root = NewDirectory();
        var cacheDir = NewDirectory();

        try
        {
            WritePdf(root, "ambiguous.pdf");
            WritePdf(root, "new.pdf");
            var filed = WritePdf(Path.Combine(root, "EIE 326 Data Structures"), "filed.pdf");
            var cachePath = Path.Combine(cacheDir, "cache.json");

            // First run: the course folder is recognized as sorted, and the ambiguous
            // file is checked and left alone. Only those two files are scanned.
            var first = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);
            Assert.Equal(new[] { "ambiguous.pdf", "new.pdf" },
                first.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n));
            Assert.Single(first.SortedFolders);

            var ambiguous = first.Files.Single(f => Path.GetFileName(f.Path) == "ambiguous.pdf");
            var cache = ScanCache.Load(cachePath);
            cache.Remember(root, Courses, new[]
            {
                (ambiguous, new CachedDump(ambiguous.Length, ambiguous.ModifiedUtcTicks,
                    "several course codes", "multi", null))
            });

            // Second run: the same scan, with the cache loaded fresh from disk.
            var second = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);
            var hits = ScanCache.Load(cachePath).Unchanged(root, Courses, second.Files);

            Assert.Single(second.SortedFolders);
            Assert.Contains(second.Files, f => f.Path == ambiguous.Path);
            Assert.True(hits.ContainsKey(ambiguous.Path));
            Assert.False(hits.ContainsKey(second.Files.Single(f => f.Path.EndsWith("new.pdf")).Path));

            // A newly added file is still found and needs a fresh check.
            var added = WritePdf(root, "added.pdf");
            var third = PdfScanner.Scan(root, root, includeSubfolders: true, Courses);
            var thirdHits = ScanCache.Load(cachePath).Unchanged(root, Courses, third.Files);

            Assert.Contains(third.Files, f => f.Path == added);
            Assert.False(thirdHits.ContainsKey(added));
            Assert.True(thirdHits.ContainsKey(ambiguous.Path));
            Assert.DoesNotContain(third.Files, f => f.Path == filed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ================================================================ helpers

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "SchoolPdfSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WritePdf(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }

    private static PdfScanEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new PdfScanEntry(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }
}
