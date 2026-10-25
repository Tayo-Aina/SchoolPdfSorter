using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SchoolPdfSorter.Engine;

/// <summary>One release, as published by the project's release step.</summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("minVersion")] string? MinVersion = null);

/// <summary>A manifest that is newer than the running build.</summary>
public sealed record UpdateCheck(UpdateManifest Manifest, Version Version)
{
    public string SizeText => Manifest.Size >= 1024L * 1024 * 1024
        ? $"{Manifest.Size / (1024.0 * 1024 * 1024):0.0} GB"
        : $"{Manifest.Size / (1024.0 * 1024):0} MB";
}

/// <summary>What the updater remembers between runs (LocalAppData, never next to the user's files).</summary>
public sealed class UpdateSettings
{
    public bool Enabled { get; set; } = true;
    public string? FeedUrl { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? SkippedVersion { get; set; }
}

/// <summary>
/// A small, offline-first updater: one static JSON file names the newest release, the app
/// downloads it, checks its SHA-256 and swaps itself on restart. No installer, no admin, no
/// background service. Every network failure is treated as "offline", which is the normal
/// state for this app.
/// </summary>
public static class UpdateFeed
{
    /// <summary>
    /// The release feed this build checks. It is a plain static file next to the releases, so a
    /// failed lookup or a 404 simply means "no update", never an error.
    /// </summary>
    public const string PlaceholderFeedUrl =
        "https://raw.githubusercontent.com/Tayo-Aina/SchoolPdfSorter/main/latest.json";

    /// <summary>Set this environment variable to test against a local feed without rebuilding.</summary>
    public const string FeedUrlVariable = "SCHOOLPDFSORTER_UPDATE_FEED";

    private const string ExecutableName = "SchoolPdfSorterApp.exe";

    private static readonly Regex Sha256Shape = new(
        "^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ================================================================ locations

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SchoolPdfSorter");

    public static string SettingsPath => Path.Combine(DataDirectory, "update.json");

    public static string StagingRoot => Path.Combine(DataDirectory, "updates");

    /// <summary>The copy of the previous release kept for /rollback.</summary>
    public static string PreviousExePath(string currentExe)
    {
        var directory = Path.GetDirectoryName(currentExe) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(currentExe);
        return Path.Combine(directory, stem + ".previous.exe");
    }

    // ================================================================= version

    public static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        return Version.TryParse(value, out var version) ? Normalize(version) : null;
    }

