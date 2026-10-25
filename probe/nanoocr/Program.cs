using System.Diagnostics;
using NanoOcr;
using NanoOcr.Pipeline;

var corpus = args.Length > 0 ? args[0] : @"C:\Users\Tayod\Downloads\experiment\ShowFolderCreator-GitHub\SchoolPdfSorter\probe\testcorpus\messy";
int maxPages = args.Length > 1 && int.TryParse(args[1], out var mp) ? mp : 5;

string[] files = Directory.GetFiles(corpus, "*.pdf", SearchOption.AllDirectories);
var codes = new[] { "ECON301", "GST102", "CSC201", "MTH101", "PHY105", "BIO150", "ICE417" };

Console.WriteLine($"probe: {files.Length} pdf(s) in {corpus}, maxPages={maxPages}, cores={Environment.ProcessorCount}");

var sw = Stopwatch.StartNew();
using var ocr = new NanoOCR(new NanoOcrOptions
{
    Threads = Environment.ProcessorCount,
    Dpi = 200,
    Mode = ConvertMode.Auto,
});
Console.WriteLine($"model load: {sw.ElapsedMilliseconds} ms");
sw.Restart();

double ocrPageMs = 0;
int pagesTotal = 0, linesTotal = 0, textPages = 0, ocrPages = 0;
var perFile = new List<(string Name, int Pages, double Ms, string Hits, string Source)>();

foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    var t0 = Stopwatch.GetTimestamp();
    int pages = 0, lines = 0;
    var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var sources = new List<string>();

    try
    {
        foreach (var r in ocr.Predict(f, maxPages: maxPages))
        {
            pages++;
            lines += r.RecTexts.Count;
            sources.Add($"{r.PageIndex + 1}:{r.Page.Source}");
            if (r.Page.Source == "text") textPages++; else ocrPages++;
            if (r.Page.Timings.TryGetValue("total_ms", out var pageMs)) ocrPageMs += pageMs;
            var text = r.Text;
            var flat = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            foreach (var c in codes)
                if (flat.Contains(c, StringComparison.OrdinalIgnoreCase)) hits.Add(c);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERR {Path.GetFileName(f)}: {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    pagesTotal += pages;
    linesTotal += lines;
    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    perFile.Add((Path.GetFileName(f), pages, ms, string.Join(",", hits), string.Join(" ", sources)));
}

Console.WriteLine($"{"file",-48} {"pg",3} {"ms",8} {"hits",-20} source");
foreach (var p in perFile)
    Console.WriteLine($"{p.Name,-48} {p.Pages,3} {p.Ms,8:F0} {p.Hits,-20} {p.Source}");

Console.WriteLine();
Console.WriteLine($"total wall: {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"pages: {pagesTotal} (text {textPages}, ocr {ocrPages}) | lines {linesTotal} | avg {sw.Elapsed.TotalMilliseconds / Math.Max(1, pagesTotal):F0} ms/page wall");
Console.WriteLine($"reported page total: {ocrPageMs:F0} ms | avg {ocrPageMs / Math.Max(1, pagesTotal):F0} ms/page");
