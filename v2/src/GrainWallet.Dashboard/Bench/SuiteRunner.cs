using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace GrainWallet.Dashboard.Bench;

/// <summary>
/// Drives a multi-step benchmark suite by chaining <see cref="BenchRunner"/> calls sequentially.
/// One suite at a time (mirrors BenchRunner's single-run lock). The default spec suite runs
/// add-funds/deduct-funds/get-balance against v1 then v2 at 300s @ 1000 rps each = 6 sequential
/// steps. Live log entries are exposed for the dashboard terminal; final per-step outcomes are
/// captured for the summary table. The full SuiteRun is persisted to disk on completion so it
/// survives a dashboard restart.
/// </summary>
public sealed class SuiteRunner
{
    private const int HistoryCap = 20;
    private static readonly SemaphoreSlim s_suiteLock = new(1, 1);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly BenchRunner _benchRunner;
    private readonly IOptions<DashboardOptions> _options;
    private readonly ILogger<SuiteRunner> _logger;
    private readonly ConcurrentDictionary<string, SuiteRun> _suites = new();
    private readonly Queue<string> _suiteOrder = new();
    private readonly object _historyLock = new();
    private readonly string _suitesRoot;

    public SuiteRunner(BenchRunner benchRunner, IOptions<DashboardOptions> options, ILogger<SuiteRunner> logger)
    {
        _benchRunner = benchRunner;
        _options = options;
        _logger = logger;
        _suitesRoot = Path.Combine(AppContext.BaseDirectory, "suites");
        Directory.CreateDirectory(_suitesRoot);

        LoadHistoryFromDisk();
    }

    public IReadOnlyCollection<SuiteRun> RecentSuites
    {
        get
        {
            lock (_historyLock)
            {
                return _suiteOrder.Select(id => _suites[id]).Reverse().ToArray();
            }
        }
    }

    public SuiteRun? GetSuite(string id) => _suites.TryGetValue(id, out var s) ? s : null;

    public bool IsRunning => s_suiteLock.CurrentCount == 0;

