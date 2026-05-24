using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NBomber.CSharp;

namespace PlayerWallet.Dashboard.Bench;

/// <summary>
/// Coordinates dashboard-triggered bench runs. Lets one run be in flight at a time, keeps the last N runs in memory, and exposes per-run status for polling.
/// Runs NBomber in-process; v1 and v2 scenarios register together when both projects are picked, so the load is truly concurrent rather than serialised.
/// </summary>
public sealed class BenchRunner(IHttpClientFactory clientFactory, IOptions<DashboardOptions> options, ILogger<BenchRunner> logger)
{
    private const int HistoryCap = 20;
    private static readonly SemaphoreSlim s_runLock = new(1, 1);
    private readonly ConcurrentDictionary<string, BenchRun> _runs = new();
    private readonly Queue<string> _runOrder = new();
    private readonly object _historyLock = new();

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

    public Task<BenchRun> StartAsync(string scenario, IReadOnlyList<string> projectNames, int? durationOverrideSeconds, CancellationToken cancellationToken)
    {
        var dashboardOpts = options.Value;
        var configured = dashboardOpts.Bench;

        // Per-run effective options, with the duration override applied if any. Other knobs stay
        // server-side configured so a single dashboard URL exposes a coherent bench shape.
        var bench = durationOverrideSeconds is { } overrideDur
            ? new BenchOptions
            {
                WarmUpSeconds = configured.WarmUpSeconds,
                DurationSeconds = overrideDur,
                RequestsPerSecond = configured.RequestsPerSecond,
                WalletPoolSize = configured.WalletPoolSize,
                SeedBalance = configured.SeedBalance,
                Currency = configured.Currency,
            }
            : configured;

        var projects = projectNames
            .Select(n => dashboardOpts.Projects.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown project '{n}'."))
            .ToList();

        var run = new BenchRun
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow,
            ProjectNames = projects.Select(p => p.Name).ToArray(),
            Scenario = scenario,
            WarmUpSeconds = bench.WarmUpSeconds,
            DurationSeconds = bench.DurationSeconds,
            RequestsPerSecond = bench.RequestsPerSecond,
        };

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

        _ = Task.Run(() => ExecuteAsync(run, projects, scenario, bench, cancellationToken), cancellationToken);
        return Task.FromResult(run);
    }

    private async Task ExecuteAsync(BenchRun run, List<ProjectConfig> projects, string scenario, BenchOptions bench, CancellationToken cancellationToken)
    {
        await s_runLock.WaitAsync(cancellationToken);
        try
        {
            run.Status = BenchStatus.Seeding;
            run.StatusDetail = $"Seeding {projects.Count} project(s) with {bench.WalletPoolSize} wallets each.";

            var perProject = new List<(ProjectConfig Project, HttpClient Client, string[] Ids)>();

            foreach (var project in projects)
            {
                var client = clientFactory.CreateClient(project.Name);
                client.BaseAddress = new Uri(project.Url);
                client.Timeout = TimeSpan.FromSeconds(30);

                var ids = BenchScenarios.BuildPlayerIds(project.Name, bench.WalletPoolSize);
                logger.LogInformation("Seeding {Count} wallets (+1 hot) for project {Project} at {Url}.", ids.Length, project.Name, project.Url);
                await BenchScenarios.SeedAndWarmAsync(client, ids, project.Name, bench.SeedBalance, bench.Currency, cancellationToken);

                perProject.Add((project, client, ids));
            }

            run.Status = BenchStatus.Warming;
            run.StatusDetail = $"NBomber warmup ({bench.WarmUpSeconds}s), then {bench.DurationSeconds}s at {bench.RequestsPerSecond} rps per project.";

            var scenarios = perProject
                .Select(p => BenchScenarios.Build(scenario, p.Project.Name, p.Client, p.Ids, bench))
                .ToArray();

            run.Status = BenchStatus.Running;

            var stats = await Task.Run(() =>
                NBomberRunner
                    .RegisterScenarios(scenarios)
                    .WithReportFileName($"dashboard-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}")
                    .WithReportFormats()
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
            logger.LogError(ex, "Bench run {Id} failed.", run.Id);
            run.Status = BenchStatus.Failed;
            run.Error = ex.Message;
            run.FinishedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            s_runLock.Release();
        }
    }

    public static IReadOnlyList<string> SupportedScenarios { get; } = new[] { "get-balance", "add-funds", "deduct-funds", "hot-wallet" };
}
