using System.Diagnostics.Metrics;

namespace PlayerWallet.Grains.Telemetry;

/// <summary>Wallet OTel meters. Shared between Grains (balance, idempotency, outbox depth) and the API (request count, latency). Tests run without a meter provider and the recordings become silent no-ops.</summary>
public static class WalletMeters
{
    public const string MeterName = "PlayerWallet";

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

    /// <summary>Histogram of drainer batch sizes. Useful to spot whether the drainer is keeping up (small batches = pace matches arrivals) or behind (consistently maxing out the batch cap).</summary>
    public static readonly Histogram<int> DrainerBatchSize = Meter.CreateHistogram<int>(
        "wallet.drainer.batch_size",
        unit: "{events}",
        description: "Number of rows claimed per drainer batch.");

    /// <summary>Histogram of full drainer cycle wall time (claim + publish + mark + commit).</summary>
    public static readonly Histogram<double> DrainerBatchDuration = Meter.CreateHistogram<double>(
        "wallet.drainer.batch_duration_ms",
        unit: "ms",
        description: "Wall time of one drainer cycle from claim to commit.");

    /// <summary>Histogram of just the Kafka publish phase. Sub-component of batch duration so you can see whether the drainer is Postgres-bound (claim/commit dominates) or Kafka-bound (publish dominates).</summary>
    public static readonly Histogram<double> DrainerPublishDuration = Meter.CreateHistogram<double>(
        "wallet.drainer.publish_duration_ms",
        unit: "ms",
        description: "Time spent in the Kafka publish phase per batch.");
}
