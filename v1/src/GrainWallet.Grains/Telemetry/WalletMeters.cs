using System.Diagnostics.Metrics;

namespace GrainWallet.Grains.Telemetry;

/// <summary>Wallet OTel meters. Shared between Grains (balance, idempotency, outbox depth) and the API (request count, latency). Tests run without a meter provider and the recordings become silent no-ops.</summary>
public static class WalletMeters
{
    public const string MeterName = "GrainWallet";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "wallet.requests",
        unit: "{requests}",
        description: "Total wallet operation requests, tagged by endpoint and result.");

    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "wallet.request_duration_ms",
        unit: "ms",
        description: "Wallet operation duration in milliseconds.");

    public static readonly Histogram<double> BalanceAfterOp = Meter.CreateHistogram<double>(
        "wallet.balance_after_op",
        unit: "{currency}",
        description: "Balance value after each successful mutation.");

    public static readonly Counter<long> IdempotencyHits = Meter.CreateCounter<long>(
        "wallet.idempotency_hits",
        unit: "{hits}",
        description: "Idempotency cache hits, broken down by endpoint.");

    private static int _maxOutboxDepth;

    public static readonly ObservableGauge<int> OutboxPending = Meter.CreateObservableGauge(
        "wallet.outbox_pending",
        () => Interlocked.Exchange(ref _maxOutboxDepth, 0),
        unit: "{events}",
        description: "Maximum outbox depth observed since the last sample.");

    public static void RecordOutboxDepth(int depth)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _maxOutboxDepth);
            if (depth <= current)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _maxOutboxDepth, depth, current) != current);
    }
}
