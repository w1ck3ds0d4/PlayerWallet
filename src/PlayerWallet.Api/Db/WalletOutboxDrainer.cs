using System.Text.Json;
using Npgsql;
using PlayerWallet.Contracts;
using PlayerWallet.Grains;
using PlayerWallet.Grains.Telemetry;

namespace PlayerWallet.Api.Db;

/// <summary>
/// Background service that polls <c>wallet_outbox</c> for unpublished rows, forwards them to <see cref="IWalletEventPublisher"/>, and marks <c>published_at</c> on success.
/// Decouples Kafka from the HTTP path: the grain commits balance + outbox row atomically and returns; this drainer ships the event off-thread. Crash recovery: unpublished rows survive process restart and re-publish on the next poll (at-least-once; consumer dedupes on <c>event_id</c>).
/// Publishes via <c>Task.WhenAll</c>; sequential publishes would cap drainer throughput at 1/broker-ack-latency (~200 evt/s on Acks=Leader). MaxInFlight is bounded by the Kafka producer config.
/// v2: each claim cycle holds a Postgres transaction with FOR UPDATE SKIP LOCKED so multiple API instances can drain non-overlapping shards without double-publishing.
/// </summary>
internal sealed class WalletOutboxDrainer(
    NpgsqlDataSource dataSource,
    IWalletEventPublisher publisher,
    OutboxBackpressureGate gate,
    ILogger<WalletOutboxDrainer> logger) : BackgroundService
{
    // v2.2: bigger batch + multi-worker. Two worker loops poll wallet_outbox concurrently;
    // FOR UPDATE SKIP LOCKED ensures they never claim the same row, so the workers truly
    // parallelise. Adaptive poll interval: when the previous batch was full (probable backlog)
    // we re-poll on the BusyInterval; when empty we back off to IdleInterval.
    private static readonly TimeSpan BusyInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan GateRefreshInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 500;
    private const int WorkerCount = 2;

    private static readonly string ClaimSql = $"""
        SELECT id, event_id, event_type, player_id, payload
        FROM wallet_outbox
        WHERE published_at IS NULL
        ORDER BY id
        LIMIT {BatchSize}
        FOR UPDATE SKIP LOCKED
        """;

    private const string MarkPublishedSql = """
        UPDATE wallet_outbox
        SET published_at = NOW()
        WHERE id = ANY(@ids)
        """;

    private const string PendingCountSql = """
        SELECT COUNT(*) FROM wallet_outbox WHERE published_at IS NULL
        """;

    private DateTime _nextGateRefreshUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "WalletOutboxDrainer started; workers {Workers}, batch {Batch}, busy {Busy}ms / poll {Poll}ms / idle {Idle}ms.",
            WorkerCount,
            BatchSize,
            (int)BusyInterval.TotalMilliseconds,
            (int)PollInterval.TotalMilliseconds,
            (int)IdleInterval.TotalMilliseconds);

        var workers = Enumerable.Range(0, WorkerCount)
            .Select(workerId => Task.Run(() => RunWorkerAsync(workerId, stoppingToken), stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }

        logger.LogInformation("WalletOutboxDrainer stopped.");
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var drained = await DrainBatchAsync(stoppingToken);
                // Worker 0 is the single owner of the gate refresh + outbox-depth meter, so we
                // don't multiply COUNT(*) load by WorkerCount.
                if (workerId == 0)
                {
                    await RefreshGateIfDueAsync(drained, stoppingToken);
                }
                // Adaptive cadence: BusyInterval when last batch was full (likely more pending),
                // PollInterval when we drained anything but not a full batch, IdleInterval when empty.
                var nextDelay = drained switch
                {
                    0 => IdleInterval,
                    _ when drained >= BatchSize => BusyInterval,
                    _ => PollInterval,
                };
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WalletOutboxDrainer worker {WorkerId} iteration failed; backing off.", workerId);
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<int> DrainBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var batch = await ClaimBatchAsync(connection, transaction, cancellationToken);
        if (batch.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var publishTasks = batch.Select(entry => PublishOneAsync(entry, cancellationToken)).ToArray();
        var outcomes = await Task.WhenAll(publishTasks);

        var publishedIds = new List<long>(batch.Count);
        for (var i = 0; i < outcomes.Length; i++)
        {
            if (outcomes[i])
            {
                publishedIds.Add(batch[i].Id);
            }
        }

        if (publishedIds.Count > 0)
        {
            await MarkPublishedAsync(connection, transaction, publishedIds, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return publishedIds.Count;
    }

    private async Task<bool> PublishOneAsync(DrainEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await publisher.PublishAsync(entry.Event, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "WalletOutboxDrainer failed to publish event {EventId} for player {PlayerId}; will retry.",
                entry.Event.EventId,
                entry.Event.PlayerId);
            return false;
        }
    }

    private async Task<List<DrainEntry>> ClaimBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var batch = new List<DrainEntry>(BatchSize);

        await using var command = new NpgsqlCommand(ClaimSql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var eventType = reader.GetString(2);
            var payload = reader.GetString(4);

            var walletEvent = DeserializeEvent(eventType, payload);
            if (walletEvent is null)
            {
                logger.LogWarning("Skipping unknown / unparseable event {EventType} in wallet_outbox id {Id}.", eventType, id);
                continue;
            }

            batch.Add(new DrainEntry(id, walletEvent));
        }

        return batch;
    }

    private static async Task MarkPublishedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MarkPublishedSql, connection, transaction);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RefreshGateIfDueAsync(int drained, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (drained == 0 && now < _nextGateRefreshUtc)
        {
            return;
        }

        _nextGateRefreshUtc = now + GateRefreshInterval;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PendingCountSql, connection);
        var pending = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        gate.Update(pending);
        WalletMeters.RecordOutboxDepth((int)Math.Min(pending, int.MaxValue));
    }

    private static IWalletEvent? DeserializeEvent(string eventType, string payload) => eventType switch
    {
        nameof(FundsAdded) => JsonSerializer.Deserialize(payload, WalletStateJsonContext.Default.FundsAdded),
        nameof(FundsDeducted) => JsonSerializer.Deserialize(payload, WalletStateJsonContext.Default.FundsDeducted),
        nameof(OperationRejected) => JsonSerializer.Deserialize(payload, WalletStateJsonContext.Default.OperationRejected),
        _ => null,
    };

    private sealed record DrainEntry(long Id, IWalletEvent Event);
}
