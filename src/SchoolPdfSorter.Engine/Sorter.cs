using System.Diagnostics;
using System.Text.RegularExpressions;
using NanoOcr;
using NanoOcr.Pipeline;

namespace SchoolPdfSorter.Engine;

/// <summary>What the sorter does with a file: move it, or leave it exactly where it is.</summary>
public enum PlanAction { Move, Dump }

public sealed record SorterProgress(
    int Done,
    int Total,
    string Phase,
    string FileName,
    int Page = 0,
    int Pages = 0,
    string? Course = null);

public sealed record SorterFileResult(
    FileSignals File,
    PlanAction Action,
    string? Course,
    string? Code,
    string Reason,
    string Tier,
    int OcrPages,
    double OcrSeconds,
    CourseSuggestion? Suggestion = null)
{
    public bool IsMove => Action == PlanAction.Move;
}

public sealed record SorterStats(
    int Total,
    int Moved,
    int Dumped,
    int OcrFiles,
    int OcrPages,
    double SignalSeconds,
    double ScanSeconds,
    double OcrSeconds,
    double OcrInitSeconds,
    double TotalSeconds,
    int OpenedFiles = 0);

public sealed record SorterRunResult(IReadOnlyList<SorterFileResult> Items, SorterStats Stats)
{
    public IReadOnlyList<SorterFileResult> Moves => Items.Where(i => i.IsMove).ToList();
    public IReadOnlyList<SorterFileResult> Dumps => Items.Where(i => !i.IsMove).ToList();
}

public sealed class StrictOptions
{
    /// <summary>How many pages the OCR rescue may read (the first N pages of the PDF).</summary>
    public int MaxOcrPages { get; set; } = PdfText.MaxPages;

    /// <summary>Render resolution for OCR of scanned pages.</summary>
    public double Dpi { get; set; } = 200;

    /// <summary>Kernel threads per OCR page; 0 uses the resource budget's laptop default.</summary>
    public int Threads { get; set; }

    /// <summary>
    /// Maximum number of concurrent OCR workers. 0 derives it from physical cores and available
    /// RAM via <see cref="ResourceBudget"/>. Never exceeds <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int Workers { get; set; }

    /// <summary>
    /// Stop early when the file already points to another course: a different code in the
    /// file name is skipped without opening the file, and a sibling code in the cheap text
    /// (same department prefix, different number) skips OCR.
    /// </summary>
    public bool StopOnUnlistedCode { get; set; } = true;

    /// <summary>
    /// Add a very high-confidence "looks like this course" note to files that stay put.
    /// It is only a note: it never moves or renames anything.
    /// </summary>
    public bool SuggestNearMatches { get; set; } = true;
}

/// <summary>
/// Strict course-code filing. A file moves only when exactly one course from the list is
/// identified, in this order:
///   1. its file name / parent folder,
///   2. the document title / largest-font line (or the first slide line),
///   3. the text layer of its first pages, or the text of its first slides,
///   4. OCR of its first pages (NanoOCR, a pure-C# port of Indra OCR; PDFs only).
/// Zero codes, or several different courses in the same place, means it is left untouched.
/// A different course code in the file name (for example CSC 201 when only EIE 301 is listed)
/// leaves the file alone without opening it. A sibling code in the cheap text (EIE 315 when
/// only EIE 301 is listed) also leaves it alone and skips OCR. No embeddings, no guesses.
///
/// Execution uses one shared OCR engine and bounded in-process agents: a triage pass settles
/// files by name, a small signal pool reads cheap text, and NanoOCR's PredictMany pool runs
/// page jobs on at most W workers (each single-threaded) over the same model store. Page text is
/// committed in ascending page index, so every reason, tier and cached payload is identical to
/// the old sequential loop.
/// </summary>
public sealed class Sorter
{
    private readonly IReadOnlyList<CourseCode> _codes;
    private readonly StrictOptions _opt;

    public Sorter(IEnumerable<string> courses, StrictOptions? options = null)
    {
        var codes = new List<CourseCode>();
        var missing = new List<string>();

        foreach (var course in courses)
        {
            var code = CourseCodes.TryCreate(course);
            if (code is null)
                missing.Add(course);
            else
                codes.Add(code);
        }

        _codes = codes;
        CoursesWithoutCode = missing;
        _opt = options ?? new StrictOptions();
    }

