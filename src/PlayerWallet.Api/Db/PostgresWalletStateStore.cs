using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PlayerWallet.Contracts;
using PlayerWallet.Grains;

namespace PlayerWallet.Api.Db;

/// <summary>
/// v2.1 hot-path store. Persists balance + outbox event in ONE round-trip via a CTE (balance UPSERT + outbox INSERT in a single SQL statement, one implicit transaction, one fsync).
/// The idempotency cache is NO LONGER written on every mutation. It stays in grain memory and is flushed via <see cref="PersistCacheAsync"/> from <c>WalletGrain.OnDeactivateAsync</c>. Trade: cache lost on process crash, retries that cross both crash + grain re-activation will re-execute. Win: removed JSONB write amplification (10s of KB per mutation as cache filled).
/// <see cref="LoadAsync"/> still reads the cache columns so a clean re-activation preserves dedupe.
/// </summary>
internal sealed class PostgresWalletStateStore(NpgsqlDataSource dataSource) : IWalletStateStore
{
    private const string LoadSql = """
        SELECT balance_amount, balance_currency, recent_operations, operation_order
        FROM wallet_state
        WHERE player_id = @player_id
        """;

    // Single-statement CTE: the UPSERT runs as the CTE; the final INSERT into wallet_outbox is the
    // statement Postgres returns. Implicit single transaction, one fsync (wallet_state only;
    // wallet_outbox is UNLOGGED). The cache columns are not touched on the hot path; existing values
    // are retained on UPDATE, defaults '{}' / '[]' apply on INSERT.
    private const string SaveSql = """
        WITH state_upsert AS (
            INSERT INTO wallet_state
                (player_id, balance_amount, balance_currency, updated_at)
            VALUES
                (@player_id, @balance_amount, @balance_currency, NOW())
            ON CONFLICT (player_id) DO UPDATE SET
                balance_amount   = EXCLUDED.balance_amount,
                balance_currency = EXCLUDED.balance_currency,
                updated_at       = NOW()
            RETURNING player_id
        )
        INSERT INTO wallet_outbox (event_id, event_type, player_id, payload)
        SELECT @event_id, @event_type, player_id, @payload::jsonb FROM state_upsert
        """;

    private const string PersistCacheSql = """
        UPDATE wallet_state
        SET recent_operations = @recent::jsonb,
            operation_order   = @order::jsonb,
            updated_at        = NOW()
        WHERE player_id = @player_id
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
        var balanceCurrency = reader.GetString(1);
        var recentJson = reader.GetString(2);
        var orderJson = reader.GetString(3);

        var recent = JsonSerializer.Deserialize(recentJson, WalletStateJsonContext.Default.DictionaryGuidOperationResult)
            ?? new Dictionary<Guid, OperationResult>();
        var orderList = JsonSerializer.Deserialize(orderJson, WalletStateJsonContext.Default.ListGuid)
            ?? new List<Guid>();

        var orderLinked = new LinkedList<Guid>();
        var index = new Dictionary<Guid, LinkedListNode<Guid>>(orderList.Count);
        foreach (var id in orderList)
        {
            var node = orderLinked.AddLast(id);
            index[id] = node;
        }

        return new WalletState
        {
            Balance = new Money(balanceAmount, balanceCurrency),
            Initialized = true,
            RecentOperations = recent,
            OperationOrder = orderLinked,
            OperationOrderIndex = index,
        };
    }

    public async Task SaveAsync(string playerId, WalletState state, IWalletEvent walletEvent, CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(walletEvent, walletEvent.GetType(), WalletStateJsonContext.Default);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SaveSql, connection);

        command.Parameters.Add(new NpgsqlParameter("player_id", NpgsqlDbType.Text) { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter("balance_amount", NpgsqlDbType.Numeric) { Value = state.Balance.Amount });
        command.Parameters.Add(new NpgsqlParameter("balance_currency", NpgsqlDbType.Varchar) { Value = state.Balance.Currency });
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Uuid) { Value = walletEvent.EventId });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = walletEvent.GetType().Name });
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payloadJson });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PersistCacheAsync(string playerId, WalletState state, CancellationToken cancellationToken = default)
    {
        if (!state.Initialized)
        {
            return;
        }

        var recentJson = JsonSerializer.Serialize(state.RecentOperations, WalletStateJsonContext.Default.DictionaryGuidOperationResult);
        var orderJson = JsonSerializer.Serialize(state.OperationOrder.ToList(), WalletStateJsonContext.Default.ListGuid);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PersistCacheSql, connection);
        command.Parameters.Add(new NpgsqlParameter("player_id", NpgsqlDbType.Text) { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter("recent", NpgsqlDbType.Jsonb) { Value = recentJson });
        command.Parameters.Add(new NpgsqlParameter("order", NpgsqlDbType.Jsonb) { Value = orderJson });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
