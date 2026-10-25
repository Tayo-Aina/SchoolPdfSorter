using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using SchoolPdfSorter.Engine;

namespace SchoolPdfSorter.ViewModels;

/// <summary>
/// Drives the whole conversation: source folder, course list, target folder, a
/// strict course-code pass over the PDFs, a plan review, the move, and a compact
/// list of files left untouched. Every prompt is awaited, so the flow reads
/// top-to-bottom like the script it is.
/// </summary>
public sealed class ChatSession
{
    private const string CancelAnswer = "\u0001cancel";

    private readonly ChatViewModel _vm;
    private readonly List<List<MoveRecord>> _undoStack = new();

    private TaskCompletionSource<string>? _pending;
    private bool _pendingAcceptsText;
    private bool _pendingAcceptsFile;
    private bool _running;
    private bool _restartRequested;
    private CancellationTokenSource? _cts;

    private List<string> _courses = new();

    /// <summary>A newer build found by the background check, if any.</summary>
    private UpdateCheck? _pendingUpdate;
    private bool _updateCheckStarted;

    /// <summary>True while PDFs are being read, OCR'd or moved — never restart an update then.</summary>
    private bool _busy;

    public ChatSession(ChatViewModel vm) => _vm = vm;

    /// <summary>True while a prompt is waiting for something the user can type.</summary>
    public bool AcceptsTypedAnswer => _pending is not null && _pendingAcceptsText;

    /// <summary>True while the prompt can take the path of a course-structure file.</summary>
    public bool AcceptsStructureFile => _pending is not null && _pendingAcceptsFile;

    private static string DownloadsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string DocumentsDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private sealed record MoveRecord(string From, string To);

    // ================================================================== entry

    public void StartFlow()
    {
        if (_running)
        {
            _vm.Add("A run is already going. Press Cancel run or type /cancel to stop it.", ChatRole.System);
            return;
        }

        _ = RunGuardedAsync();
    }

