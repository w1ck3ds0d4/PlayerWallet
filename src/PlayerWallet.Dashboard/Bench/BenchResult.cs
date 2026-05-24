namespace PlayerWallet.Dashboard.Bench;

public enum BenchStatus
{
    Pending,
    Seeding,
    Warming,
    Running,
    Completed,
    Failed,
}

public sealed record ScenarioOutcome(
    string Project,
    string Scenario,
    long OkCount,
    long FailCount,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double StdDevMs,
    double AvgRps);

public sealed class BenchRun
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public required string[] ProjectNames { get; init; }
    public required string Scenario { get; init; }
    public BenchStatus Status { get; set; } = BenchStatus.Pending;
    public string? StatusDetail { get; set; }
    public string? Error { get; set; }
    public List<ScenarioOutcome> Outcomes { get; set; } = [];
    public int WarmUpSeconds { get; init; }
    public int DurationSeconds { get; init; }
    public int RequestsPerSecond { get; init; }

    /// <summary>Absolute path to the per-run folder under the dashboard's reports root. Holds NBomber's HTML/CSV/MD/TXT reports plus summary.json. Set by <see cref="BenchRunner"/> on run start.</summary>
    public string? FolderPath { get; set; }
}