    /// <summary>The running build's version, as stamped into the project file.</summary>
    public static Version CurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateFeed).Assembly;
        return Normalize(assembly.GetName().Version ?? new Version(1, 0, 0, 0));
    }

    public static bool IsNewer(Version candidate, Version current) => candidate > current;

    // =================================================================== feed

    /// <summary>True when the feed URL is real (not the checkout placeholder).</summary>
    public static bool IsConfigured(string? feedUrl)
        => !string.IsNullOrWhiteSpace(feedUrl)
           && !feedUrl.Contains("OWNER", StringComparison.OrdinalIgnoreCase)
           && Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public static string ResolveFeedUrl(UpdateSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.FeedUrl))
            return settings.FeedUrl!;

        var fromEnvironment = Environment.GetEnvironmentVariable(FeedUrlVariable);

        return string.IsNullOrWhiteSpace(fromEnvironment) ? PlaceholderFeedUrl : fromEnvironment;
    }

    /// <summary>At most one check a day, and only when the user has not turned updates off.</summary>
    public static bool ShouldCheck(UpdateSettings settings, string feedUrl, DateTimeOffset now)
        => settings.Enabled
           && IsConfigured(feedUrl)
           && (settings.LastCheckUtc is null ||
               now - settings.LastCheckUtc.Value >= TimeSpan.FromHours(24));

    public static UpdateManifest? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        UpdateManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null ||
            ParseVersion(manifest.Version) is null ||
            string.IsNullOrWhiteSpace(manifest.Url) ||
            !Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            manifest.Size <= 0 ||
            string.IsNullOrWhiteSpace(manifest.Sha256) ||
            !Sha256Shape.IsMatch(manifest.Sha256))
            return null;

        return manifest;
    }

    /// <summary>
    /// Fetches the manifest and returns it only when it is a newer release the user has not
    /// skipped. Any failure — offline, DNS, timeout, a bad document — returns null.
    /// </summary>
    public static async Task<UpdateCheck?> CheckAsync(
        string feedUrl, Version current, HttpClient? client = null, CancellationToken ct = default)
    {
        if (!IsConfigured(feedUrl))
            return null;

        try
        {
            using var http = client ?? CreateClient();

            using var response = await http.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var manifest = ParseManifest(json);

            if (manifest is null || ParseVersion(manifest.Version) is not { } version)
                return null;

            if (!IsNewer(version, current))
                return null;

            // A build too old for the feed cannot be updated safely in place.
            if (ParseVersion(manifest.MinVersion) is { } minimum && IsNewer(minimum, current))
                return null;

            return new UpdateCheck(manifest, version);
        }
        catch (Exception)
        {
            // Offline is the normal case. Updates are a convenience, never a requirement.
            return null;
        }
    }

    // ================================================================ download

    /// <summary>
    /// Downloads, verifies and stages the new build, then returns its path. The file only
    /// lands under the staging directory; nothing next to the user's app is touched yet.
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<double>? progress = null,
        HttpClient? client = null,
        CancellationToken ct = default,
        string? stagingRoot = null)
    {
        var root = stagingRoot ?? StagingRoot;
        var directory = Path.Combine(root, SafeVersion(manifest.Version));
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, ExecutableName);
        var part = target + ".part";

        try
        {
            using var http = client ?? CreateClient();

            using var response = await http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? manifest.Size;
            long read = 0;

            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(part))
            {
                var buffer = new byte[81920];
                int chunk;

                while ((chunk = await input.ReadAsync(buffer, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await output.WriteAsync(buffer.AsMemory(0, chunk), ct);
                    read += chunk;

                    if (total > 0)
                        progress?.Report((double)read / total);
                }
            }

            var length = new FileInfo(part).Length;
            if (length != manifest.Size)
                throw new InvalidDataException(
                    $"The update downloaded {length:N0} bytes but the feed says {manifest.Size:N0}.");

            var hash = Sha256Of(part);
            if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The downloaded update did not match its SHA-256 checksum, so it was discarded.");

            // A downloaded file carries Mark-of-the-Web; this build is hash-verified, and the
            // marker would only make Windows hesitate to run it.
            RemoveZoneIdentifier(part);

            File.Move(part, target, overwrite: true);
            progress?.Report(1);

            return target;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ==================================================================== swap

    /// <summary>
    /// Replaces the running executable. Windows allows renaming a running exe, so the old
    /// build becomes <c>SchoolPdfSorterApp.previous.exe</c> and the staged one takes its place.
    /// On any failure the old build is put back. No administrator rights, no installer.
    /// </summary>
    public static bool TrySwap(string currentExe, string stagedExe, out string previousExe, out string? error)
    {
        previousExe = PreviousExePath(currentExe);
        error = null;

        try
        {
            if (!File.Exists(stagedExe))
            {
                error = "The downloaded update is no longer on disk.";
                return false;
            }

            TryDelete(previousExe);
            File.Move(currentExe, previousExe);

            try
            {
                File.Move(stagedExe, currentExe);
            }
            catch (Exception ex)
            {
                File.Move(previousExe, currentExe);
                error = ex.Message;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex is UnauthorizedAccessException
                ? "That folder is read-only. Move the app somewhere you can write (for example Downloads) and try again."
                : ex.Message;
            return false;
        }
    }

    /// <summary>Puts the kept previous build back, moving the current one aside.</summary>
    public static bool TryRestore(string currentExe, out string? error)
    {
        error = null;
        var previous = PreviousExePath(currentExe);
        var failed = Path.Combine(
            Path.GetDirectoryName(currentExe) ?? ".",
            Path.GetFileNameWithoutExtension(currentExe) + ".failed.exe");

        try
        {
            if (!File.Exists(previous))
            {
                error = "There is no earlier build to go back to.";
                return false;
            }

            TryDelete(failed);
            File.Move(currentExe, failed);

            try
            {
                File.Move(previous, currentExe);
            }
            catch (Exception ex)
            {
                File.Move(failed, currentExe);
                error = ex.Message;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ================================================================ settings

    public static UpdateSettings LoadSettings(string? path = null)
    {
        path ??= SettingsPath;

        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<UpdateSettings>(
                    File.ReadAllText(path), JsonOptions);

                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // An unreadable settings file is the same as no settings file.
        }

        return new UpdateSettings();
    }

    public static void SaveSettings(UpdateSettings settings, string? path = null)
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
            // Remembering a check time is never worth failing a run over.
        }
    }

    /// <summary>Forgets staged downloads that are no longer the newest release.</summary>
    public static void CleanStaging(string? keepVersion = null)
    {
        try
        {
            if (!Directory.Exists(StagingRoot))
                return;

            foreach (var directory in Directory.GetDirectories(StagingRoot))
            {
                if (keepVersion is not null &&
                    string.Equals(Path.GetFileName(directory), SafeVersion(keepVersion),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }
        catch
        {
            // Staging cleanup is housekeeping only.
        }
    }

    // ================================================================ internals

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SchoolPdfSorter-Updater/1.0");

        return client;
    }

    private static string SafeVersion(string version)
        => new(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());

    private static void RemoveZoneIdentifier(string path)
    {
        try
        {
            var zone = path + ":Zone.Identifier";

            if (File.Exists(zone))
                File.Delete(zone);
        }
        catch
        {
            // Not fatal: the checksum already passed.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A locked leftover is retried the next time.
        }
    }

    /// <summary>Launch arguments understood by the app itself.</summary>
    public static bool WantsRollback(string[] args)
        => args.Any(arg => string.Equals(arg, "--rollback", StringComparison.OrdinalIgnoreCase));
}
