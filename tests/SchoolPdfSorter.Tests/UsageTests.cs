using System.Net;
using System.Net.Sockets;
using System.Text;
using SchoolPdfSorter.Engine;

namespace SchoolPdfSorter.Tests;

public sealed class UsageTests
{
    // ============================================================== identifier

    [Fact]
    public void Install_id_is_created_once_and_reused()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "install-id");

        try
        {
            var first = UsageCounter.GetOrCreateInstallId(path);

            Assert.True(UsageCounter.IsInstallId(first));
            Assert.Equal(first, File.ReadAllText(path).Trim());
            Assert.Equal(first, UsageCounter.GetOrCreateInstallId(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_install_id_is_replaced()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "install-id");

        try
        {
            File.WriteAllText(path, "not-an-id");

            var id = UsageCounter.GetOrCreateInstallId(path);

            Assert.True(UsageCounter.IsInstallId(id));
            Assert.Equal(id, File.ReadAllText(path).Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("", false)]
    public void Install_id_shape_is_validated(string value, bool expected)
        => Assert.Equal(expected, UsageCounter.IsInstallId(value));

    // =================================================================== ping

    [Theory]
    [InlineData("https://counter.example.net/ping", "0123456789abcdef0123456789abcdef", "1.2.3")]
    [InlineData("https://counter.example.net/ping?key=abc", "0123456789abcdef0123456789abcdef", "1.2.3")]
    public void Ping_url_carries_only_the_id_and_version(string url, string id, string version)
    {
        var result = UsageCounter.BuildPingUrl(url, id, version);

        Assert.Contains($"id={id}", result);
        Assert.Contains($"v={version}", result);
        Assert.Equal(url.Contains('?'), result.Contains("?key=abc&"));
    }

    [Fact]
    public void Placeholder_counter_is_not_configured()
        => Assert.False(UsageCounter.IsConfigured(UsageCounter.PlaceholderCounterUrl));

    [Theory]
    [InlineData("https://counter.example.net/ping", true)]
    [InlineData("http://127.0.0.1:8080/ping", true)]
    [InlineData("http://counter.example.net/ping", false)]
    [InlineData("", false)]
    public void Counter_url_must_be_https_or_loopback(string url, bool expected)
        => Assert.Equal(expected, UsageCounter.IsConfigured(url));

    [Fact]
    public void Ping_runs_at_most_once_a_day_and_respects_the_switch()
    {
        var url = "https://counter.example.net/ping";
        var now = DateTimeOffset.UtcNow;

        Assert.True(UsageCounter.ShouldPing(new UsageSettings(), url, now));
        Assert.False(UsageCounter.ShouldPing(
            new UsageSettings { LastPingUtc = now.AddHours(-23) }, url, now));
        Assert.True(UsageCounter.ShouldPing(
            new UsageSettings { LastPingUtc = now.AddHours(-25) }, url, now));
        Assert.False(UsageCounter.ShouldPing(
            new UsageSettings { Enabled = false }, url, now));
        Assert.False(UsageCounter.ShouldPing(
            new UsageSettings(), UsageCounter.PlaceholderCounterUrl, now));
    }

    [Fact]
    public void Ping_sends_the_id_and_version()
    {
        using var server = new OneShotServer(200);
        var id = "0123456789abcdef0123456789abcdef";

        var ok = UsageCounter.PingAsync(server.Url(), id, "1.2.3").GetAwaiter().GetResult();

        Assert.True(ok);

        var line = server.RequestLine.GetAwaiter().GetResult();
        Assert.Contains($"/ping?id={id}", line);
        Assert.Contains("v=1.2.3", line);
    }

    [Fact]
    public void Ping_treats_a_server_error_as_a_failed_count()
    {
        using var server = new OneShotServer(500);

        Assert.False(UsageCounter.PingAsync(
            server.Url(), "0123456789abcdef0123456789abcdef", "1.2.3").GetAwaiter().GetResult());
    }

    [Fact]
    public void Ping_is_silent_when_the_server_is_unreachable()
    {
        var ok = UsageCounter.PingAsync(
                $"http://127.0.0.1:{FreePort()}/ping",
                "0123456789abcdef0123456789abcdef", "1.2.3")
            .GetAwaiter().GetResult();

        Assert.False(ok);
    }

    [Fact]
    public void Ping_is_skipped_for_an_unconfigured_counter()
        => Assert.False(UsageCounter.PingAsync(
            UsageCounter.PlaceholderCounterUrl,
            "0123456789abcdef0123456789abcdef", "1.2.3").GetAwaiter().GetResult());

    // ================================================================ settings

    [Fact]
    public void Settings_round_trip_and_default_to_enabled()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "usage.json");

        try
        {
            Assert.True(UsageCounter.LoadSettings(path).Enabled);

            UsageCounter.SaveSettings(new UsageSettings
            {
                Enabled = false,
                CounterUrl = "https://counter.example.net/ping",
                LastPingUtc = DateTimeOffset.Parse("2026-02-03T04:05:06Z")
            }, path);

            var loaded = UsageCounter.LoadSettings(path);

            Assert.False(loaded.Enabled);
            Assert.Equal("https://counter.example.net/ping", loaded.CounterUrl);
            Assert.Equal(DateTimeOffset.Parse("2026-02-03T04:05:06Z"), loaded.LastPingUtc);
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
        var path = Path.Combine(directory, "usage.json");

        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.True(UsageCounter.LoadSettings(path).Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // =================================================================== stats

    [Theory]
    [InlineData("https://x.workers.dev", "https://x.workers.dev/stats")]
    [InlineData("https://x.workers.dev/", "https://x.workers.dev/stats")]
    [InlineData("https://x.workers.dev/ping", "https://x.workers.dev/stats")]
    [InlineData("https://x.workers.dev/ping/", "https://x.workers.dev/stats")]
    public void Stats_url_is_built_from_the_worker_root(string input, string expected)
        => Assert.Equal(expected, UsageStats.StatsUrl(input));

    [Fact]
    public void Counter_stats_are_read_from_the_worker_json()
    {
        var stats = UsageStats.ParseCounter("""
            { "total": 128, "today": 9, "date": "2026-10-25" }
            """);

        Assert.Equal(128, stats.Total);
        Assert.Equal(9, stats.Today);
        Assert.Equal("2026-10-25", stats.Date);
    }

    [Fact]
    public void Missing_counter_fields_read_as_zero()
    {
        var stats = UsageStats.ParseCounter("{}");

        Assert.Equal(0, stats.Total);
        Assert.Equal(0, stats.Today);
        Assert.Equal("", stats.Date);
    }

    [Fact]
    public void Downloads_are_summed_across_every_release_asset()
    {
        var json = """
            [
              { "tag_name": "v1", "assets": [ { "download_count": 5 }, { "download_count": 7 } ] },
              { "tag_name": "v2", "assets": [ { "download_count": 3 } ] },
              { "tag_name": "v3", "assets": [] }
            ]
            """;

        Assert.Equal(15, UsageStats.SumDownloads(json));
    }

    [Fact]
    public void Downloads_treat_a_non_array_as_zero()
        => Assert.Equal(0, UsageStats.SumDownloads("{ \"message\": \"rate limited\" }"));

    [Fact]
    public void Counter_client_reads_a_local_worker()
    {
        var json = """{ "total": 12, "today": 2, "date": "2026-10-25" }""";
        using var server = new OneShotServer(200, json);

        var stats = UsageStats.FetchCounterAsync(server.Url(), "secret")
            .GetAwaiter().GetResult();

        Assert.Equal(12, stats.Total);
        Assert.Equal(2, stats.Today);

        var line = server.RequestLine.GetAwaiter().GetResult();
        Assert.Contains("/stats?token=secret", line);
    }

    [Fact]
    public void Counter_client_reports_a_rejected_token()
    {
        using var server = new OneShotServer(403, "forbidden");

        Assert.Throws<UnauthorizedAccessException>(() =>
            UsageStats.FetchCounterAsync(server.Url(), "wrong").GetAwaiter().GetResult());
    }

    // =============================================================== helpers

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SchoolPdfSorterUsageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Accepts one request, records its request line, and answers with a fixed status.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _status;
        private readonly string _body;
        private readonly TaskCompletionSource<string> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;

        public OneShotServer(int status, string body = "ok")
        {
            _status = status;
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _serve = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public Task<string> RequestLine => _request.Task;

        public string Url(string path = "/ping") => BaseUrl + path;

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer);
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                var line = text.Split("\r\n")[0];

                _request.TrySetResult(line);

                var body = _body;
                var reason = _status == 200 ? "200 OK" : $"{_status} Error";
                var head = $"HTTP/1.1 {reason}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head + body));
                await stream.FlushAsync();
            }
            catch
            {
                _request.TrySetResult("");
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _serve.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
