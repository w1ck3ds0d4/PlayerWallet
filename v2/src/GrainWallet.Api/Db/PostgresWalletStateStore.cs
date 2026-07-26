using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using GrainWallet.Contracts;
using GrainWallet.Grains;

namespace GrainWallet.Api.Db;

/// <summary>
/// Persists versioned state, a fixed-size durable operation receipt, and the outbox event in one CTE statement. The receipt makes a retry idempotent across crashes without restoring the JSONB write amplification removed in v2.1.
/// </summary>
internal sealed class PostgresWalletStateStore(NpgsqlDataSource dataSource) : IWalletStateStore
{
    /// <summary>OTel ActivitySource for store operations.</summary>
    public static readonly ActivitySource ActivitySource = new("GrainWallet.Api.Db");

    private const string LoadSql = """
        SELECT balance_amount, balance_currency, recent_operations, operation_order, initialized, version
        FROM wallet_state
        WHERE player_id = @player_id
        """;

    private const string SaveSql = """
        WITH state_write AS (
            INSERT INTO wallet_state
                (player_id, balance_amount, balance_currency, initialized, version, updated_at)
            SELECT @player_id, @balance_amount, @balance_currency, @initialized, 1, NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM wallet_operations
                WHERE player_id = @player_id AND operation_id = @operation_id)
            ON CONFLICT (player_id) DO UPDATE SET
                balance_amount   = EXCLUDED.balance_amount,
                balance_currency = EXCLUDED.balance_currency,
                initialized      = EXCLUDED.initialized,
                version          = wallet_state.version + 1,
                updated_at       = NOW()
            WHERE wallet_state.version = @expected_version
              AND NOT EXISTS (
                  SELECT 1 FROM wallet_operations
                  WHERE player_id = @player_id AND operation_id = @operation_id)
            RETURNING player_id, version
        ), operation_insert AS (
            INSERT INTO wallet_operations
                (player_id, operation_id, operation_type, amount, currency, result)
            SELECT @player_id, @operation_id, @operation_type, @amount, @currency, @result::jsonb
            FROM state_write
            ON CONFLICT (player_id, operation_id) DO NOTHING
            RETURNING result
        ), outbox_insert AS (
            INSERT INTO wallet_outbox (event_id, event_type, player_id, payload)
            SELECT @event_id, @event_type, @player_id, @payload::jsonb
            FROM operation_insert
        )
        SELECT 0 AS status, result::text, (SELECT version FROM state_write) AS version
        FROM operation_insert
        UNION ALL
        SELECT
            CASE WHEN operation_type = @operation_type
                       AND amount = @amount
                       AND currency = @currency
                 THEN 1 ELSE 3 END AS status,
            result::text,
            0 AS version
        FROM wallet_operations
        WHERE player_id = @player_id AND operation_id = @operation_id
        LIMIT 1
        """;

    public async Task<WalletState?> LoadAsync(string playerId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("wallet.store.load", ActivityKind.Client);
        activity?.SetTag("player_id", playerId);

        var openSw = Stopwatch.StartNew();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        activity?.SetTag("connection_open_ms", openSw.Elapsed.TotalMilliseconds);

        await using var command = new NpgsqlCommand(LoadSql, connection);
        command.Parameters.Add(new NpgsqlParameter("player_id", NpgsqlDbType.Text) { Value = playerId });

        var execSw = Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            activity?.SetTag("exec_ms", execSw.Elapsed.TotalMilliseconds);
            activity?.SetTag("hit", false);
            return null;
        }
        activity?.SetTag("exec_ms", execSw.Elapsed.TotalMilliseconds);
        activity?.SetTag("hit", true);

        var balanceAmount = reader.GetDecimal(0);
        var balanceCurrency = reader.GetString(1);
        var recentJson = reader.GetString(2);
        var orderJson = reader.GetString(3);
        var initialized = reader.GetBoolean(4);
        var version = reader.GetInt64(5);

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
            Initialized = initialized,
            RecentOperations = recent,
            OperationOrder = orderLinked,
            OperationOrderIndex = index,
            Version = version,
        };
    }

    public async Task<WalletStoreSaveResult> SaveAsync(
        string playerId,
        long expectedVersion,
        WalletState state,
        OperationResult result,
        IWalletEvent walletEvent,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("wallet.store.save", ActivityKind.Client);
        activity?.SetTag("player_id", playerId);
        activity?.SetTag("event.type", walletEvent.GetType().Name);

        var serSw = Stopwatch.StartNew();
        var payloadJson = JsonSerializer.Serialize(walletEvent, walletEvent.GetType(), WalletStateJsonContext.Default);
        var resultJson = JsonSerializer.Serialize(result, WalletStateJsonContext.Default.OperationResult);
        var (operationType, amount) = walletEvent switch
        {
            FundsAdded added => ("add", added.Amount),
            FundsDeducted deducted => ("deduct", deducted.Amount),
            OperationRejected rejected => ("deduct", rejected.RequestedAmount),
            _ => throw new InvalidOperationException($"Unsupported wallet event {walletEvent.GetType().Name}."),
        };
        activity?.SetTag("serialize_ms", serSw.Elapsed.TotalMilliseconds);

        var openSw = Stopwatch.StartNew();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        activity?.SetTag("connection_open_ms", openSw.Elapsed.TotalMilliseconds);

        await using var command = new NpgsqlCommand(SaveSql, connection);

        command.Parameters.Add(new NpgsqlParameter("player_id", NpgsqlDbType.Text) { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter("balance_amount", NpgsqlDbType.Numeric) { Value = state.Balance.Amount });
        command.Parameters.Add(new NpgsqlParameter("balance_currency", NpgsqlDbType.Varchar) { Value = state.Balance.Currency });
        command.Parameters.Add(new NpgsqlParameter("initialized", NpgsqlDbType.Boolean) { Value = state.Initialized });
        command.Parameters.Add(new NpgsqlParameter("expected_version", NpgsqlDbType.Bigint) { Value = expectedVersion });
        command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Uuid) { Value = walletEvent.OperationId });
        command.Parameters.Add(new NpgsqlParameter("operation_type", NpgsqlDbType.Text) { Value = operationType });
        command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = amount.Amount });
        command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Varchar) { Value = amount.Currency });
        command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = resultJson });
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Uuid) { Value = walletEvent.EventId });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = walletEvent.GetType().Name });
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payloadJson });

        var execSw = Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        activity?.SetTag("exec_ms", execSw.Elapsed.TotalMilliseconds);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(WalletStoreSaveStatus.Conflict, null, expectedVersion);
        }

        var status = reader.GetInt32(0) switch
        {
            0 => WalletStoreSaveStatus.Applied,
            1 => WalletStoreSaveStatus.Duplicate,
            3 => WalletStoreSaveStatus.OperationMismatch,
            _ => throw new InvalidOperationException("Unknown wallet save status."),
        };
        var durableResult = JsonSerializer.Deserialize(
            reader.GetString(1), WalletStateJsonContext.Default.OperationResult)
            ?? throw new InvalidOperationException("Wallet operation receipt has no result.");
        var version = reader.GetInt64(2);
        return new(status, durableResult, version);
    }
}