    public Task<SuiteRun> StartSpecSuiteAsync(string[]? projects = null, CancellationToken cancellationToken = default)
    {
        var configured = _options.Value.Projects.Select(p => p.Name).ToArray();
        var targetProjects = (projects is { Length: > 0 } ? projects : configured)
            .Where(name => configured.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (targetProjects.Length == 0)
        {
            throw new InvalidOperationException("No valid projects to bench in the suite.");
        }

        // The Elantil spec lists 3 endpoints at 1000 rps for 5 min. Hot-wallet is an extra: it
        // routes all traffic to ONE Orleans grain, so 1000 rps overflows the per-grain capacity
        // (NBomber would measure HTTP timeouts, not real latency). Use the dashboard's configured
        // per-scenario override for hot-wallet (default 50) so the step measures realistic
        // single-grain throughput instead of queue overflow.
        const int specDurationSeconds = 300;
        const int specRps = 1000;
        var hotWalletRps = _options.Value.Bench.ResolvedRpsFor("hot-wallet");

        var scenarioConfigs = new (string Scenario, int Rps)[]
        {
            ("add-funds",    specRps),
            ("deduct-funds", specRps),
            ("get-balance",  specRps),
            ("hot-wallet",   hotWalletRps),
        };

        var steps = new List<SuiteStep>();
        foreach (var project in targetProjects)
        {
            foreach (var (scenario, rps) in scenarioConfigs)
            {
                steps.Add(new SuiteStep
                {
                    Scenario = scenario,
                    Project = project,
                    DurationSeconds = specDurationSeconds,
                    RequestsPerSecond = rps,
                });
            }
        }

        return StartCustomSuiteAsync(
            name: $"Spec ({targetProjects.Length} projects x {scenarioConfigs.Length} scenarios @ {specDurationSeconds}s; spec endpoints @ {specRps}rps, hot-wallet @ {hotWalletRps}rps)",
            steps: steps,
            cancellationToken: cancellationToken);
    }

    public Task<SuiteRun> StartCustomSuiteAsync(string name, List<SuiteStep> steps, CancellationToken cancellationToken = default)
    {
        if (steps.Count == 0)
        {
            throw new InvalidOperationException("Suite must contain at least one step.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var folderName = $"{startedAt:yyyyMMdd-HHmmss}-suite-{id[..8]}";
        var folderPath = Path.Combine(_suitesRoot, folderName);
        Directory.CreateDirectory(folderPath);

        var suite = new SuiteRun
        {
            Id = id,
            Name = name,
            StartedAt = startedAt,
            Steps = steps,
            FolderPath = folderPath,
        };

        AddToHistory(suite);

        _ = Task.Run(() => ExecuteAsync(suite, cancellationToken), cancellationToken);
        return Task.FromResult(suite);
    }

    private async Task ExecuteAsync(SuiteRun suite, CancellationToken cancellationToken)
    {
        await s_suiteLock.WaitAsync(cancellationToken);
        try
        {
            suite.Status = SuiteStatus.Running;
            Append(suite, "info", $"Suite '{suite.Name}' started with {suite.Steps.Count} step(s).");

            var totalEstimateSeconds = suite.Steps.Sum(s => s.DurationSeconds + 30); // bench + ~30s seed/warmup overhead
            Append(suite, "info", $"Estimated total runtime: ~{TimeSpan.FromSeconds(totalEstimateSeconds):mm\\:ss} (excluding cold-start). Sit tight.");

            for (var i = 0; i < suite.Steps.Count; i++)
            {
                var step = suite.Steps[i];
                var prefix = $"[{i + 1}/{suite.Steps.Count} {step.Project}/{step.Scenario}]";

                if (cancellationToken.IsCancellationRequested)
                {
                    step.Status = StepStatus.Skipped;
                    Append(suite, "warn", $"{prefix} skipped (suite cancelled).");
                    continue;
                }

                step.Status = StepStatus.Running;
                Append(suite, "step", $"{prefix} starting: {step.DurationSeconds}s @ {step.RequestsPerSecond} rps");

                BenchRun benchRun;
                try
                {
                    benchRun = await _benchRunner.StartAsync(
                        step.Scenario,
                        new[] { step.Project },
                        step.DurationSeconds,
                        step.RequestsPerSecond,
                        cancellationToken);
                    step.RunId = benchRun.Id;
                    step.FolderPath = benchRun.FolderPath;
                }
                catch (Exception ex)
                {
                    step.Status = StepStatus.Failed;
                    step.Error = ex.Message;
                    Append(suite, "error", $"{prefix} failed to start: {ex.Message}");
                    continue;
                }

                // Poll the underlying BenchRun until it leaves the running state.
                var lastReportedStatus = string.Empty;
                while (true)
                {
                    var current = _benchRunner.GetRun(benchRun.Id);
                    if (current is null)
                    {
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }

                    var currentStatus = current.Status.ToString();
                    if (currentStatus != lastReportedStatus)
                    {
                        Append(suite, "info", $"{prefix} {currentStatus.ToLowerInvariant()}{(current.StatusDetail is { } d ? $" - {d}" : string.Empty)}");
                        lastReportedStatus = currentStatus;
                    }

                    if (current.Status is BenchStatus.Completed or BenchStatus.Failed)
                    {
                        step.Outcome = current.Outcomes.FirstOrDefault();
                        if (current.Status == BenchStatus.Completed && step.Outcome is not null)
                        {
                            step.Status = StepStatus.Completed;
                            Append(suite, "done", $"{prefix} ok: {step.Outcome.OkCount:N0} ok / {step.Outcome.FailCount:N0} fail | mean {step.Outcome.MeanMs:F2}ms | p95 {step.Outcome.P95Ms:F2}ms | p99 {step.Outcome.P99Ms:F2}ms");
                        }
                        else
                        {
                            step.Status = StepStatus.Failed;
                            step.Error = current.Error;
                            Append(suite, "error", $"{prefix} failed: {current.Error ?? "(no error message)"}");
                        }
                        break;
                    }

                    await Task.Delay(1000, cancellationToken);
                }
            }

            var failed = suite.Steps.Count(s => s.Status == StepStatus.Failed);
            var ok = suite.Steps.Count(s => s.Status == StepStatus.Completed);
            suite.Status = failed > 0 ? SuiteStatus.Failed : SuiteStatus.Completed;
            suite.StatusDetail = $"{ok} ok / {failed} failed / {suite.Steps.Count} total";
            suite.FinishedAt = DateTimeOffset.UtcNow;
            Append(suite, suite.Status == SuiteStatus.Completed ? "done" : "error", $"Suite finished: {suite.StatusDetail}");
        }
        catch (OperationCanceledException)
        {
            suite.Status = SuiteStatus.Cancelled;
            suite.FinishedAt = DateTimeOffset.UtcNow;
            Append(suite, "warn", "Suite cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suite {Id} failed.", suite.Id);
            suite.Status = SuiteStatus.Failed;
            suite.Error = ex.Message;
            suite.FinishedAt = DateTimeOffset.UtcNow;
            Append(suite, "error", $"Suite failed: {ex.Message}");
        }
        finally
        {
            s_suiteLock.Release();
            TryWriteSummary(suite);
        }
    }

    private void Append(SuiteRun suite, string level, string message)
    {
        var entry = new SuiteLogEntry(DateTimeOffset.UtcNow, level, message);
        lock (suite.Log)
        {
            suite.Log.Add(entry);
        }
        _logger.LogInformation("[suite {Id}] {Message}", suite.Id, message);
    }

    private void TryWriteSummary(SuiteRun suite)
    {
        if (suite.FolderPath is null) return;
        try
        {
            Directory.CreateDirectory(suite.FolderPath);
            var path = Path.Combine(suite.FolderPath, "suite.json");
            File.WriteAllText(path, JsonSerializer.Serialize(suite, s_jsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist suite {Id} at {Folder}.", suite.Id, suite.FolderPath);
        }
    }

    private void LoadHistoryFromDisk()
    {
        if (!Directory.Exists(_suitesRoot)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_suitesRoot, "suite.json", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate suites folder {Folder}; starting empty.", _suitesRoot);
            return;
        }

        var loaded = new List<SuiteRun>();
        foreach (var path in files)
        {
            try
            {
                var json = File.ReadAllText(path);
                var suite = JsonSerializer.Deserialize<SuiteRun>(json, s_jsonOptions);
                if (suite is not null)
                {
                    suite.FolderPath = Path.GetDirectoryName(path);
                    loaded.Add(suite);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load suite at {Path}; skipping.", path);
            }
        }

        var ordered = loaded.OrderBy(s => s.StartedAt).TakeLast(HistoryCap).ToArray();
        foreach (var suite in ordered)
        {
            AddToHistory(suite);
        }

        _logger.LogInformation("Loaded {Count} prior suite(s) from {Folder}.", ordered.Length, _suitesRoot);
    }

    private void AddToHistory(SuiteRun suite)
    {
        _suites[suite.Id] = suite;
        lock (_historyLock)
        {
            _suiteOrder.Enqueue(suite.Id);
            while (_suiteOrder.Count > HistoryCap)
            {
                var old = _suiteOrder.Dequeue();
                _suites.TryRemove(old, out _);
            }
        }
    }

    /// <summary>Wipes in-memory suite history AND every per-suite folder under the suites root. Refuses while a suite is in flight.</summary>
    public int ClearHistory()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Cannot clear suite history while a suite is running.");
        }

        int removed;
        lock (_historyLock)
        {
            removed = _suites.Count;
            _suites.Clear();
            _suiteOrder.Clear();
        }

        if (Directory.Exists(_suitesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(_suitesRoot))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove suite folder {Folder}.", dir); }
            }
        }

        return removed;
    }
}