    private async Task RunGuardedAsync()
    {
        _running = true;
        _cts = new CancellationTokenSource();
        _vm.CancelEnabled = true;

        try
        {
            await RunFlowAsync();
        }
        catch (OperationCanceledException)
        {
            if (!_restartRequested)
                _vm.Bot("Cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            _vm.Add($"Something went wrong: {ex.Message}", ChatRole.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pending = null;
            _pendingAcceptsText = false;
            _pendingAcceptsFile = false;
            _vm.ClearProgress();
            _vm.StepLabel = "Ready";
            _running = false;
            _vm.CancelEnabled = false;

            if (_restartRequested)
            {
                _restartRequested = false;
                StartFlow();
            }
        }
    }

    private async Task RunFlowAsync()
    {
        var token = _cts!.Token;

        _vm.Bot(Greeting());

        // ------------------------------------------------------------ step 1
        _vm.StepLabel = "Step 1 of 3 · Folder with your PDFs";

        (string Label, string Value)[] step1Options = Directory.Exists(DownloadsDirectory)
            ? new[] { ("My Downloads folder", DownloadsDirectory) }
            : Array.Empty<(string Label, string Value)>();

        var sourceDir = await AskDirectoryAsync(
            "Step 1 of 3: Source folder\nPaste the folder that has your school PDFs, or use a quick option below.",
            "Enter a folder that exists (you can also paste the path of a file inside it):",
            mustExist: true,
            step1Options);

        _vm.Add($"Source: {sourceDir}", ChatRole.System);

        // ------------------------------------------------------------ step 2
        _vm.StepLabel = "Step 2 of 3 · Course list";

        Sorter sorter;
        while (true)
        {
            _courses = await AskCoursesAsync(token);
            sorter = new Sorter(_courses);

            if (sorter.Codes.Count > 0)
                break;

            _vm.Add(
                "None of those courses contains a course code, so nothing could ever match. " +
                "Include the code in each course, e.g. \"MTH 101 Elementary Mathematics I\".",
                ChatRole.Error);
        }

        if (sorter.CoursesWithoutCode.Count > 0)
        {
            _vm.Add(
                "These courses have no code in their name, so files can't be matched to them:\n" +
                string.Join("\n", sorter.CoursesWithoutCode.Select(c => "    " + c)) +
                "\nAdd the code (e.g. \"MTH 101 …\") and run again if you want them included.",
                ChatRole.Warning);
        }

        // ------------------------------------------------------------ step 3
        _vm.StepLabel = "Step 3 of 3 · Where should the folders go?";

        var step3Options = new List<(string Label, string Value)>
        {
            ("Create inside the source folder", sourceDir)
        };

        if (Directory.Exists(DocumentsDirectory))
            step3Options.Add(("My Documents", DocumentsDirectory));

        var targetDir = await AskDirectoryAsync(
            "Step 3 of 3: Target folder\nWhere should the course folders be created? A folder is made for each course inside it.",
            "Enter a target folder (its parent folder must already exist):",
            mustExist: false,
            step3Options.ToArray());

        if (!Directory.Exists(targetDir))
            _vm.Add($"Target folder will be created: {targetDir}", ChatRole.System);

        // ----------------------------------------------------------- scanning
        _vm.StepLabel = "Looking for PDFs";
        _vm.Bot(
            $"Looking for PDF files in:\n{sourceDir}\n" +
            (_vm.IncludeSubfolders ? "including subfolders" : "this folder only (subfolders are off)"));

        var scan = PdfScanner.Scan(sourceDir, targetDir, _vm.IncludeSubfolders, _courses);
        var files = scan.Files;

        if (files.Count == 0)
        {
            if (scan.SortedFolders.Count > 0)
                _vm.Add(
                    $"No new PDFs to sort in {sourceDir}; " +
                    $"{scan.SortedFolders.Count} course folder(s) already contain sorted files.",
                    ChatRole.System);
            else
                _vm.Add($"No PDFs found in {sourceDir}.", ChatRole.Warning);
            _vm.Bot("You can point me somewhere else, or start over.");
            var again = await AskAsync("What would you like to do?", false,
                ("Choose another folder", "restart"),
                ("Show commands", "help"));

            if (again == "restart")
                _restartRequested = true;
            else
                PrintHelp();

            return;
        }

        _vm.Add($"Found {files.Count} PDF(s).", ChatRole.Success);
        token.ThrowIfCancellationRequested();

        // The files themselves can name courses the list is missing — a code the structure scan
        // mangled, or a course from another level. Offered before any reading happens, so the
        // course list is settled first.
        sorter = await OfferMissingFileCodesAsync(sorter, files, token);

        // ------------------------------------------------------ strict matching
        _vm.StepLabel = "Matching course codes";

        var cache = ScanCache.Load();
        var cached = cache.Unchanged(sourceDir, _courses, files);
        var fresh = files
            .Where(file => !cached.ContainsKey(file.Path))
            .Select(file => file.Path)
            .ToList();

        var intro = cached.Count == 0
            ? $"Checking {files.Count} PDF(s) for a course code — offline, on this machine.\n"
            : $"Checking {fresh.Count} new or changed PDF(s) for a course code — offline, on this machine.\n" +
              $"{cached.Count} unchanged file(s) were already checked and are skipped.\n";

        _vm.Bot(
            intro +
            "Name and folder first; a different course code there is skipped without opening the PDF. " +
            "Otherwise I read the title and first 5 pages' text, then OCR the first 5 pages of scans. " +
            "Timetables are always left alone. A file moves only when exactly one of your courses is " +
            "identified and no other course code appears in those pages; otherwise it stays where it is.");

        IProgress<SorterProgress> progress = new Progress<SorterProgress>(p =>
        {
            switch (p.Phase)
            {
                case "signals":
                    _vm.SetProgress(p.Total == 0 ? 0 : p.Done * 25.0 / p.Total,
                        $"Reading PDF {p.Done} of {p.Total}…");
                    break;
                case "ocr-load":
                    _vm.SetProgress(25, $"Loading the built-in OCR models — first scan: {p.FileName}");
                    break;
                case "ocr":
                    _vm.SetProgress(25 + p.Done * 75.0 / p.Total,
                        $"OCR — {p.Done + 1} of {p.Total}, page {p.Page} of {p.Pages}…");
                    break;
                case "scan":
                    _vm.SetProgress(25 + p.Done * 75.0 / p.Total,
                        $"Checked {p.Done} of {p.Total}…");
                    break;
            }
        });

        SorterRunResult freshResult;

        _busy = true;

        try
        {
            freshResult = await Task.Run(() => sorter.Run(fresh, progress.Report, token), token);
        }
        finally
        {
            _busy = false;
        }

        _vm.ClearProgress();
        token.ThrowIfCancellationRequested();

        var result = MergeCachedDumps(freshResult,
            cached.Select(hit => CachedDumpResult(hit.Key, hit.Value)).ToList());

        // Save the left-alone decisions before the plan prompt. If the user cancels, the
        // next run still does not have to reopen and re-OCR every unchanged file.
        var entries = scan.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        cache.Remember(sourceDir, _courses,
            result.Dumps
                .Where(dump => entries.ContainsKey(dump.File.Path))
                .Select(dump =>
                {
                    var entry = entries[dump.File.Path];
                    return (entry, ScanCache.FromResult(entry, dump));
                }));

        var moves = result.Moves.ToList();
        var dumps = result.Dumps.ToList();

        // --------------------------------------------------------------- plan
        _vm.StepLabel = "Review the plan";
        _vm.Bot(BuildPlanSummary(result, targetDir));

        var moved = 0;
        var failed = 0;

        if (moves.Count > 0)
        {
            var decision = await AskAsync(
                "Ready? Nothing on disk changes until you approve the plan.",
                false,
                ($"Move {moves.Count} file(s)", "apply"),
                ("Cancel", "cancel"));

            if (decision != "apply")
            {
                _vm.Bot("Plan discarded. Nothing was changed.");
                _restartRequested = true;
                return;
            }

            (moved, failed) = await ExecuteMovesAsync(moves, targetDir, token);
        }

        // -------------------------------------------------------- leftovers
        _vm.StepLabel = "Files left where they were";
        ShowLeftovers(dumps);

        // ------------------------------------------------------------- finish
        _vm.StepLabel = "Done";

        var summary = new StringBuilder();
        summary.Append($"Done. Filed {moved} of {result.Items.Count} PDF(s)");
        summary.Append($" in {result.Stats.TotalSeconds:0.0}s.");

        if (dumps.Count > 0)
            summary.Append($" {dumps.Count} left in place.");
        if (failed > 0)
            summary.Append($" {failed} failed.");

        summary.AppendLine();
        summary.Append("Folders: ").Append(targetDir);

        if (result.Stats.OcrFiles > 0)
        {
            summary.AppendLine();
            summary.Append($"OCR ran on {result.Stats.OcrFiles} file(s) — {result.Stats.OcrPages} page(s), ");
            summary.Append($"{result.Stats.OcrSeconds:0.0}s of OCR.");
        }

        if (result.Stats.OpenedFiles < result.Items.Count)
        {
            summary.AppendLine();
            summary.Append($"Opened {result.Stats.OpenedFiles} of {result.Items.Count} PDF(s); ");
            summary.Append("the rest were already checked or had a course code in the file name.");
        }

        if (moved > 0)
            summary.AppendLine().Append("Type /undo to revert the last batch of moves.");

        _vm.Add(summary.ToString(), failed > 0 ? ChatRole.Warning : ChatRole.Success);

        while (true)
        {
            var options = new List<(string Label, string Value)>
            {
                ("File another folder", "restart")
            };

            if (_undoStack.Count > 0)
                options.Insert(0, ("Undo the last batch", "undo"));

            if (_pendingUpdate is { } offered)
                options.Insert(0, ($"Update to {offered.Version}", "update"));

            var next = await AskAsync("What's next?", false, options.ToArray());

            if (next == "restart")
            {
                _restartRequested = true;
                return;
            }

            if (next == "update" && _pendingUpdate is { } pending)
            {
                ShowUpdateOffer(pending);
                continue;
            }

            DoUndo();
            _vm.Bot("Reverted. Anything else?");
        }
    }

    // ================================================================ updates

    /// <summary>
    /// A quiet startup check: at most once a day, never blocking, and it only posts a line when
    /// a newer build exists. Offline means no line at all — the app never needs the network.
    /// </summary>
    public void StartUpdateCheck()
    {
        if (_updateCheckStarted)
            return;

        _updateCheckStarted = true;

        var settings = UpdateFeed.LoadSettings();
        var feed = UpdateFeed.ResolveFeedUrl(settings);
        var now = DateTimeOffset.UtcNow;

        if (!UpdateFeed.ShouldCheck(settings, feed, now))
            return;

        _ = Task.Run(async () =>
        {
            var check = await UpdateFeed.CheckAsync(feed, UpdateFeed.CurrentVersion());

            settings = UpdateFeed.LoadSettings();
            settings.LastCheckUtc = now;
            UpdateFeed.SaveSettings(settings);

            if (check is null ||
                string.Equals(settings.SkippedVersion, check.Manifest.Version,
                    StringComparison.OrdinalIgnoreCase))
                return;

            _pendingUpdate = check;

            OnUiThread(() => _vm.Add(
                $"Update {check.Version} is available ({check.SizeText}). Type /update to install it.",
                ChatRole.System));
        });
    }

    /// <summary>Checks now (if the startup check has not found one yet) and offers it.</summary>
    private async Task CheckAndOfferUpdateAsync()
    {
        if (_busy)
        {
            _vm.Add("Let me finish this run first, then ask again.", ChatRole.System);
            return;
        }

        var check = _pendingUpdate;

        if (check is null)
        {
            var settings = UpdateFeed.LoadSettings();
            var feed = UpdateFeed.ResolveFeedUrl(settings);

            if (!UpdateFeed.IsConfigured(feed))
            {
                _vm.Add("This build has no update feed configured, so updates are off.", ChatRole.System);
                return;
            }

            _vm.SetProgress(0, "Checking for updates…", "");

            try
            {
                check = await UpdateFeed.CheckAsync(feed, UpdateFeed.CurrentVersion());
            }
            finally
            {
                _vm.ClearProgress();
            }

            settings = UpdateFeed.LoadSettings();
            settings.LastCheckUtc = DateTimeOffset.UtcNow;
            UpdateFeed.SaveSettings(settings);

            if (check is null)
            {
                _vm.Add($"You're on the newest version ({UpdateFeed.CurrentVersion().ToString(3)}).",
                    ChatRole.Success);
                return;
            }

            _pendingUpdate = check;
        }

        ShowUpdateOffer(check);
    }

    /// <summary>
    /// The offer is plain message buttons rather than a prompt: an update can be offered while
    /// another prompt is waiting, and it must not steal that prompt's answer.
    /// </summary>
    private void ShowUpdateOffer(UpdateCheck check)
    {
        var text = new StringBuilder($"Update {check.Version} is available ({check.SizeText}).");

        if (!string.IsNullOrWhiteSpace(check.Manifest.Notes))
            text.Append('\n').Append(check.Manifest.Notes.Trim());

        text.AppendLine().AppendLine();
        text.Append("I download it, check its SHA-256, replace this copy and restart. The current\n")
            .Append("build is kept as a .previous.exe, so /rollback can undo it.");

        var message = _vm.Bot(text.ToString());

        AddOfferReply(message, "Update now", () => _ = InstallUpdateAsync(check));
        AddOfferReply(message, "Later", () => _vm.Add("Postponed. Type /update whenever you want it.", ChatRole.System));
        AddOfferReply(message, "Skip this version", () => SkipUpdateVersion(check));
    }

    private void AddOfferReply(ChatMessage message, string label, Action action)
    {
        var reply = new QuickReply(label, label);
        reply.Command = new RelayCommand(() =>
        {
            message.ClearReplies();
            _vm.User(label);
            action();
        });

        message.AddReply(reply);
    }

    private void SkipUpdateVersion(UpdateCheck check)
    {
        var settings = UpdateFeed.LoadSettings();
        settings.SkippedVersion = check.Manifest.Version;
        UpdateFeed.SaveSettings(settings);

        if (_pendingUpdate?.Manifest.Version == check.Manifest.Version)
            _pendingUpdate = null;

        _vm.Add($"Okay — I won't offer {check.Version} again. /update still installs it on request.",
            ChatRole.System);
    }

    private async Task InstallUpdateAsync(UpdateCheck check)
    {
        if (_busy)
        {
            _vm.Add("Let me finish this run first, then ask again.", ChatRole.System);
            return;
        }

        _vm.StepLabel = $"Updating to {check.Version}";
        _vm.SetProgress(0, $"Downloading {check.Version}…", "");

        try
        {
            IProgress<double> progress = new Progress<double>(fraction =>
                _vm.SetProgress(fraction * 100, $"Downloading {check.Version}…", $"{fraction:P0}"));

            var staged = await UpdateFeed.DownloadAsync(check.Manifest, progress);

            UpdateFeed.CleanStaging(check.Manifest.Version);
            _vm.ClearProgress();

            var exe = Environment.ProcessPath;
            string? error = null;

            if (string.IsNullOrEmpty(exe) || !UpdateFeed.TrySwap(exe, staged, out _, out error))
            {
                _vm.Add($"I couldn't install it: {error ?? "I don't know where this app lives."}", ChatRole.Error);
                return;
            }

            _pendingUpdate = null;
            _vm.Add($"Installed {check.Version}. Restarting — /rollback undoes it if needed.", ChatRole.Success);
            _vm.StepLabel = "Restarting…";

            await Task.Delay(500);
            Restart(exe);
        }
        catch (Exception ex)
        {
            _vm.ClearProgress();
            _vm.Add($"The update failed: {ex.Message}", ChatRole.Error);
        }
    }

    /// <summary>Puts the saved previous build back and restarts it. False when there is none.</summary>
    public async Task<bool> TryRollbackAsync()
    {
        if (_busy)
        {
            _vm.Add("Let me finish this run first, then ask again.", ChatRole.System);
            return false;
        }

        var exe = Environment.ProcessPath;
        string? error = null;

        if (string.IsNullOrEmpty(exe) || !UpdateFeed.TryRestore(exe, out error))
        {
            _vm.Add(error ?? "There's no previous build to go back to.", ChatRole.Error);
            return false;
        }

        _vm.Add("Restored the previous build. Restarting…", ChatRole.Success);
        await Task.Delay(500);
        Restart(exe);

        return true;
    }

    private static void Restart(string exe)
    {
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        });

        Application.Current?.Shutdown();
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    // ================================================================ courses

    private async Task<List<string>> AskCoursesAsync(CancellationToken token)
    {
        var current =
            "Step 2 of 3: Your course list\n" +
            "Paste or type your courses — one per line, or separated by semicolons.\n" +
            "Or paste a whole course structure (a portal page or an exported table), or use Browse…\n" +
            "to pick / drop a PDF or text file that holds one, and I will read the courses out of it.\n" +
            "Example: ECON 301 Microeconomics; GST 102 Communication in English";

        while (true)
        {
            // The Browse button should offer a file picker while this prompt is up.
            _pendingAcceptsFile = true;
            string typed;

            try
            {
                typed = await AskAsync(current, true);
            }
            finally
            {
                _pendingAcceptsFile = false;
            }

            token.ThrowIfCancellationRequested();

            var reading = await ReadCoursesAsync(typed, token);

            if (reading.Labels.Count == 0)
            {
                _vm.Add("I couldn't read any courses from that. Try something like: ECO 301 Macroeconomics; MTH 102 Calculus", ChatRole.Error);
                current = "Enter your course list:";
                continue;
            }

            if (reading.Note.Length > 0)
                _vm.Add(reading.Note, ChatRole.System);

            _vm.Add(BuildCourseListMessage(reading.Labels), ChatRole.System);

            var options = new List<(string Label, string Value)>
            {
                ($"Yes, {reading.Labels.Count} course(s)", "ok")
            };

            if (reading.FromStructure)
                options.Add(($"Yes, but folder names as codes only ({reading.CodesOnly.Count})", "codes"));

            options.Add(("Enter them again", "retry"));

            var confirm = await AskAsync("Use these courses?", false, options.ToArray());

            if (confirm == "ok")
                return reading.Labels;

            if (confirm == "codes")
                return reading.CodesOnly;

            current = "Enter your course list:";
        }
    }

    /// <summary>
    /// The course list for the transcript. A structure can hold a hundred courses, so a long
    /// list is capped and /courses is offered for the rest.
    /// </summary>
    private static string BuildCourseListMessage(IReadOnlyList<string> labels)
    {
        var sb = new StringBuilder();
        sb.Append("Courses (").Append(labels.Count).AppendLine("):");

        const int shown = 30;

        foreach (var label in labels.Take(shown))
            sb.Append("    ").AppendLine(label);

        if (labels.Count > shown)
            sb.Append("    … and ").Append(labels.Count - shown)
              .AppendLine(" more — type /courses to see the whole list.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>What the course prompt produced: labels to sort by, plus how they were obtained.</summary>
    private sealed record CourseReading(
        List<string> Labels,
        List<string> CodesOnly,
        string Note,
        bool FromStructure);

    /// <summary>
    /// Reads the course prompt three ways: as a file holding a course structure, as a pasted
    /// structure dump, or as a hand-typed course list.
    /// </summary>
    private async Task<CourseReading> ReadCoursesAsync(string typed, CancellationToken token)
    {
        var text = typed;
        var note = new StringBuilder();

        var file = TryResolveStructureFile(typed);

        if (file is not null)
        {
            _vm.Bot($"Reading your course structure:\n{file}");

            try
            {
                IProgress<string> reading = new Progress<string>(stage =>
                    _vm.SetProgress(50, $"Reading your course structure — {stage}"));

                text = await Task.Run(
                    () => CourseStructureDocument.ReadText(file, reading.Report, token), token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _vm.Add($"I could not read that file: {ex.Message}", ChatRole.Error);
                return new CourseReading(new List<string>(), new List<string>(), "", false);
            }
            finally
            {
                _vm.ClearProgress();
            }
        }

        var parsed = CourseStructure.ParseDetailed(text);

        // A file handed to the course prompt is there to be read as a course list; pasted text
        // is only treated as a structure when it carries a structure's shape, so a hand-typed
        // list keeps its exact wording.
        var asStructure = parsed.Courses.Count >= 3 && (file is not null || CourseStructure.LooksLikeStructure(text));

        if (asStructure)
            return FromStructure(parsed, file is not null, note);

        // A hand-typed list (or a plain one-line-per-course file that is not a structure table).
        var source = file is not null ? text : typed;

        var courses = source
            .Split(new[] { ';', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim(',', '.'))
            .Where(part => part.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CourseReading(courses, courses, "", false);
    }

    private static CourseReading FromStructure(CourseStructureResult parsed, bool fromFile, StringBuilder note)
    {
        note.Append(fromFile
            ? $"Read {parsed.Courses.Count} course(s) from that file."
            : $"Read {parsed.Courses.Count} course(s) from your course structure.");

        if (parsed.Repaired.Count > 0)
        {
            note.Append("\n\nCodes I had to correct from the scan (")
                .Append(parsed.Repaired.Count).AppendLine("):");

            foreach (var course in parsed.Repaired.Take(20))
                note.Append("    ").AppendLine(course.Label);

            if (parsed.Repaired.Count > 20)
                note.Append("    … and ").Append(parsed.Repaired.Count - 20).AppendLine(" more");
        }

        if (parsed.Suspects.Count > 0)
        {
            note.AppendLine().AppendLine("Please check these — they may be scan misreads:");

            foreach (var suspect in parsed.Suspects.Take(12))
                note.Append("    ").Append(suspect.Label).Append(" (").Append(suspect.Why).Append(')')
                    .AppendLine();

            note.AppendLine().AppendLine("Nothing moves that I am not sure about, so a wrong code here only means");
            note.AppendLine("that course's files stay where they are.");
        }

        return new CourseReading(
            parsed.Courses.Select(course => course.Label).ToList(),
            parsed.Courses.Select(CodeOnly).ToList(),
            note.ToString(),
            true);
    }

    /// <summary>"EIE 326 Signals and Systems" → "EIE 326", for a folder named by code alone.</summary>
    private static string CodeOnly(ParsedCourse course)
    {
        var parts = course.Label.Split(' ', 3);
        return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : course.Label;
    }

    /// <summary>
    /// The course prompt accepts the path of a structure file — typed, pasted, or dropped onto
    /// the window. Anything multiline is a pasted dump, not a path.
    /// </summary>
    private static string? TryResolveStructureFile(string raw)
    {
        var value = raw.Trim().Trim('"');

        if (value.Length == 0 || value.Length > 512 ||
            value.Contains('\n') || value.Contains(';') || value.Contains('|'))
            return null;

        if (value.StartsWith("~"))
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value[1..].TrimStart('\\', '/'));

        return CourseStructureDocument.IsSupportedFile(value) ? Path.GetFullPath(value) : null;
    }

    /// <summary>
    /// Asks whether to add courses that the user's own file names mention but their list lacks.
    /// Only offered for codes seen in two or more files; a single file is left to the leftovers
    /// list. Adding a course never loosens the rule — a file still moves only when exactly one
    /// listed course is identified in it and no other course code appears.
    /// </summary>
    private async Task<Sorter> OfferMissingFileCodesAsync(
        Sorter sorter, IReadOnlyList<PdfScanEntry> files, CancellationToken token)
    {
        var listed = new HashSet<string>(sorter.Codes.Select(c => c.Code), StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var affected = 0;

        foreach (var file in files)
        {
            var codes = CourseCodes.FindCourseLikeCodes(Path.GetFileNameWithoutExtension(file.Path))
                .Where(code => !listed.Contains(CourseCodes.Normalize(code)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (codes.Count == 0)
                continue;

            affected++;

            foreach (var code in codes)
                counts[code] = counts.GetValueOrDefault(code) + 1;
        }

        var missing = counts.Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
            return sorter;

        var sb = new StringBuilder();
        sb.Append(affected).Append(" of these files name a course that is not on your list:\n");

        foreach (var (code, count) in missing.Take(12))
            sb.Append("    ").Append(code).Append(" — ").Append(count).AppendLine(count == 1 ? " file" : " files");

        if (missing.Count > 12)
            sb.Append("    … and ").Append(missing.Count - 12).AppendLine(" more");

        sb.AppendLine().Append("Shall I add them to the course list? A file still moves only when exactly one\n")
          .Append("of your courses is identified in it and no other course code appears.");

        token.ThrowIfCancellationRequested();

        var answer = await AskAsync(sb.ToString(), false,
            ($"Yes, add {missing.Count} course(s)", "add"),
            ("No, keep the list as it is", "keep"));

        if (answer != "add")
            return sorter;

        _courses = _courses.Concat(missing.Select(kv => kv.Key)).ToList();

        _vm.Add($"Added {missing.Count} course(s) from your file names: " +
                string.Join(", ", missing.Select(kv => kv.Key)), ChatRole.System);

        return new Sorter(_courses);
    }

    // ================================================================ results

    /// <summary>
    /// Adds the unchanged "left alone" files back into the result so the plan and the final
    /// list look exactly as they would after a full scan. They are not opened or OCR'd again.
    /// </summary>
    private static SorterRunResult MergeCachedDumps(
        SorterRunResult fresh, IReadOnlyList<SorterFileResult> cached)
    {
        if (cached.Count == 0)
            return fresh;

        var items = fresh.Items.Concat(cached).ToList();
        var stats = fresh.Stats with
        {
            Total = items.Count,
            Dumped = fresh.Stats.Dumped + cached.Count
        };

        return new SorterRunResult(items, stats);
    }

    private static SorterFileResult CachedDumpResult(string path, CachedDump dump)
        => new(Signals.Light(path), PlanAction.Dump, null, null, dump.Reason, dump.Tier, 0, 0,
            dump.Suggestion);

    // ============================================================== execution

    private async Task<(int moved, int failed)> ExecuteMovesAsync(
        List<SorterFileResult> moves, string targetDir, CancellationToken token)
    {
        _vm.StepLabel = "Filing your PDFs";
        _vm.SetProgress(0, $"Filing {moves.Count} file(s)…");

        var undoBatch = new List<MoveRecord>();
        var errors = new List<string>();
        var moved = 0;
        var failed = 0;

        IProgress<int> progress = new Progress<int>(done =>
            _vm.SetProgress(done * 100.0 / moves.Count, $"Filing {done} of {moves.Count}…"));

        _busy = true;

        try
        {
            await Task.Run(() =>
            {
                for (var i = 0; i < moves.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var item = moves[i];

                    try
                    {
                        var destination = FileFiler.MoveInto(item, targetDir);
                        undoBatch.Add(new MoveRecord(item.File.Path, destination));
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"{item.File.FileName}: {ex.Message}");
                    }

                    progress.Report(i + 1);
                }
            }, token);
        }
        finally
        {
            _busy = false;
        }

        _vm.ClearProgress();

        foreach (var error in errors.Take(5))
            _vm.Add(error, ChatRole.Error);

        if (undoBatch.Count > 0)
            _undoStack.Add(undoBatch);

        return (moved, failed);
    }

    /// <summary>One compact list of every file the strict rule left untouched.</summary>
    private void ShowLeftovers(List<SorterFileResult> dumps)
    {
        if (dumps.Count == 0)
        {
            _vm.Add("Every PDF had exactly one course code. Nothing was left behind.", ChatRole.Success);
            return;
        }

        var sb = new StringBuilder();
        sb.Append("Left untouched — ").Append(dumps.Count).AppendLine(" file(s), still in their original folders:");
        sb.AppendLine();

        foreach (var item in dumps.OrderBy(i => i.File.FileName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("    ").Append(item.File.FileName).Append(" — ").Append(item.Reason);

            if (item.Suggestion is { } suggestion)
                sb.Append(" | looks like ").Append(suggestion.Course);

            sb.AppendLine();
        }

        if (dumps.Any(d => d.Suggestion is not null))
            sb.AppendLine().Append("Suggestions are notes only; no file is renamed or moved by a suggestion.");

        _vm.Add(sb.ToString().TrimEnd(), ChatRole.Warning);
    }

    private void DoUndo()
    {
        if (_undoStack.Count == 0)
        {
            _vm.Add("There's nothing left to undo.", ChatRole.System);
            return;
        }

        var batch = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        var restored = 0;

        foreach (var record in batch)
        {
            try
            {
                if (!File.Exists(record.To))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(record.From)!);
                File.Move(record.To, record.From);
                restored++;

                // Clean up a course folder that's now empty.
                try
                {
                    var directory = Path.GetDirectoryName(record.To)!;
                    if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                        Directory.Delete(directory, recursive: false);
                }
                catch
                {
                    // folder busy or kept on purpose: leave it
                }
            }
            catch
            {
                // file moved/renamed in the meantime: skip it
            }
        }

        _vm.Add($"Reverted {restored} file(s).", restored > 0 ? ChatRole.Success : ChatRole.System);
    }

    // ================================================================= prompts

    private async Task<string> AskDirectoryAsync(
        string prompt,
        string retryPrompt,
        bool mustExist,
        params (string Label, string Value)[] options)
    {
        var current = prompt;

        while (true)
        {
            var typed = await AskAsync(current, true, options);

            if (TryResolveDirectory(typed, mustExist, out var directory, out var adjusted, out var error))
            {
                if (adjusted)
                    _vm.Add($"Using: {directory}", ChatRole.System);

                return directory;
            }

            _vm.Add(error!, ChatRole.Error);
            current = retryPrompt;
        }
    }

    /// <summary>Posts a prompt and suspends the flow until a quick reply or typed answer arrives.</summary>
    private async Task<string> AskAsync(string prompt, bool acceptsText, params (string Label, string Value)[] options)
    {
        // Every prompt carries a Cancel button so the user can bail out from anywhere —
        // skipped only when the caller already provides its own (the plan prompt does).
        if (!options.Any(o => string.Equals(o.Label, "Cancel", StringComparison.OrdinalIgnoreCase)))
            options = options.Append(("Cancel", CancelAnswer)).ToArray();

        var message = _vm.Bot(prompt);

        foreach (var (label, value) in options)
        {
            var reply = new QuickReply(label, value);
            reply.Command = new RelayCommand(() => OnQuickReply(message, reply));
            message.AddReply(reply);
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        _pendingAcceptsText = acceptsText;

        try
        {
            var answer = await tcs.Task;

            if (answer == CancelAnswer)
                throw new OperationCanceledException();

            return answer;
        }
        finally
        {
            if (ReferenceEquals(_pending, tcs))
            {
                _pending = null;
                _pendingAcceptsText = false;
                _pendingAcceptsFile = false;
            }

            message.ClearReplies();
        }
    }

    // =================================================================== input

    public void OnTextInput(string text)
    {
        text = text.Trim();

        if (text.Length == 0)
            return;

        if (TryHandleCommand(text))
            return;

        if (_pending is { } pending && _pendingAcceptsText)
        {
            _vm.User(Shorten(text));
            pending.TrySetResult(text);
            return;
        }

        _vm.User(Shorten(text));

        if (_pending is not null)
            _vm.Add("Please use one of the options above (or type /help).", ChatRole.System);
        else
            _vm.Add("Nothing is waiting for input. Type /help to see what I can do.", ChatRole.System);
    }

    /// <summary>A pasted course structure is long; the transcript only needs its opening lines.</summary>
    private static string Shorten(string text)
        => text.Length <= 240
            ? text
            : text[..240].TrimEnd() + $" … ({text.Length:N0} characters pasted)";

    public void OnQuickReply(ChatMessage message, QuickReply reply)
    {
        if (!message.HasReplies)
            return;

        message.ClearReplies();
        _vm.User(reply.Label);
        _pending?.TrySetResult(reply.Value);
    }

    // ================================================================ commands

    private bool TryHandleCommand(string text)
    {
        var command = text;

        if (!command.StartsWith('/'))
        {
            command = command.ToLowerInvariant() switch
            {
                "help" => "/help",
                "undo" => "/undo",
                "cancel" => "/cancel",
                "start" or "new" or "restart" => "/start",
                _ => ""
            };

            if (command.Length == 0)
                return false;
        }

        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();

        switch (name)
        {
            case "/help":
                PrintHelp();
                return true;

            case "/cancel":
                if (_running)
                    CancelCurrent();
                else
                    _vm.Add("Nothing to cancel.", ChatRole.System);
                return true;

            case "/undo":
                DoUndo();
                return true;

            case "/start":
            case "/new":
                if (_running)
                {
                    _vm.Bot("Restarting…");
                    _restartRequested = true;
                    CancelCurrent();
                }
                else
                {
                    StartFlow();
                }
                return true;

            case "/courses":
                _vm.Add(
                    _courses.Count == 0
                        ? "No course list yet — /start begins a run."
                        : $"Courses ({_courses.Count}):\n{string.Join("\n", _courses.Select(c => $"    {c}"))}",
                    ChatRole.System);
                return true;

            case "/update":
                _ = CheckAndOfferUpdateAsync();
                return true;

            case "/rollback":
                _ = TryRollbackAsync();
                return true;

            case "/subfolders":
                _vm.IncludeSubfolders = !_vm.IncludeSubfolders;
                _vm.Add($"Include subfolders: {(_vm.IncludeSubfolders ? "on" : "off")}", ChatRole.System);
                return true;

            case "/support":
                if (!_vm.HasSupport)
                {
                    _vm.Add("Support details aren't available in this copy of the app.", ChatRole.System);
                    return true;
                }

                _vm.ShowSupport = !_vm.ShowSupport;
                if (!_vm.ShowSupport)
                    _vm.Add("Support section hidden.", ChatRole.System);
                return true;

            default:
                _vm.Add($"Unknown command \"{parts[0]}\". Type /help for the list.", ChatRole.Error);
                return true;
        }
    }

    private void CancelCurrent()
    {
        _cts?.Cancel();

        var pending = _pending;
        _pending = null;
        _pendingAcceptsText = false;
        _pendingAcceptsFile = false;

        if (pending is not null)
            pending.TrySetResult(CancelAnswer);
    }

    /// <summary>Wired to the persistent "Cancel run" button — identical to /cancel.</summary>
    public void OnCancelClicked()
    {
        if (_running)
            CancelCurrent();
        else
            _vm.Add("Nothing to cancel.", ChatRole.System);
    }

    private void PrintHelp()
    {
        _vm.Bot(
            "Commands\n" +
            "  /help              this list\n" +
            "  /start             begin a new filing run\n" +
            "  /cancel            stop the current step\n" +
            "  /undo              revert the last batch of moves\n" +
            "  /courses           show the current course list\n" +
            "  /subfolders        toggle scanning of nested folders\n" +
            "  /update            check for and install a newer build\n" +
            "  /rollback          go back to the previous build\n" +
            "  /support           show or hide the support section\n" +
            "\n" +
            "A PDF moves only when exactly one course code is found in its name, title,\n" +
            "first 5 pages' text, or OCR of a scan, and no other course code appears there.\n" +
            "Timetables and multi-course past-question packs are always left alone. No code,\n" +
            "or several codes, means it stays put. A different course code in the file name is\n" +
            "left alone without opening it; one found in the content skips OCR. A very strong\n" +
            "name/title match may be shown as a note, but nothing is moved or renamed by it.\n" +
            "\n" +
            "At step 2 you can paste a whole course structure, or drop a file that holds one\n" +
            "(.pdf, .txt, .md, .csv, .rtf), and the course list is read out of it.\n" +
            "\n" +
            "You can drop a folder, or a course-structure file, onto this window at any time —\n" +
            "or copy the file in Explorer and press Ctrl+V.");
    }

    // ================================================================ helpers

    private static bool TryResolveDirectory(string raw, bool mustExist, out string directory, out bool adjusted, out string? error)
    {
        directory = "";
        adjusted = false;
        error = null;

        var original = raw.Trim().Trim('"');
        var value = original;

        if (value.StartsWith("~"))
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[1..].TrimStart('\\', '/'));

        if (value.Length == 0)
        {
            error = "Please enter a folder path.";
            return false;
        }

        try
        {
            // Accepting a file path and using its folder is a nice convenience.
            if (File.Exists(value))
                value = Path.GetDirectoryName(value)!;

            value = Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            error = $"That isn't a valid path ({ex.Message}).";
            return false;
        }

        if (Directory.Exists(value))
        {
            directory = Path.TrimEndingDirectorySeparator(value);
            adjusted = !string.Equals(Path.TrimEndingDirectorySeparator(original), directory, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        if (mustExist)
        {
            error = $"Folder not found: {value}";
            return false;
        }

        var parent = Path.GetDirectoryName(value);

        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            error = $"Can't create that folder because it has no existing parent: {parent}";
            return false;
        }

        directory = Path.TrimEndingDirectorySeparator(value);
        adjusted = !string.Equals(Path.TrimEndingDirectorySeparator(original), directory, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string Greeting() =>
        "Hi! I file school PDFs into one folder per course — strictly by course code.\n" +
        "    I look in the file name, the PDF title and the first 5 pages' text, then OCR scans.\n" +
        "    Timetables and multi-course past-question packs are always left where they are.\n" +
        "    No code, or more than one course code: the file stays exactly where it is.\n" +
        "    A different course code in the name skips the file; one in the content skips OCR.\n" +
        "    Nothing is ever renamed.\n" +
        "\n" +
        "Three quick questions, then I show you a plan. Nothing moves until you approve it.\n" +
        "For the course list you can type your courses or hand me your whole course structure —\n" +
        "paste the portal page or drop its PDF, and I read the courses out of it.\n" +
        "Type /help for commands.";

    private static string BuildPlanSummary(SorterRunResult result, string targetDir)
    {
        var moves = result.Moves;
        var dumps = result.Dumps;

        var sb = new StringBuilder();

        if (moves.Count > 0)
        {
            sb.Append("Plan: move ").Append(moves.Count).Append(" of ").Append(result.Items.Count)
              .AppendLine(" PDF(s) into course folders under:");
            sb.AppendLine(targetDir);
            sb.AppendLine();

            var lines = new List<string>();

            foreach (var group in moves.GroupBy(m => m.Course!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"    {group.Key}   ({group.Count()} file(s))");

                foreach (var item in group)
                    lines.Add($"      {item.File.FileName}");
            }

            foreach (var line in lines.Take(40))
                sb.AppendLine(line);

            if (lines.Count > 40)
                sb.AppendLine($"    … and {lines.Count - 40} more line(s)");

            sb.AppendLine();
            var tiers = moves.GroupBy(m => m.Tier)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key} {g.Count()}");
            sb.Append("Matched by: ").AppendLine(string.Join(", ", tiers));
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Nothing moves automatically this time.");
        }

        if (dumps.Count > 0)
            sb.Append(dumps.Count).AppendLine(" file(s) have no single course code and stay exactly where they are — I'll list them at the end.");

        return sb.ToString();
    }
}
