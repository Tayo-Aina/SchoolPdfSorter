using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SchoolPdfSorter.Engine;

namespace UsageDashboard;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private List<Snapshot> _history = new();
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            LoadSettings();
            await RefreshAsync();
            _timer.Start();
        };

        _timer.Tick += async (_, _) => await RefreshAsync(quiet: true);
    }

    private sealed class DashboardSettings
    {
        public string? WorkerUrl { get; set; }
        public string? Token { get; set; }
        public string Repo { get; set; } = "Tayo-Aina/SchoolPdfSorter";
        public List<Snapshot> History { get; set; } = new();
    }

    private sealed class Snapshot
    {
        public string Date { get; set; } = "";
        public long Total { get; set; }
        public long Today { get; set; }
        public long Downloads { get; set; }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SchoolPdfSorter",
        "dashboard.json");

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<DashboardSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions);

                if (settings is not null)
                {
                    WorkerBox.Text = settings.WorkerUrl ?? "";
                    TokenBox.Password = settings.Token ?? "";
                    RepoBox.Text = string.IsNullOrWhiteSpace(settings.Repo)
                        ? "Tayo-Aina/SchoolPdfSorter"
                        : settings.Repo;
                    _history = settings.History ?? new();
                }
            }
        }
        catch
        {
            // A damaged settings file just means starting from empty fields.
        }

        if (string.IsNullOrWhiteSpace(RepoBox.Text))
            RepoBox.Text = "Tayo-Aina/SchoolPdfSorter";

        HistoryList.ItemsSource = _history.OrderByDescending(h => h.Date).ToList();
    }

    private DashboardSettings ReadSettings() => new()
    {
        WorkerUrl = WorkerBox.Text.Trim(),
        Token = TokenBox.Password.Trim(),
        Repo = string.IsNullOrWhiteSpace(RepoBox.Text) ? "Tayo-Aina/SchoolPdfSorter" : RepoBox.Text.Trim(),
        History = _history
    };

    private static void SaveSettings(DashboardSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch
        {
            // The dashboard still works for this session.
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        SaveSettings(ReadSettings());
        Status.Text = "Saved on this PC.";
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(bool quiet = false)
    {
        if (_refreshing)
            return;

        _refreshing = true;
        RefreshButton.IsEnabled = false;

        if (!quiet)
            Status.Text = "Checking...";

        var settings = ReadSettings();
        SaveSettings(settings);

        long downloads = 0;
        string? downloadsNote = null;

        try
        {
            downloads = await UsageStats.FetchDownloadsAsync(settings.Repo);
        }
        catch
        {
            downloadsNote = "downloads unavailable";
        }

        CounterStats? counter = null;
        string? counterNote = null;

        if (string.IsNullOrWhiteSpace(settings.WorkerUrl) ||
            string.IsNullOrWhiteSpace(settings.Token))
        {
            counterNote = "counter not set";
        }
        else
        {
            try
            {
                counter = await UsageStats.FetchCounterAsync(settings.WorkerUrl, settings.Token);
            }
            catch (UnauthorizedAccessException)
            {
                counterNote = "token rejected";
            }
            catch
            {
                counterNote = "counter unreachable";
            }
        }

        if (counter is not null)
        {
            UniqueValue.Text = counter.Total.ToString("N0");
            TodayValue.Text = counter.Today.ToString("N0");
            Record(settings, counter, downloads);
        }
        else
        {
            UniqueValue.Text = "-";
            TodayValue.Text = "-";
        }

        DownloadsValue.Text = downloadsNote is null ? downloads.ToString("N0") : "-";
        HistoryList.ItemsSource = _history.OrderByDescending(h => h.Date).ToList();

        var parts = new List<string> { $"Updated {DateTime.Now:HH:mm}" };
        parts.Add(counter is null ? $"counter: {counterNote}" : "counter: ok");
        parts.Add(downloadsNote ?? "downloads: ok");

        Status.Text = string.Join("   |   ", parts);

        RefreshButton.IsEnabled = true;
        _refreshing = false;
    }

    /// <summary>Stores or updates today's reading, so the table builds a history over time.</summary>
    private void Record(DashboardSettings settings, CounterStats counter, long downloads)
    {
        var date = string.IsNullOrWhiteSpace(counter.Date)
            ? DateTime.UtcNow.ToString("yyyy-MM-dd")
            : counter.Date;

        var existing = settings.History.FirstOrDefault(item => item.Date == date);

        if (existing is null)
        {
            settings.History.Add(new Snapshot
            {
                Date = date,
                Total = counter.Total,
                Today = counter.Today,
                Downloads = downloads
            });
        }
        else
        {
            existing.Total = counter.Total;
            existing.Today = counter.Today;
            existing.Downloads = downloads;
        }

        settings.History = settings.History
            .OrderBy(item => item.Date)
            .TakeLast(180)
            .ToList();

        _history = settings.History;
        SaveSettings(settings);
    }
}
