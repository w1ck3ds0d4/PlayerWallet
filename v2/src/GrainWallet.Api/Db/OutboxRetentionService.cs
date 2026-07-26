using Npgsql;

namespace GrainWallet.Api.Db;

/// <summary>
/// v2.4: periodic cleanup of fully-drained outbox rows.
/// Every <see cref="SweepInterval"/> deletes rows where <c>published_at</c> is older than
/// <see cref="RetentionWindow"/>. Once a row is published its job is done; we keep it briefly so
/// late consumers can catch up via the underlying Kafka topic AND so an operator can audit the
/// recent past via direct SQL, then it can go. Without retention the table grows linearly with
/// total mutation count and autovacuum work scales with it, which is what caused the recurring
/// bench-tail anomaly even after the v2.3 autovacuum tuning.
/// </summary>
internal sealed class OutboxRetentionService(
    NpgsqlDataSource dataSource,
    ILogger<OutboxRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private const string DeleteSql = """
        DELETE FROM wallet_outbox
        WHERE published_at IS NOT NULL
          AND published_at < NOW() - INTERVAL '5 minutes'
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "OutboxRetentionService started; sweep every {Sweep}, retain published rows for {Window}.",
            SweepInterval,
            RetentionWindow);

        // First sweep waits a full interval; on a fresh boot the table is empty so no rush.
        try { await Task.Delay(SweepInterval, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await SweepAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("OutboxRetentionService removed {Count} published rows older than {Window}.", deleted, RetentionWindow);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OutboxRetentionService sweep failed; will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("OutboxRetentionService stopped.");
    }

    private async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DeleteSql, connection);
        command.CommandTimeout = 30;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
