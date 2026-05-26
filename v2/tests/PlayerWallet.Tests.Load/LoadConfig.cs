namespace PlayerWallet.Tests.Load;

internal static class LoadConfig
{
    /// <summary>HTTP base URL of the wallet API. Override with the first program argument.</summary>
    public const string DefaultBaseUrl = "http://localhost:5000";

    /// <summary>Pre-seeded wallet pool. The benchmark cycles through these player ids.</summary>
    public const int WalletPoolSize = 1000;

    /// <summary>Per-endpoint sustained throughput target from the spec.</summary>
    public static readonly int TargetRequestsPerSecond =
        int.TryParse(Environment.GetEnvironmentVariable("WALLET_TARGET_RPS"), out var rps) ? rps : 1000;

    /// <summary>Warmup phase before measurement starts. Excluded from reported stats.</summary>
    public static readonly TimeSpan WarmUpDuration =
        int.TryParse(Environment.GetEnvironmentVariable("WALLET_WARMUP_SECONDS"), out var warm)
            ? TimeSpan.FromSeconds(warm)
            : TimeSpan.FromSeconds(30);

    /// <summary>Measurement window per scenario (spec-mandated 5 minutes per endpoint).</summary>
    public static readonly TimeSpan MeasurementDuration =
        int.TryParse(Environment.GetEnvironmentVariable("WALLET_MEASURE_SECONDS"), out var meas)
            ? TimeSpan.FromSeconds(meas)
            : TimeSpan.FromMinutes(5);

    /// <summary>Hot-wallet appendix scenario: single grain, shorter window.</summary>
    public static readonly TimeSpan HotWalletDuration = TimeSpan.FromSeconds(60);

    /// <summary>Pre-seed every wallet with this balance so deduct scenarios never hit zero.</summary>
    public const decimal SeedBalance = 1_000_000_000m;

    public const string Currency = "EUR";
}
