namespace PlayerWallet.Dashboard.Bench;

/// <summary>Per-bench-run tuning. Defaults are short so the dashboard "Run" button feels interactive; the 5-minute production benchmark still lives in tests/PlayerWallet.Tests.Load.</summary>
public sealed class BenchOptions
{
    public int WarmUpSeconds { get; set; } = 5;
    public int DurationSeconds { get; set; } = 30;
    public int RequestsPerSecond { get; set; } = 200;
    public int WalletPoolSize { get; set; } = 100;
    public decimal SeedBalance { get; set; } = 1_000_000m;
    public string Currency { get; set; } = "EUR";
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