    /// <summary>Courses whose labels contain no recognisable code; they can never match a file.</summary>
    public IReadOnlyList<string> CoursesWithoutCode { get; }

    public IReadOnlyList<CourseCode> Codes => _codes;

    /// <summary>A file whose cheap evidence could not settle it, handed to the OCR pool.</summary>
    private sealed record OcrJob(
        int Index,
        int Total,
        FileSignals File,
        Evidence Evidence,
        CourseSuggestion? Suggestion,
        int ScanPages);

    public SorterRunResult Run(
        IReadOnlyList<string> paths,
        Action<SorterProgress>? progress = null,
        CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        int total = paths.Count;

        // ---- triage agent: file/folder names first; open only the files still in play ----
        var signals = new FileSignals[total];
        var open = new bool[total];
        int toOpen = 0;

        for (int i = 0; i < total; i++)
        {
            var light = Signals.Light(paths[i]);
            signals[i] = light;

            // A timetable is never a single-course document, even when its file name has one code.
            // Leave it alone without spending time opening or OCR-ing it.
            if (IsTimetable(light.FileName) || IsTimetable(light.ParentFolder))
                continue;

            var nameHits = CourseCodes.Find(light.FileName, _codes);
            var folderHits = nameHits.Count == 0
                ? CourseCodes.Find(light.ParentFolder, _codes)
                : new List<CourseCode>();

            bool nameHasListed = nameHits.Count > 0;
            bool folderHasListed = folderHits.Count > 0;

            // "Past questions", "PQ", "merged", "combined", ... may contain several courses.
            // If the name has a code, still open it so the pages can be checked.
            bool mayBeMulti = IsMultiCourse(light.FileName) || IsMultiCourse(light.ParentFolder);

            bool settled;
            if (nameHasListed)
            {
                // A code in the file name is strong evidence; only a multi-course name
                // makes us look inside.
                settled = !mayBeMulti;
            }
            else if (folderHasListed)
            {
                // A course folder is not proof: a timetable or another course's paper
                // can be sitting inside it. Open this one and check the pages.
                settled = false;
            }
            else
            {
                // A file whose name already names another course does not need to be opened.
                settled = _opt.StopOnUnlistedCode &&
                    CourseCodes.FindUnlisted(
                        light.FileName, _codes, includeUnknownPrefixes: true).Count > 0;
            }

            if (!settled)
            {
                open[i] = true;
                toOpen++;
            }
        }

        // ---- signal agents: bounded cheap extraction (streamed PdfPig / Open XML) ----
        int extracted = 0;
        var extractSw = Stopwatch.StartNew();
        int budget = ResourceBudget.TotalBudget(_opt.Threads);
        int signalWorkers = ResourceBudget.SignalWorkers(budget);

        Parallel.For(0, total,
            new ParallelOptions { MaxDegreeOfParallelism = signalWorkers, CancellationToken = ct },
            i =>
            {
                if (!open[i])
                    return;

                signals[i] = Signals.ExtractSafe(paths[i]);
                int done = Interlocked.Increment(ref extracted);
                progress?.Invoke(new SorterProgress(done, toOpen, "signals", signals[i].FileName));
            });

        extractSw.Stop();

        // ---- cheap decisions: lots of files settle without ever touching file bytes ----
        var results = new SorterFileResult[total];
        var ocrJobs = new List<OcrJob>();
        var scanSw = Stopwatch.StartNew();

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var job = PrepareOcr(signals[i], i, total, out var settled);
            if (job is null)
            {
                results[i] = settled!;
                progress?.Invoke(new SorterProgress(i + 1, total, "scan", signals[i].FileName,
                    settled!.OcrPages, _opt.MaxOcrPages, settled.Course));
            }
            else
            {
                ocrJobs.Add(job);
            }
        }

        // ---- OCR agents: one shared engine, W bounded page workers, ascending-page commit ----
        double ocrInitSeconds = 0;
        var ocrPhaseSw = Stopwatch.StartNew();

