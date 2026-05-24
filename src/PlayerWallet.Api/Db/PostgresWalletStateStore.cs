using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PlayerWallet.Contracts;
using PlayerWallet.Grains;

namespace PlayerWallet.Api.Db;

/// <summary>
/// Persists wallet state and enqueues the outbox event in ONE Npgsql transaction (UPSERT wallet_state + INSERT wallet_outbox, one fsync, two writes).
/// <c>LoadAsync</c> is a single PK SELECT; missing rows return null (grain treats null as "not initialised"). JSONB columns use <see cref="WalletStateJsonContext"/> so the hot path skips reflection.
/// </summary>
internal sealed class PostgresWalletStateStore(NpgsqlDataSource dataSource) : IWalletStateStore
{
    private const string LoadSql = """
        SELECT balance_amount, balance_currency, recent_operations, operation_order
        FROM wallet_state
        WHERE player_id = @player_id
        """;

    private const string UpsertStateSql = """
        INSERT INTO wallet_state
            (player_id, balance_amount, balance_currency, recent_operations, operation_order, updated_at)
        VALUES
            (@player_id, @balance_amount, @balance_currency, @recent::jsonb, @order::jsonb, NOW())
        ON CONFLICT (player_id) DO UPDATE SET
            balance_amount    = EXCLUDED.balance_amount,
            balance_currency  = EXCLUDED.balance_currency,
            recent_operations = EXCLUDED.recent_operations,
            operation_order   = EXCLUDED.operation_order,
            updated_at        = NOW()
        """;

    private const string InsertOutboxSql = """
        INSERT INTO wallet_outbox (event_id, event_type, player_id, payload)
        VALUES (@event_id, @event_type, @player_id, @payload::jsonb)
        """;

    public async Task<WalletState?> LoadAsync(string playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(LoadSql, connection);
        command.Parameters.Add(new NpgsqlParameter("player_id", NpgsqlDbType.Text) { Value = playerId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var balanceAmount = reader.GetDecimal(0);
        var balanceCurrency = reader.GetString(1).TrimEnd();
        var recentJson = reader.GetString(2);
        var orderJson = reader.GetString(3);

        var recent = JsonSerializer.Deserialize(recentJson, WalletStateJsonContext.Default.DictionaryGuidOperationResult)
            ?? new Dictionary<Guid, OperationResult>();
        var orderList = JsonSerializer.Deserialize(orderJson, WalletStateJsonContext.Default.ListGuid)
            ?? new List<Guid>();

        return new WalletState
        {
            Balance = new Money(balanceAmount, balanceCurrency),
            Initialized = true,
            RecentOperations = recent,
            OperationOrder = new Queue<Guid>(orderList),
        };
    }

    public async Task SaveAsync(string playerId, WalletState state, IWalletEvent walletEvent, CancellationToken cancellationToken = default)
    {
        var recentJson = JsonSerializer.Serialize(state.RecentOperations, WalletStateJsonContext.Default.DictionaryGuidOperationResult);
        var orderJson = JsonSerializer.Serialize(state.OperationOrder.ToList(), WalletStateJsonContext.Default.ListGuid);
        var payloadJson = JsonSerializer.Serialize(walletEvent, walletEvent.GetType(), WalletStateJsonContext.Default);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var stateCmd = new NpgsqlCommand(UpsertStateSql, connection, transaction))
            {
                stateCmd.Parameters.Add(new NpgsqlParameter("player_id", NpgsqlDbType.Text) { Value = playerId });
                stateCmd.Parameters.Add(new NpgsqlParameter("balance_amount", NpgsqlDbType.Numeric) { Value = state.Balance.Amount });
                stateCmd.Parameters.Add(new NpgsqlParameter("balance_currency", NpgsqlDbType.Char) { Value = state.Balance.Currency });
                stateCmd.Parameters.Add(new NpgsqlParameter("recent", NpgsqlDbType.Jsonb) { Value = recentJson });
                stateCmd.Parameters.Add(new NpgsqlParameter("order", NpgsqlDbType.Jsonb) { Value = orderJson });
                await stateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var outboxCmd = new NpgsqlCommand(InsertOutboxSql, connection, transaction))
            {
                outboxCmd.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Uuid) { Value = walletEvent.EventId });
                outboxCmd.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = walletEvent.GetType().Name });
                outboxCmd.Parameters.Add(new NpgsqlParameter("player_id", NpgsqlDbType.Text) { Value = walletEvent.PlayerId });
                outboxCmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payloadJson });
                await outboxCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
