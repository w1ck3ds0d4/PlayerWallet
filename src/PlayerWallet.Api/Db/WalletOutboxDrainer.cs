using System.Text.Json;
using Npgsql;
using PlayerWallet.Contracts;
using PlayerWallet.Grains;

namespace PlayerWallet.Api.Db;

/// <summary>
/// Background service that polls <c>wallet_outbox</c> for unpublished rows, forwards them to <see cref="IWalletEventPublisher"/>, and marks <c>published_at</c> on success.
/// Decouples Kafka from the HTTP path: the grain commits balance + outbox row atomically and returns; this drainer ships the event off-thread. Crash recovery: unpublished rows survive process restart and re-publish on the next poll (at-least-once; consumer dedupes on <c>event_id</c>).
/// Publishes via <c>Task.WhenAll</c>; sequential publishes would cap drainer throughput at 1/broker-ack-latency (~200 evt/s on Acks=Leader). MaxInFlight is bounded by the Kafka producer config.
/// </summary>
internal sealed class WalletOutboxDrainer(
    NpgsqlDataSource dataSource,
    IWalletEventPublisher publisher,
    ILogger<WalletOutboxDrainer> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(100);
    private const int BatchSize = 200;

    private static readonly string ClaimSql = $"""
        SELECT id, event_id, event_type, player_id, payload
        FROM wallet_outbox
        WHERE published_at IS NULL
        ORDER BY id
        LIMIT {BatchSize}
        """;

    private const string MarkPublishedSql = """
        UPDATE wallet_outbox
        SET published_at = NOW()
        WHERE id = ANY(@ids)
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "WalletOutboxDrainer started; poll {Poll}ms, idle {Idle}ms, batch {Batch}.",
            (int)PollInterval.TotalMilliseconds,
            (int)IdleInterval.TotalMilliseconds,
            BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var drained = await DrainBatchAsync(stoppingToken);
                await Task.Delay(drained == 0 ? IdleInterval : PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WalletOutboxDrainer iteration failed; backing off.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        logger.LogInformation("WalletOutboxDrainer stopped.");
    }

    private async Task<int> DrainBatchAsync(CancellationToken cancellationToken)
    {
        var batch = await ClaimBatchAsync(cancellationToken);
        if (batch.Count == 0)
        {
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
            await MarkPublishedAsync(publishedIds, cancellationToken);
        }

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

    private async Task<List<DrainEntry>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<DrainEntry>(BatchSize);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ClaimSql, connection);
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

    private async Task MarkPublishedAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(MarkPublishedSql, connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IWalletEvent? DeserializeEvent(string eventType, string payload) => eventType switch
    {
        nameof(FundsAdded) => JsonSerializer.Deserialize(payload, WalletStateJsonContext.Default.FundsAdded),
        nameof(FundsDeducted) => JsonSerializer.Deserialize(payload, WalletStateJsonContext.Default.FundsDeducted),
        nameof(DeductionRejected) => JsonSerializer.Deserialize(payload, WalletStateJsonContext.Default.DeductionRejected),
        _ => null,
    };

    private sealed record DrainEntry(long Id, IWalletEvent Event);
}
