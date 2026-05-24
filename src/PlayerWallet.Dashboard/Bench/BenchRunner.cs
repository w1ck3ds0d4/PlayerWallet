using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NBomber.CSharp;

namespace PlayerWallet.Dashboard.Bench;

/// <summary>
/// Coordinates dashboard-triggered bench runs. Lets one run be in flight at a time, keeps the last N runs in memory, and exposes per-run status for polling.
/// Runs NBomber in-process; v1 and v2 scenarios register together when both projects are picked, so the load is truly concurrent rather than serialised.
/// v2 persistence: every completed run writes a per-run folder under <c>reports/</c> containing NBomber's HTML/CSV/MD/TXT exports plus a <c>summary.json</c>. On startup the runner scans that folder so the Recent runs table survives a dashboard restart and prior runs can be analysed offline.
/// </summary>
public sealed class BenchRunner
{
    private const int HistoryCap = 20;
    private static readonly SemaphoreSlim s_runLock = new(1, 1);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _clientFactory;
    private readonly IOptions<DashboardOptions> _options;
    private readonly ILogger<BenchRunner> _logger;
    private readonly ConcurrentDictionary<string, BenchRun> _runs = new();
    private readonly Queue<string> _runOrder = new();
    private readonly object _historyLock = new();
    private readonly string _reportsRoot;

    public BenchRunner(IHttpClientFactory clientFactory, IOptions<DashboardOptions> options, ILogger<BenchRunner> logger)
    {
        _clientFactory = clientFactory;
        _options = options;
        _logger = logger;
        _reportsRoot = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(_reportsRoot);

        LoadHistoryFromDisk();
    }

    public IReadOnlyCollection<BenchRun> RecentRuns
    {
        get
        {
            lock (_historyLock)
            {
                return _runOrder.Select(id => _runs[id]).Reverse().ToArray();
            }
        }
    }

    public BenchRun? GetRun(string id) => _runs.TryGetValue(id, out var run) ? run : null;

    public bool IsRunning => s_runLock.CurrentCount == 0;

    public string ReportsRoot => _reportsRoot;

