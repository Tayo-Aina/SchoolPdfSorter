using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SchoolPdfSorter.Engine;

namespace SchoolPdfSorter.Tests;

public sealed class UpdateTests
{
    private static readonly string Hash64 = new('a', 64);

    // ================================================================ versions

    [Theory]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData(" 2.0.1 ", "2.0.1.0")]
    public void ParseVersion_normalizes(string input, string expected)
        => Assert.Equal(Version.Parse(expected), UpdateFeed.ParseVersion(input));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.x.0")]
    [InlineData(null)]
    public void ParseVersion_rejects_nonsense(string? input)
        => Assert.Null(UpdateFeed.ParseVersion(input));

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    public void IsNewer_compares_numbers_not_text(string candidate, string current, bool expected)
        => Assert.Equal(expected, UpdateFeed.IsNewer(Version.Parse(candidate), Version.Parse(current)));

    // ================================================================ manifest

    private static string Manifest(
        string version = "1.1.0",
        string url = "https://example.com/SchoolPdfSorterApp.exe",
        long size = 4,
        string? hash = null,
        string? minVersion = null)
        => $$"""
            {
              "version": "{{version}}",
              "url": "{{url}}",
              "size": {{size}},
              "sha256": "{{hash ?? Hash64}}",
              "notes": "Faster sorting",
              "minVersion": {{(minVersion is null ? "null" : $"\"{minVersion}\"")}}
            }
            """;

    [Fact]
    public void ParseManifest_accepts_a_well_formed_feed()
    {
        var manifest = UpdateFeed.ParseManifest(Manifest());

        Assert.NotNull(manifest);
        Assert.Equal("1.1.0", manifest!.Version);
        Assert.Equal(4, manifest.Size);
        Assert.Equal("Faster sorting", manifest.Notes);
    }

    [Theory]
    [InlineData("http://example.com/app.exe")]      // plain http is only allowed on loopback
    [InlineData("ftp://example.com/app.exe")]
    [InlineData("not a url")]
    public void ParseManifest_rejects_untrusted_urls(string url)
        => Assert.Null(UpdateFeed.ParseManifest(Manifest(url: url)));

    [Fact]
    public void ParseManifest_allows_http_on_loopback_for_local_testing()
        => Assert.NotNull(UpdateFeed.ParseManifest(Manifest(url: "http://127.0.0.1:8123/app.exe")));

    [Fact]
    public void ParseManifest_tolerates_a_utf8_bom()
        => Assert.NotNull(UpdateFeed.ParseManifest("\uFEFF" + Manifest()));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void ParseManifest_rejects_a_bad_checksum(string hash)
        => Assert.Null(UpdateFeed.ParseManifest(Manifest(hash: hash)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ParseManifest_rejects_an_impossible_size(long size)
        => Assert.Null(UpdateFeed.ParseManifest(Manifest(size: size)));

    [Fact]
    public void ParseManifest_rejects_nonsense()
    {
        Assert.Null(UpdateFeed.ParseManifest(null));
        Assert.Null(UpdateFeed.ParseManifest(""));
        Assert.Null(UpdateFeed.ParseManifest("{ not json"));
        Assert.Null(UpdateFeed.ParseManifest("{}"));
    }

    [Fact]
    public void ParseManifest_rejects_a_version_that_is_not_a_version()
        => Assert.Null(UpdateFeed.ParseManifest(Manifest(version: "latest")));

    // ================================================================== feed

    [Fact]
    public void IsConfigured_ignores_the_placeholder()
        => Assert.False(UpdateFeed.IsConfigured(UpdateFeed.PlaceholderFeedUrl.Replace("Tayo-Aina", "OWNER")));

    [Fact]
    public void IsConfigured_accepts_the_published_feed()
        => Assert.True(UpdateFeed.IsConfigured(UpdateFeed.PlaceholderFeedUrl));

    [Theory]
    [InlineData("https://example.com/latest.json", true)]
    [InlineData("http://127.0.0.1:9000/latest.json", true)]
    [InlineData("http://example.com/latest.json", false)]
    [InlineData("", false)]
    public void IsConfigured_only_trusts_https_or_loopback(string url, bool expected)
        => Assert.Equal(expected, UpdateFeed.IsConfigured(url));

    [Fact]
    public void ShouldCheck_runs_once_a_day_and_respects_the_switch()
    {
        var feed = UpdateFeed.PlaceholderFeedUrl;
        var now = DateTimeOffset.UtcNow;

        Assert.True(UpdateFeed.ShouldCheck(new UpdateSettings(), feed, now));
        Assert.False(UpdateFeed.ShouldCheck(
            new UpdateSettings { LastCheckUtc = now.AddHours(-23) }, feed, now));
        Assert.True(UpdateFeed.ShouldCheck(
            new UpdateSettings { LastCheckUtc = now.AddHours(-25) }, feed, now));
        Assert.False(UpdateFeed.ShouldCheck(
            new UpdateSettings { Enabled = false }, feed, now));
        Assert.False(UpdateFeed.ShouldCheck(
            new UpdateSettings(), "http://example.com/latest.json", now));
    }

    // ============================================================ feed lookups

    [Fact]
    public void Check_reports_an_available_release()
    {
        using var server = new TestServer("/latest.json",
            Encoding.UTF8.GetBytes(Manifest(version: "2.0.0")));

        var outcome = UpdateFeed.CheckAsync(server.Url("/latest.json"), Version.Parse("1.0.0"))
            .GetAwaiter().GetResult();

        Assert.Equal(UpdateStatus.Available, outcome.Status);
        Assert.Equal("2.0.0", outcome.Check!.Manifest.Version);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    public void Check_reports_no_update_for_an_older_or_equal_feed(string version)
    {
        using var server = new TestServer("/latest.json",
            Encoding.UTF8.GetBytes(Manifest(version: version)));

        var outcome = UpdateFeed.CheckAsync(server.Url("/latest.json"), Version.Parse("1.0.0"))
            .GetAwaiter().GetResult();

        Assert.Equal(UpdateStatus.NoUpdate, outcome.Status);
        Assert.Null(outcome.Check);
    }

    [Fact]
    public void Check_treats_a_missing_feed_as_no_update()
    {
        using var server = new TestServer("/latest.json", Encoding.UTF8.GetBytes(Manifest()));

        var outcome = UpdateFeed.CheckAsync(server.Url("/not-published.json"), Version.Parse("1.0.0"))
            .GetAwaiter().GetResult();

        Assert.Equal(UpdateStatus.NoUpdate, outcome.Status);
    }

    [Fact]
    public void Check_reports_an_unreachable_server()
    {
        var outcome = UpdateFeed.CheckAsync(
                $"http://127.0.0.1:{FreePort()}/latest.json", Version.Parse("1.0.0"))
            .GetAwaiter().GetResult();

        Assert.Equal(UpdateStatus.Unreachable, outcome.Status);
        Assert.Null(outcome.Check);
    }

    [Fact]
    public void Check_reports_a_feed_that_is_not_json()
    {
        using var server = new TestServer("/latest.json", Encoding.UTF8.GetBytes("not a manifest"));

        var outcome = UpdateFeed.CheckAsync(server.Url("/latest.json"), Version.Parse("1.0.0"))
            .GetAwaiter().GetResult();

        Assert.Equal(UpdateStatus.InvalidFeed, outcome.Status);
    }

    [Fact]
    public void Check_reports_a_disabled_feed()
    {
        var outcome = UpdateFeed.CheckAsync("", Version.Parse("1.0.0"))
            .GetAwaiter().GetResult();

        Assert.Equal(UpdateStatus.InvalidFeed, outcome.Status);
    }

    [Fact]
    public void Download_verifies_and_stages_a_release()
    {
        var payload = Encoding.UTF8.GetBytes("a newer build");
        var hash = Convert.ToHexString(SHA256.HashData(payload));

        using var server = new TestServer("/app.exe", payload);
        var staging = NewDirectory();

        try
        {
            var manifest = UpdateFeed.ParseManifest(
                Manifest(url: server.Url("/app.exe"), size: payload.Length, hash: hash))!;

            var progress = new RecordingProgress();
            var staged = UpdateFeed.DownloadAsync(manifest, progress, stagingRoot: staging)
                .GetAwaiter().GetResult();

            Assert.True(File.Exists(staged));
            Assert.Equal(payload, File.ReadAllBytes(staged));
            Assert.False(File.Exists(staged + ".part"));
            Assert.NotEmpty(progress.Values);
            Assert.Equal(1, progress.Values[^1], 3);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public void Download_discards_a_release_with_the_wrong_checksum()
    {
        var payload = Encoding.UTF8.GetBytes("tampered build");
        var wrongHash = new string('b', 64);

        using var server = new TestServer("/app.exe", payload);
        var staging = NewDirectory();

        try
        {
            var manifest = UpdateFeed.ParseManifest(
                Manifest(url: server.Url("/app.exe"), size: payload.Length, hash: wrongHash))!;

            var error = Assert.Throws<InvalidDataException>(() =>
                UpdateFeed.DownloadAsync(manifest, stagingRoot: staging).GetAwaiter().GetResult());

            Assert.Contains("SHA-256", error.Message);
            Assert.Empty(Directory.GetFiles(staging, "*.exe", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(staging, "*.part", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public void Download_discards_a_release_with_the_wrong_size()
    {
        var payload = Encoding.UTF8.GetBytes("short");
        using var server = new TestServer("/app.exe", payload);
        var staging = NewDirectory();

        try
        {
            var manifest = UpdateFeed.ParseManifest(
                Manifest(url: server.Url("/app.exe"), size: payload.Length + 100,
                    hash: Convert.ToHexString(SHA256.HashData(payload))))!;

            Assert.Throws<InvalidDataException>(() =>
                UpdateFeed.DownloadAsync(manifest, stagingRoot: staging).GetAwaiter().GetResult());

            Assert.Empty(Directory.GetFiles(staging, "*.exe", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    // ================================================================== swap

    [Fact]
    public void Swap_keeps_the_old_build_as_previous()
    {
        var directory = NewDirectory();

        try
        {
            var current = Path.Combine(directory, "App.exe");
            var staged = Path.Combine(directory, "new.exe");

            File.WriteAllText(current, "old build");
            File.WriteAllText(staged, "new build");

            Assert.True(UpdateFeed.TrySwap(current, staged, out var previous, out var error));
            Assert.Null(error);
            Assert.Equal("new build", File.ReadAllText(current));
            Assert.Equal("old build", File.ReadAllText(previous));
            Assert.False(File.Exists(staged));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Swap_fails_cleanly_when_the_download_is_gone()
    {
        var directory = NewDirectory();

        try
        {
            var current = Path.Combine(directory, "App.exe");
            File.WriteAllText(current, "old build");

            Assert.False(UpdateFeed.TrySwap(current, Path.Combine(directory, "missing.exe"),
                out _, out var error));
            Assert.NotNull(error);
            Assert.Equal("old build", File.ReadAllText(current));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Restore_puts_the_previous_build_back()
    {
        var directory = NewDirectory();

        try
        {
            var current = Path.Combine(directory, "App.exe");
            var staged = Path.Combine(directory, "new.exe");

            File.WriteAllText(current, "old build");
            File.WriteAllText(staged, "new build");

            Assert.True(UpdateFeed.TrySwap(current, staged, out _, out _));
            Assert.True(UpdateFeed.TryRestore(current, out var error));
            Assert.Null(error);
            Assert.Equal("old build", File.ReadAllText(current));
            Assert.True(File.Exists(Path.Combine(directory, "App.failed.exe")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Restore_reports_when_there_is_nothing_to_restore()
    {
        var directory = NewDirectory();

        try
        {
            var current = Path.Combine(directory, "App.exe");
            File.WriteAllText(current, "only build");

            Assert.False(UpdateFeed.TryRestore(current, out var error));
            Assert.NotNull(error);
            Assert.Equal("only build", File.ReadAllText(current));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ============================================================== settings

    [Fact]
    public void Settings_round_trip_and_survive_a_missing_file()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "update.json");

        try
        {
            Assert.True(UpdateFeed.LoadSettings(path).Enabled);

            UpdateFeed.SaveSettings(new UpdateSettings
            {
                Enabled = false,
                FeedUrl = "https://example.com/latest.json",
                LastCheckUtc = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
                SkippedVersion = "1.5.0"
            }, path);

            var loaded = UpdateFeed.LoadSettings(path);
            Assert.False(loaded.Enabled);
            Assert.Equal("https://example.com/latest.json", loaded.FeedUrl);
            Assert.Equal("1.5.0", loaded.SkippedVersion);
            Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), loaded.LastCheckUtc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_treat_a_corrupt_file_as_empty()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "update.json");

        try
        {
            File.WriteAllText(path, "{ not json");

            var settings = UpdateFeed.LoadSettings(path);
            Assert.True(settings.Enabled);
            Assert.Null(settings.SkippedVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // =============================================================== helpers

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SchoolPdfSorterUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A port nothing is listening on, for the "server unreachable" case.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            lock (Values)
                Values.Add(value);
        }
    }

    /// <summary>
    /// A one-route HTTP server on loopback. HttpListener needs a URL reservation on Windows;
    /// a plain socket keeps the test self-contained and requires no administrator rights.
    /// </summary>
    private sealed class TestServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _body;
        private readonly string _path;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public TestServer(string path, byte[] body)
        {
            _path = path;
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public string Url(string path) => BaseUrl + path;

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[2048];

                    while (!request.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, _cts.Token);

                        if (read <= 0)
                            return;

                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var firstLine = request.ToString().Split("\r\n")[0];
                    var target = firstLine.Split(' ')[1];

                    if (!string.Equals(target, _path, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                            Array.Empty<byte>());
                        return;
                    }

                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                                 $"Content-Length: {_body.Length}\r\nConnection: close\r\n\r\n";

                    await WriteAsync(stream, header, _body);
                }
                catch
                {
                    // A test client that disconnects early is not interesting.
                }
            }
        }

        private static async Task WriteAsync(NetworkStream stream, string header, byte[] body)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }

        public void Dispose()
        {
            _cts.Cancel();

            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }

            _cts.Dispose();
        }
    }
}
