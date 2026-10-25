using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolPdfSorter.Engine;

/// <summary>What the usage counter remembers between runs.</summary>
public sealed class UsageSettings
{
    public bool Enabled { get; set; } = true;
    public string? CounterUrl { get; set; }
    public DateTimeOffset? LastPingUtc { get; set; }
}

/// <summary>
/// An anonymous "this copy is in use" ping, used only to count unique installs.
///
/// The identifier is a random value created on first run and stored next to the app's
/// settings. It is not derived from the machine, the user or the folder being sorted, and
/// nothing else is sent: one request a day carrying that random id and the app version.
/// Counting stays off until a counter URL is configured, so a checkout never phones home.
/// </summary>
public static class UsageCounter
{
    /// <summary>The deployed counter. Replace it if the worker moves.</summary>
    public const string DefaultCounterUrl =
        "https://schoolpdfsorter-usage.tayodavidaina.workers.dev/ping";

    /// <summary>An address that is deliberately not configured, used by tests and docs.</summary>
    public const string DisabledCounterUrl =
        "https://schoolpdfsorter-usage.example.workers.dev/ping";

    /// <summary>Set this environment variable to test against a local counter.</summary>
    public const string CounterUrlVariable = "SCHOOLPDFSORTER_USAGE_URL";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string InstallIdPath => Path.Combine(UpdateFeed.DataDirectory, "install-id");

    public static string SettingsPath => Path.Combine(UpdateFeed.DataDirectory, "usage.json");

    // ================================================================ settings

    public static UsageSettings LoadSettings(string? path = null)
    {
        path ??= SettingsPath;

        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<UsageSettings>(
                    File.ReadAllText(path), JsonOptions);

                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // An unreadable settings file is the same as no settings file.
        }

        return new UsageSettings();
    }

    public static void SaveSettings(UsageSettings settings, string? path = null)
    {
        path ??= SettingsPath;

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Remembering a ping time is never worth failing a run over.
        }
    }

    // ============================================================== identifier

    /// <summary>
    /// The random install id, created once and reused. A corrupt or missing file is replaced;
    /// the value is never derived from anything on the machine.
    /// </summary>
    public static string GetOrCreateInstallId(string? path = null)
    {
        path ??= InstallIdPath;

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();

                if (IsInstallId(existing))
                    return existing;
            }
        }
        catch
        {
            // Fall through and create a fresh id.
        }

        var id = Guid.NewGuid().ToString("N");

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, id);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A read-only profile still gets a usable id for this session.
        }

        return id;
    }

    public static bool IsInstallId(string value)
        => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    // =================================================================== ping

    public static string ResolveCounterUrl(UsageSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CounterUrl))
            return settings.CounterUrl!;

        var fromEnvironment = Environment.GetEnvironmentVariable(CounterUrlVariable);

        return string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultCounterUrl : fromEnvironment;
    }

    /// <summary>Counting is on only when a real endpoint is configured.</summary>
    public static bool IsConfigured(string? counterUrl)
        => !string.IsNullOrWhiteSpace(counterUrl)
           && !counterUrl.Contains("schoolpdfsorter-usage.example.workers.dev", StringComparison.OrdinalIgnoreCase)
           && Uri.TryCreate(counterUrl, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public static bool ShouldPing(UsageSettings settings, string counterUrl, DateTimeOffset now)
        => settings.Enabled
           && IsConfigured(counterUrl)
           && (settings.LastPingUtc is null ||
               now - settings.LastPingUtc.Value >= TimeSpan.FromHours(24));

    public static string BuildPingUrl(string counterUrl, string installId, string version)
    {
        var separator = counterUrl.Contains('?') ? '&' : '?';

        return $"{counterUrl}{separator}id={Uri.EscapeDataString(installId)}" +
               $"&v={Uri.EscapeDataString(version)}";
    }

    /// <summary>
    /// Sends one count. Any failure returns false and is never retried in this session;
    /// the next launch may try again. Nothing about the request is logged or surfaced.
    /// </summary>
    public static async Task<bool> PingAsync(
        string counterUrl, string installId, string version,
        HttpClient? client = null, CancellationToken ct = default)
    {
        if (!IsConfigured(counterUrl) || !IsInstallId(installId))
            return false;

        try
        {
            using var http = client ?? CreateClient();
            using var response = await http.GetAsync(
                BuildPingUrl(counterUrl, installId, version), ct);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SchoolPdfSorter-Usage/1.0");

        return client;
    }
}
