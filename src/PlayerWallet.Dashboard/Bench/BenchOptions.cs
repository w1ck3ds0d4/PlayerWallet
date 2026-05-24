namespace PlayerWallet.Dashboard.Bench;

/// <summary>
/// Per-bench-run tuning. Defaults are short so the dashboard "Run" button feels interactive; the 5-minute production benchmark still lives in tests/PlayerWallet.Tests.Load.
/// v2.2: <see cref="ScenarioRpsOverrides"/> lets you cap the request rate per scenario so the bench measures latency, not queue overflow. The pool scenarios (add/deduct/balance) handle 200 rps fine because load is spread across 100 wallets, but hot-wallet routes 100% of traffic to a single grain whose Orleans turn-based serialisation has a hard per-grain ceiling well under 200 rps. Without a per-scenario cap the bench measures NBomber's 30s HTTP timeout, not the system's real per-grain latency.
/// </summary>
public sealed class BenchOptions
{
    public int WarmUpSeconds { get; set; } = 5;
    public int DurationSeconds { get; set; } = 30;
    public int RequestsPerSecond { get; set; } = 200;
    public int WalletPoolSize { get; set; } = 100;
    public decimal SeedBalance { get; set; } = 1_000_000m;
    public string Currency { get; set; } = "EUR";

    /// <summary>HTTP client timeout for bench requests. Raised from the original 30s so slow hot-wallet requests aren't artificially counted as failures by NBomber when the grain queue is deep.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Per-scenario request-rate overrides (rps). When a scenario name is not in the dictionary, <see cref="RequestsPerSecond"/> is used.</summary>
    public Dictionary<string, int> ScenarioRpsOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved rps for a scenario: per-scenario override if set, otherwise the global <see cref="RequestsPerSecond"/>.</summary>
    public int ResolvedRpsFor(string scenario)
    {
        return ScenarioRpsOverrides.TryGetValue(scenario, out var rps) ? rps : RequestsPerSecond;
    }
}

public sealed class DashboardOptions
{
    public List<ProjectConfig> Projects { get; set; } = [];
    public BenchOptions Bench { get; set; } = new();
}

public sealed class ProjectConfig
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Color { get; set; } = "#888";
}