    public Task<BenchRun> StartAsync(string scenario, IReadOnlyList<string> projectNames, int? durationOverrideSeconds, int? rpsOverride, CancellationToken cancellationToken)
    {
        var dashboardOpts = _options.Value;
        var configured = dashboardOpts.Bench;

        // Per-run effective options, with any of duration/rps overrides applied. Other knobs stay
        // server-side configured so a single dashboard URL exposes a coherent bench shape.
        // RPS-override semantics: when the user explicitly sets rps it WINS over per-scenario
        // overrides. The override applies to whatever scenario is picked, even if that scenario
        // has a per-scenario cap (like hot-wallet=50). Explicit > implicit.
        var hasAnyOverride = durationOverrideSeconds.HasValue || rpsOverride.HasValue;
        var bench = hasAnyOverride
            ? new BenchOptions
            {
                WarmUpSeconds = configured.WarmUpSeconds,
                DurationSeconds = durationOverrideSeconds ?? configured.DurationSeconds,
                RequestsPerSecond = rpsOverride ?? configured.RequestsPerSecond,
                WalletPoolSize = configured.WalletPoolSize,
                SeedBalance = configured.SeedBalance,
                Currency = configured.Currency,
                HttpTimeoutSeconds = configured.HttpTimeoutSeconds,
                // When the user gave an explicit rps, clear per-scenario overrides so the explicit
                // value wins. When they didn't, keep the per-scenario map in place.
                ScenarioRpsOverrides = rpsOverride.HasValue
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(configured.ScenarioRpsOverrides, StringComparer.OrdinalIgnoreCase),
            }
            : configured;

        var projects = projectNames
            .Select(n => dashboardOpts.Projects.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown project '{n}'."))
            .ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var folderName = $"{startedAt:yyyyMMdd-HHmmss}-{scenario}-{id[..8]}";
        var folderPath = Path.Combine(_reportsRoot, folderName);
        Directory.CreateDirectory(folderPath);

        var run = new BenchRun
        {
            Id = id,
            StartedAt = startedAt,
            ProjectNames = projects.Select(p => p.Name).ToArray(),
            Scenario = scenario,
            WarmUpSeconds = bench.WarmUpSeconds,
            DurationSeconds = bench.DurationSeconds,
            RequestsPerSecond = bench.ResolvedRpsFor(scenario),
            FolderPath = folderPath,
        };

        AddToHistory(run);

        _ = Task.Run(() => ExecuteAsync(run, projects, scenario, bench, folderPath, cancellationToken), cancellationToken);
        return Task.FromResult(run);
    }

    private async Task ExecuteAsync(BenchRun run, List<ProjectConfig> projects, string scenario, BenchOptions bench, string folderPath, CancellationToken cancellationToken)
    {
        await s_runLock.WaitAsync(cancellationToken);
        try
        {
            run.Status = BenchStatus.Seeding;
            run.StatusDetail = $"Seeding {projects.Count} project(s) with {bench.WalletPoolSize} wallets each.";

            var perProject = new List<(ProjectConfig Project, HttpClient Client, string[] Ids)>();

            foreach (var project in projects)
            {
                var client = _clientFactory.CreateClient(project.Name);
                client.BaseAddress = new Uri(project.Url);
                client.Timeout = TimeSpan.FromSeconds(bench.HttpTimeoutSeconds);

                var ids = BenchScenarios.BuildPlayerIds(project.Name, bench.WalletPoolSize);
                _logger.LogInformation("Seeding {Count} wallets (+1 hot) for project {Project} at {Url}.", ids.Length, project.Name, project.Url);
                await BenchScenarios.SeedAndWarmAsync(client, ids, project.Name, bench.SeedBalance, bench.Currency, cancellationToken);

                perProject.Add((project, client, ids));
            }

            var resolvedRps = bench.ResolvedRpsFor(scenario);
            run.Status = BenchStatus.Warming;
            run.StatusDetail = $"NBomber warmup ({bench.WarmUpSeconds}s), then {bench.DurationSeconds}s at {resolvedRps} rps per project (HTTP timeout {bench.HttpTimeoutSeconds}s).";

            var scenarios = perProject
                .Select(p => BenchScenarios.Build(scenario, p.Project.Name, p.Client, p.Ids, bench))
                .ToArray();

            run.Status = BenchStatus.Running;

            var stats = await Task.Run(() =>
                NBomberRunner
                    .RegisterScenarios(scenarios)
                    .WithReportFolder(folderPath)
                    .WithReportFileName("nbomber")
                    .Run(), cancellationToken);

            foreach (var s in stats.ScenarioStats)
            {
                var projectName = s.ScenarioName.Split('-')[0];
                var scenarioName = s.ScenarioName[(projectName.Length + 1)..];
                run.Outcomes.Add(new ScenarioOutcome(
                    Project: projectName,
                    Scenario: scenarioName,
                    OkCount: s.Ok.Request.Count,
                    FailCount: s.Fail.Request.Count,
                    MeanMs: s.Ok.Latency.MeanMs,
                    P50Ms: s.Ok.Latency.Percent50,
                    P95Ms: s.Ok.Latency.Percent95,
                    P99Ms: s.Ok.Latency.Percent99,
                    StdDevMs: s.Ok.Latency.StdDev,
                    AvgRps: s.Ok.Request.RPS));
            }

            run.Status = BenchStatus.Completed;
            run.StatusDetail = $"Done. {run.Outcomes.Sum(o => o.OkCount):N0} OK / {run.Outcomes.Sum(o => o.FailCount):N0} FAIL across {run.Outcomes.Count} scenario(s).";
            run.FinishedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bench run {Id} failed.", run.Id);
            run.Status = BenchStatus.Failed;
            run.Error = ex.Message;
            run.FinishedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            s_runLock.Release();
            TryWriteSummary(run);
        }
    }

    private void TryWriteSummary(BenchRun run)
    {
        if (run.FolderPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(run.FolderPath);
            var summaryPath = Path.Combine(run.FolderPath, "summary.json");
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(run, s_jsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write summary.json for run {Id} at {Folder}.", run.Id, run.FolderPath);
        }
    }

    private void LoadHistoryFromDisk()
    {
        if (!Directory.Exists(_reportsRoot))
        {
            return;
        }

        IEnumerable<string> summaryFiles;
        try
        {
            summaryFiles = Directory.EnumerateFiles(_reportsRoot, "summary.json", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate reports folder {Folder}; starting with empty history.", _reportsRoot);
            return;
        }

        var loaded = new List<BenchRun>();
        foreach (var path in summaryFiles)
        {
            try
            {
                var json = File.ReadAllText(path);
                var run = JsonSerializer.Deserialize<BenchRun>(json, s_jsonOptions);
                if (run is not null)
                {
                    run.FolderPath = Path.GetDirectoryName(path);
                    loaded.Add(run);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load summary.json at {Path}; skipping.", path);
            }
        }

        var ordered = loaded
            .OrderBy(r => r.StartedAt)
            .TakeLast(HistoryCap)
            .ToArray();

        foreach (var run in ordered)
        {
            AddToHistory(run);
        }

        _logger.LogInformation("Loaded {Count} prior bench run(s) from {Folder}.", ordered.Length, _reportsRoot);
    }

    private void AddToHistory(BenchRun run)
    {
        _runs[run.Id] = run;
        lock (_historyLock)
        {
            _runOrder.Enqueue(run.Id);
            while (_runOrder.Count > HistoryCap)
            {
                var old = _runOrder.Dequeue();
                _runs.TryRemove(old, out _);
            }
        }
    }

    public static IReadOnlyList<string> SupportedScenarios { get; } = new[] { "get-balance", "add-funds", "deduct-funds", "hot-wallet" };
}
