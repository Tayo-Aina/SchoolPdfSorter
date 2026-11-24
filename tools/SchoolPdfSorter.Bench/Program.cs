using System.Diagnostics;
using System.Text.Json;
using SchoolPdfSorter.Engine;

// Strict-mode benchmark: every file must be identified by exactly one course code
// (name -> title -> text layer -> OCR). No code or several codes means "left alone".

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? Arg(string name)
{
    for (int i = 0; i < argv.Length - 1; i++)
        if (argv[i] == name) return argv[i + 1];
    return null;
}
double? Num(string name) => Arg(name) is { } v && double.TryParse(v, out var d) ? d : null;
bool Flag(string name) => argv.Contains(name);

string FindDir(string name)
{
    var d = AppContext.BaseDirectory;
    for (int i = 0; i < 10; i++)
    {
        var c = Path.Combine(d, name);
        if (Directory.Exists(c)) return c;
        var p = Directory.GetParent(d);
        if (p is null) break;
        d = p.FullName;
    }
    throw new DirectoryNotFoundException($"'{name}' not found above {AppContext.BaseDirectory}");
}

var probeDir = FindDir("probe");
var corpusDir = Arg("--corpus") ?? Path.Combine(probeDir, "testcorpus");
var manifestPath = Arg("--manifest") ?? Path.Combine(corpusDir, "manifest.json");

if (!File.Exists(manifestPath)) { Console.WriteLine($"manifest missing: {manifestPath}"); return 3; }

var files = new List<(string Path, string? Expected, string Kind)>();
using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        var f = e.GetProperty("file").GetString()!;
        string? expected = e.TryGetProperty("expected_course", out var ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() : null;
        string kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "?";
        files.Add((Path.Combine(corpusDir, f.Replace('/', Path.DirectorySeparatorChar)), expected, kind));
    }

var courses = files.Where(f => f.Expected is not null).Select(f => f.Expected!)
    .Distinct(StringComparer.Ordinal).ToList();

var opts = new StrictOptions();
if (Num("--max-pages") is { } mp) opts.MaxOcrPages = (int)mp;
if (Num("--dpi") is { } dpi) opts.Dpi = dpi;
if (Num("--threads") is { } t) opts.Threads = (int)t;
if (Flag("--no-unlisted-stop")) opts.StopOnUnlistedCode = false;
if (Flag("--no-suggest")) opts.SuggestNearMatches = false;

// --workers pins the engine's OCR worker pool (0 leaves it to the resource budget).
int? workers = Arg("--workers") is { } ws && int.TryParse(ws, out var w) ? w : null;
if (workers is { } wc) opts.Workers = wc;
var stress = Flag("--stress");

var sorter = new Sorter(courses, opts);
Console.WriteLine($"corpus: {files.Count} files, {courses.Count} courses");
Console.WriteLine($"match: file name -> title -> text layer -> OCR (first {opts.MaxOcrPages} pages, {opts.Dpi:0} dpi)");
Console.WriteLine($"stop on different course codes: {(opts.StopOnUnlistedCode ? "yes" : "no")}; suggestions: {(opts.SuggestNearMatches ? "yes" : "no")}");
Console.WriteLine($"workers: {(workers?.ToString() ?? "auto")}");
if (sorter.CoursesWithoutCode.Count > 0)
    Console.WriteLine($"courses with no code: {string.Join(" | ", sorter.CoursesWithoutCode)}");

var verbose = Flag("--verbose");
var paths = files.Select(f => f.Path).ToList();
var allocBefore = GC.GetTotalAllocatedBytes(precise: false);
var sw = Stopwatch.StartNew();
var run = sorter.Run(paths, p =>
{
    if (verbose && p.Phase == "ocr")
        Console.WriteLine($"    [ocr] {p.FileName} page {p.Page}/{p.Pages}");
});
sw.Stop();
var allocAfter = GC.GetTotalAllocatedBytes(precise: false);

var byPath = files.ToDictionary(f => f.Path);
int moves = 0, wrongMoves = 0, ambiguousMoves = 0;
int dumps = 0, expectedDumps = 0, ambiguousDumps = 0;

Console.WriteLine();
Console.WriteLine($"{"result",-12} {"kind",-9} {"file",-38} expected / got");
foreach (var item in run.Items.OrderBy(i => i.File.FileName, StringComparer.OrdinalIgnoreCase))
{
    var (_, expected, kind) = byPath[item.File.Path];
    string status, got;

    if (item.IsMove)
    {
        moves++;
        if (expected is null)
        {
            status = "WRONG(amb)";
            ambiguousMoves++;
        }
        else if (string.Equals(item.Course, expected, StringComparison.Ordinal))
        {
            status = "ok";
        }
        else
        {
            status = "WRONG";
            wrongMoves++;
        }

        got = $"-> {item.Course}";
    }
    else
    {
        dumps++;
        if (expected is null)
        {
            status = "ok-dump(amb)";
            ambiguousDumps++;
        }
        else
        {
            status = "dumped";
            expectedDumps++;
        }

        got = "(left alone)";
    }

    Console.WriteLine($"{status,-12} {kind,-9} {Trunc(item.File.FileName, 38),-38} {(expected ?? "(none)"),-28} {Trunc(got, 34)}");
    var extra = item.Suggestion is { } suggestion
        ? $" | looks like {suggestion.Course} ({suggestion.Score:0.00})"
        : "";
    Console.WriteLine($"{"",12} {"",9} {Trunc(item.Reason + extra, 130)} [{item.Tier}, {item.OcrPages} pg]");
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "...";

var tiers = run.Items.GroupBy(i => i.Tier)
    .OrderBy(g => g.Key, StringComparer.Ordinal)
    .Select(g => $"{g.Key} {g.Count()}");

Console.WriteLine();
Console.WriteLine("== summary ==");
Console.WriteLine($"files {run.Stats.Total} | opened {run.Stats.OpenedFiles} PDF(s) | moves {moves} (wrong {wrongMoves}, ambiguous {ambiguousMoves}) | dumps {dumps} (expected-but-unmatched {expectedDumps}, ambiguous-ok {ambiguousDumps})");
Console.WriteLine($"suggestions: {run.Dumps.Count(i => i.Suggestion is not null)} file(s)");
Console.WriteLine($"tiers: {string.Join(", ", tiers)}");
Console.WriteLine($"OCR: {run.Stats.OcrFiles} file(s), {run.Stats.OcrPages} page(s), {run.Stats.OcrSeconds:F1}s (model load {run.Stats.OcrInitSeconds:F1}s)");
Console.WriteLine($"time total {run.Stats.TotalSeconds:F1}s (signals {run.Stats.SignalSeconds:F1}s, scan {run.Stats.ScanSeconds:F1}s)");
Console.WriteLine($"peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0):F1} MiB");
Console.WriteLine($"allocated during run: {(allocAfter - allocBefore) / (1024.0 * 1024.0):F1} MB");

if (stress)
{
    Console.WriteLine();
    Console.WriteLine("== stress (3 back-to-back passes) ==");
    for (int pass = 1; pass <= 3; pass++)
    {
        var passSw = Stopwatch.StartNew();
        sorter.Run(paths);
        passSw.Stop();
        var peak = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0);
        Console.WriteLine($"pass {pass}: wall {passSw.Elapsed.TotalSeconds:F1}s, peak working set {peak:F1} MiB");
    }
}

return wrongMoves > 0 ? 2 : 0;
