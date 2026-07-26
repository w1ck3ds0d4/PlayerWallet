namespace GrainWallet.Dashboard.Bench;

public enum SuiteStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}

public sealed record SuiteLogEntry(DateTimeOffset At, string Level, string Message);

public sealed class SuiteStep
{
    public required string Scenario { get; init; }
    public required string Project { get; init; }
    public required int DurationSeconds { get; init; }
    public required int RequestsPerSecond { get; init; }
    public string? RunId { get; set; }
    public string? FolderPath { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public ScenarioOutcome? Outcome { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Orchestrated multi-step bench run (e.g. the formal Elantil spec: 3 endpoints x 2 projects at 1000 rps x 300s). Each step is delegated to the existing <see cref="BenchRunner"/> so all the persistence + reports + retention logic is reused; the suite just sequences them and aggregates results in one place.
/// </summary>
public sealed class SuiteRun
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public SuiteStatus Status { get; set; } = SuiteStatus.Pending;
    public string? StatusDetail { get; set; }
    public string? Error { get; set; }
    public List<SuiteStep> Steps { get; init; } = [];
    public List<SuiteLogEntry> Log { get; init; } = [];
    public string? FolderPath { get; set; }
}
