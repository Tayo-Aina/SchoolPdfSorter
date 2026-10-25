using System.Net.Http;
using System.Text.Json;

namespace SchoolPdfSorter.Engine;

/// <summary>One reading from the usage worker.</summary>
public sealed record CounterStats(long Total, long Today, string Date);

/// <summary>
/// Reads the two numbers the dashboard shows: distinct installs from the usage worker,
/// and download counts from GitHub. Kept out of the UI so both can be tested against a
/// local server.
/// </summary>
public static class UsageStats
{
    /// <summary>Turns a counter address (with or without /ping) into its /stats address.</summary>
    public static string StatsUrl(string workerUrl)
    {
        var trimmed = (workerUrl ?? "").Trim().TrimEnd('/');

        if (trimmed.EndsWith("/ping", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/ping".Length];

        return trimmed + "/stats";
    }

    public static async Task<CounterStats> FetchCounterAsync(
        string workerUrl, string token, HttpClient? client = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerUrl) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("The worker URL and stats token are required.");

        using var http = client ?? CreateClient();
        var url = StatsUrl(workerUrl) + "?token=" + Uri.EscapeDataString(token);

        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("The stats token was rejected.");

        response.EnsureSuccessStatusCode();

        return ParseCounter(await response.Content.ReadAsStringAsync(ct));
    }

    public static CounterStats ParseCounter(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var total = root.TryGetProperty("total", out var t) && t.TryGetInt64(out var totalValue)
            ? totalValue
            : 0;
        var today = root.TryGetProperty("today", out var d) && d.TryGetInt64(out var todayValue)
            ? todayValue
            : 0;
        var date = root.TryGetProperty("date", out var dateValue)
            ? dateValue.GetString() ?? ""
            : "";

        return new CounterStats(total, today, date);
    }

    public static async Task<long> FetchDownloadsAsync(
        string repo, HttpClient? client = null, CancellationToken ct = default)
    {
        using var http = client ?? CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=100");

        request.Headers.UserAgent.ParseAdd("SchoolPdfSorter-Dashboard");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return SumDownloads(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Total downloads across every asset of every release.</summary>
    public static long SumDownloads(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return 0;

        long total = 0;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("download_count", out var count) &&
                    count.TryGetInt64(out var value))
                    total += value;
            }
        }

        return total;
    }

    private static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(10) };
}