        if (ocrJobs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            int workers = ResourceBudget.OcrWorkers(budget, _opt.Workers);
            int threadsPerPage = ResourceBudget.ThreadsPerPage(budget, workers, 1);

            var first = ocrJobs[0];
            progress?.Invoke(new SorterProgress(first.Index, total, "ocr-load", first.File.FileName,
                0, first.ScanPages));

            var ocr = OcrModelHost.Get(
                _opt.Dpi > 0 ? _opt.Dpi : 200,
                budget,
                ConvertMode.Auto,
                out ocrInitSeconds);

            using (NanoOcr.Core.Parallelism.Use(threadsPerPage))
            {
                bool duplicatePaths = ocrJobs.Select(j => j.File.Path)
                    .Distinct(StringComparer.Ordinal).Count() != ocrJobs.Count;

                if (duplicatePaths)
                {
                    // PredictMany groups by path; keep the old per-file semantics if the caller
                    // passed the same path twice.
                    foreach (var job in ocrJobs)
                    {
                        ct.ThrowIfCancellationRequested();
                        var (pages, error) = PredictOne(ocr, job);
                        Commit(job, pages, error, progress, ct, results);
                    }
                }
                else
                {
                    // Every page of every input is a task on 'workers' single-threaded kernel
                    // workers. Results come back in input order, so the arbiter below sees each
                    // file's pages in strict ascending order.
                    int next = 0;
                    foreach (var (_, pages, error) in ocr.PredictMany(
                                 ocrJobs.Select(j => j.File.Path),
                                 cores: workers,
                                 maxPages: _opt.MaxOcrPages))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (next >= ocrJobs.Count)
                            break;
                        Commit(ocrJobs[next++], pages, error, progress, ct, results);
                    }

                    // Any input PredictMany refused to yield becomes an ordinary OCR failure.
                    while (next < ocrJobs.Count)
                        Commit(ocrJobs[next++], null, new IOException("no OCR result"), progress, ct, results);
                }
            }
        }

        ocrPhaseSw.Stop();
        scanSw.Stop();

        int moved = results.Count(r => r.IsMove);
        int dumped = total - moved;
        int ocrFiles = results.Count(r => r.OcrPages > 0);
        int ocrPagesTotal = results.Sum(r => r.OcrPages);

        // PredictMany finishes a file's pages before yielding them, so a per-file timer would
        // measure only evidence commit. Report the whole bounded OCR stage instead.
        double ocrSeconds = ocrJobs.Count > 0 ? ocrPhaseSw.Elapsed.TotalSeconds : 0;

        var stats = new SorterStats(total, moved, dumped, ocrFiles, ocrPagesTotal,
            extractSw.Elapsed.TotalSeconds, scanSw.Elapsed.TotalSeconds, ocrSeconds, ocrInitSeconds,
            totalSw.Elapsed.TotalSeconds, toOpen);

        return new SorterRunResult(results, stats);
    }

    /// <summary>
    /// Runs the cheap part of the decision. Returns null when OCR is needed, otherwise the final
    /// result. The evidence object is carried forward so OCR commits into the same instance.
    /// </summary>
    private OcrJob? PrepareOcr(FileSignals file, int index, int total, out SorterFileResult? settled)
    {
        settled = null;
        var evidence = new Evidence();

        AddEvidence(evidence, file.FileName, "the file name", "filename", prose: false);
        AddEvidence(evidence, file.ParentFolder, "the folder name", "folder", prose: false);

        bool opened = file.PageCount > 0 || file.FirstPages.Count > 0;

        // A timetable is multi-course by nature. Catching it here also catches the case
        // where its file name was decorated with one course code.
        if (evidence.HasTimetable)
        {
            settled = Dump(file, TimetableReason(evidence), "timetable");
            return null;
        }

        if (!opened)
        {
            if (evidence.HasMultiMarker)
            {
                settled = Dump(file,
                    "looks like a timetable, past questions or a combined pack, " +
                    "and the file could not be opened. Left alone.",
                    "multi");
                return null;
            }

            var unopenedConflict = Conflict(file, evidence, 0, 0);
            if (unopenedConflict is not null)
            {
                settled = unopenedConflict;
                return null;
            }

            if (evidence.Listed.Count == 1)
            {
                var only = evidence.Listed.Values.First();

                // A course code in the file name is explicit. A code only in the folder
                // name is not: this could be a timetable or another course's paper sitting
                // inside that folder, so never move an unreadable file on the folder alone.
                if (evidence.ListedTier[only.Code] == "folder")
                {
                    settled = Dump(file,
                        $"the folder name has a course code, but the file could not be read to verify it. " +
                        "Left alone.", "none");
                    return null;
                }

                settled = MoveFromEvidence(file, evidence, 0, 0);
                return null;
            }

            settled = Dump(file, "the file could not be opened", "none");
            return null;
        }

        AddEvidence(evidence, file.MetadataTitle, "the title", "title");
        AddEvidence(evidence, file.BiggestLine, "the heading", "header");

        for (int p = 0; p < file.FirstPages.Count; p++)
        {
            var text = file.FirstPages[p];

            // Slide paragraphs are separate lines. Treat a newline as a hard break so a
            // heading like "WORK DONE" followed by an item number does not become a fake
            // course code ("DONE 01").
            if (file.IsPresentation)
                text = text.Replace('\n', ';');

            AddEvidence(evidence, text,
                file.IsPresentation ? $"slide {p + 1}" : $"page {p + 1}",
                file.IsPresentation ? "slide" : "text");
        }

        if (evidence.HasTimetable)
        {
            settled = Dump(file, TimetableReason(evidence), "timetable");
            return null;
        }

        var cheapConflict = Conflict(file, evidence, 0, 0);
        if (cheapConflict is not null)
        {
            settled = cheapConflict;
            return null;
        }

        // A PowerPoint file with no slide text cannot be verified. If its only code is
        // the folder name, or its name says it may cover several courses, leave it alone;
        // there is no OCR fallback to check the pictures.
        if (file.IsPresentation && !file.HasAnyText && evidence.Listed.Count == 1)
        {
            var only = evidence.Listed.Values.First();

            if (evidence.ListedTier[only.Code] == "folder")
            {
                settled = Dump(file,
                    "the folder name has a course code, but the PowerPoint file has no text to " +
                    "verify it. Left alone.", "slides");
                return null;
            }

            if (evidence.HasMultiMarker)
            {
                settled = Dump(file,
                    "the name says it may cover more than one course, and the PowerPoint file has " +
                    "no text to check. Left alone.", "multi");
                return null;
            }
        }

        // A multi-course-looking file with one code and no text layer (or with blank
        // pages in the text layer) may still hide other courses in the scans. A PowerPoint
        // file has no OCR fallback, so its slides are all the evidence there is.
        bool mustCheckScans = evidence.Listed.Count == 1 && evidence.HasMultiMarker
            && !file.IsPresentation && NeedsOcr(file);
        if (evidence.Listed.Count == 1 && !mustCheckScans)
        {
            settled = MoveFromEvidence(file, evidence, 0, 0);
            return null;
        }

        // A suggestion is only ever shown beside an untouched file; it never moves or renames.
        CourseSuggestion? suggestion = _opt.SuggestNearMatches
            ? CourseSuggester.Suggest(file, _codes)
            : null;

        if (file.PageCount <= 0)
        {
            settled = Dump(file, "the file could not be opened", "none", suggestion: suggestion);
            return null;
        }

        if (_codes.Count == 0)
        {
            settled = Dump(file, "your course list has no course codes", "none");
            return null;
        }

        if (file.IsPresentation)
        {
            var slides = Math.Min(Math.Max(1, file.PageCount), PptxText.MaxSlides);

            settled = Dump(file,
                file.HasAnyText
                    ? $"no course code in the file name, the title or the first {slides} slides"
                    : "the slides are pictures with no text I can read",
                "slides", 0, 0, suggestion);
            return null;
        }

        int scanPages = Math.Min(Math.Max(1, _opt.MaxOcrPages), file.PageCount);
        return new OcrJob(index, total, file, evidence, suggestion, scanPages);
    }

    private static (IReadOnlyList<OcrResult>? Pages, Exception? Error) PredictOne(NanoOCR ocr, OcrJob job)
    {
        try
        {
            return (ocr.Predict(job.File.Path, maxPages: job.ScanPages).ToList(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex);
        }
    }

    /// <summary>
    /// Decision arbiter: commits page text strictly in ascending page index, using the very same
    /// AddEvidence / Conflict path as the old sequential loop. An early timetable or conflict
    /// stops the file without looking at later pages.
    /// </summary>
    private void Commit(
        OcrJob job,
        IReadOnlyList<OcrResult>? pages,
        Exception? error,
        Action<SorterProgress>? progress,
        CancellationToken ct,
        SorterFileResult[] results)
    {
        var evidence = job.Evidence;
        int pagesRead = 0;
        var ocrStart = Stopwatch.GetTimestamp();

        if (error is not null || pages is null)
        {
            results[job.Index] = Dump(job.File, "the scanned pages could not be read",
                "ocr", 0, 0, job.Suggestion);
            return;
        }

        try
        {
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();

                pagesRead = page.PageIndex + 1;
                progress?.Invoke(new SorterProgress(job.Index + 1, job.Total, "ocr",
                    job.File.FileName, pagesRead, job.ScanPages));

                AddEvidence(evidence, page.Text, $"scanned page {pagesRead}", "ocr");

                if (evidence.HasTimetable)
                {
                    results[job.Index] = Dump(job.File, TimetableReason(evidence), "timetable",
                        pagesRead, Stopwatch.GetElapsedTime(ocrStart).TotalSeconds);
                    return;
                }

                var ocrConflict = Conflict(job.File, evidence, pagesRead,
                    Stopwatch.GetElapsedTime(ocrStart).TotalSeconds);
                if (ocrConflict is not null)
                {
                    results[job.Index] = ocrConflict;
                    return;
                }

                // Exactly one listed code so far: keep reading. A second code on a later
                // page is exactly the multi-course / timetable case this check exists for.
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            results[job.Index] = Dump(job.File, "the scanned pages could not be read",
                "ocr", pagesRead, Stopwatch.GetElapsedTime(ocrStart).TotalSeconds, job.Suggestion);
            return;
        }

        double seconds = Stopwatch.GetElapsedTime(ocrStart).TotalSeconds;

        if (evidence.Listed.Count == 1)
        {
            results[job.Index] = MoveFromEvidence(job.File, evidence, pagesRead, seconds);
            return;
        }

        results[job.Index] = Dump(job.File,
            $"no course code in the file name, the title or the first {job.ScanPages} pages",
            "ocr", pagesRead, seconds, job.Suggestion);
    }

    private void AddEvidence(Evidence evidence, string? text, string where, string tier,
        bool prose = true)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!evidence.HasTimetable && IsTimetable(text))
            evidence.TimetableWhere = where;
        if (!evidence.HasMultiMarker && IsMultiCourse(text))
            evidence.MultiWhere = where;

        foreach (var hit in CourseCodes.Find(text, _codes))
            AddListed(evidence, hit, where, tier);

        if (!_opt.StopOnUnlistedCode)
            return;

        foreach (var code in CourseCodes.FindUnlisted(text, _codes,
                     includeUnknownPrefixes: true, requireUpperUnknown: prose))
        {
            var variant = CourseCodes.MatchVariant(code, _codes);
            if (variant is not null)
            {
                AddListed(evidence, variant, where, tier);
                continue;
            }

            if (!evidence.Foreign.ContainsKey(code))
                evidence.Foreign[code] = where;
        }
    }

    private static void AddListed(Evidence evidence, CourseCode hit, string where, string tier)
    {
        if (evidence.Listed.ContainsKey(hit.Code))
            return;

        evidence.Listed[hit.Code] = hit;
        evidence.ListedWhere[hit.Code] = where;
        evidence.ListedTier[hit.Code] = tier;
    }

    /// <summary>Returns the reason to leave a file alone, or null when it can be filed.</summary>
    private static SorterFileResult? Conflict(
        FileSignals file, Evidence evidence, int ocrPages, double ocrSeconds)
    {
        if (evidence.HasTimetable)
            return Dump(file, TimetableReason(evidence), "timetable", ocrPages, ocrSeconds);

        if (evidence.Listed.Count > 1)
            return Dump(file,
                $"more than one course code ({DescribeListed(evidence)}). Left alone.",
                "multi", ocrPages, ocrSeconds);

        if (evidence.Listed.Count == 1 && evidence.Foreign.Count > 0)
            return Dump(file,
                $"covers more than one course ({DescribeListed(evidence)} and {DescribeForeign(evidence)}). " +
                "Left alone.",
                "multi", ocrPages, ocrSeconds);

        if (evidence.Listed.Count == 0 && evidence.Foreign.Count > 0)
        {
            string skipped = ocrPages == 0 ? " I did not need to look inside." : "";
            return Dump(file,
                $"a course code that is not on your list ({DescribeForeign(evidence)}).{skipped} Left alone.",
                ocrPages == 0 ? "unlisted" : "ocr", ocrPages, ocrSeconds);
        }

        return null;
    }

    private static SorterFileResult MoveFromEvidence(
        FileSignals file, Evidence evidence, int ocrPages, double ocrSeconds)
    {
        var hit = evidence.Listed.Values.First();
        string where = evidence.ListedWhere[hit.Code];
        string tier = evidence.ListedTier[hit.Code];
        string reason = $"code {Upper(hit)} in {where}";
        if (ocrPages > 0 && tier != "ocr")
            reason += " (confirmed by OCR)";

        return Move(file, hit, reason, tier, ocrPages, ocrSeconds);
    }

    private static bool NeedsOcr(FileSignals file)
    {
        if (!file.HasTextLayer || file.FirstPages.Count == 0)
            return true;

        return file.FirstPages.Any(p => p.Trim().Length < 20);
    }

    private static string TimetableReason(Evidence evidence)
        => $"looks like a timetable or schedule ({evidence.TimetableWhere}). " +
           "Left alone because it covers several courses.";

    private static string DescribeListed(Evidence evidence, int max = 6)
    {
        var parts = evidence.Listed.Values.Take(max)
            .Select(c => $"{Upper(c)} ({evidence.ListedWhere[c.Code]})")
            .ToList();

        if (evidence.Listed.Count > max)
            parts.Add($"+{evidence.Listed.Count - max} more");

        return string.Join(", ", parts);
    }

    private static string DescribeForeign(Evidence evidence, int max = 6)
    {
        var parts = evidence.Foreign.Take(max)
            .Select(kv => $"{kv.Key} ({kv.Value})")
            .ToList();

        if (evidence.Foreign.Count > max)
            parts.Add($"+{evidence.Foreign.Count - max} more");

        return string.Join(", ", parts);
    }

    private static bool IsTimetable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string letters = LettersOnly(text);
        return letters.Contains("timetable", StringComparison.Ordinal)
            || letters.Contains("examschedule", StringComparison.Ordinal)
            || letters.Contains("examinationschedule", StringComparison.Ordinal)
            || letters.Contains("lectureschedule", StringComparison.Ordinal)
            || letters.Contains("classschedule", StringComparison.Ordinal)
            || letters.Contains("scheduleoflectures", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"(?<![A-Za-z])schedule(?![A-Za-z])", RegexOptions.IgnoreCase);
    }

    private static bool IsMultiCourse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string letters = LettersOnly(text);
        return letters.Contains("pq", StringComparison.Ordinal)
            || letters.Contains("pastquestion", StringComparison.Ordinal)
            || letters.Contains("questionbank", StringComparison.Ordinal)
            || letters.Contains("questionpaper", StringComparison.Ordinal)
            || letters.Contains("exampaper", StringComparison.Ordinal)
            || letters.Contains("testpaper", StringComparison.Ordinal)
            || letters.Contains("multicourse", StringComparison.Ordinal)
            || letters.Contains("multiplecourses", StringComparison.Ordinal)
            || letters.Contains("allcourses", StringComparison.Ordinal)
            || letters.Contains("allpq", StringComparison.Ordinal)
            || letters.Contains("combined", StringComparison.Ordinal)
            || letters.Contains("compilation", StringComparison.Ordinal)
            || letters.Contains("merged", StringComparison.Ordinal)
            || letters.Contains("bundle", StringComparison.Ordinal)
            || letters.Contains("collection", StringComparison.Ordinal)
            || letters.Contains("calendar", StringComparison.Ordinal);
    }

    private static string LettersOnly(string text)
        => new(text.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    private sealed class Evidence
    {
        public Dictionary<string, CourseCode> Listed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ListedWhere { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ListedTier { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Foreign { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? TimetableWhere { get; set; }
        public string? MultiWhere { get; set; }
        public bool HasTimetable => TimetableWhere is not null;
        public bool HasMultiMarker => MultiWhere is not null;
    }

    private static SorterFileResult Move(FileSignals file, CourseCode hit, string reason, string tier,
        int ocrPages = 0, double ocrSeconds = 0)
        => new(file, PlanAction.Move, hit.Course, hit.Code, reason, tier, ocrPages, ocrSeconds);

    private static SorterFileResult Dump(FileSignals file, string reason, string tier,
        int ocrPages = 0, double ocrSeconds = 0, CourseSuggestion? suggestion = null)
        => new(file, PlanAction.Dump, null, null, reason, tier, ocrPages, ocrSeconds, suggestion);

    private static string Upper(CourseCode code)
    {
        string raw = code.Code.ToUpperInvariant();
        int split = 0;
        while (split < raw.Length && char.IsLetter(raw[split]))
            split++;

        return split > 0 && split < raw.Length ? raw[..split] + " " + raw[split..] : raw;
    }
}
