using Npgsql;
using GrainWallet.Api.Db;
using GrainWallet.Contracts;
using GrainWallet.Grains;
using Testcontainers.PostgreSql;

namespace GrainWallet.Tests.Component.Outbox;

[Trait("Category", "Integration")]
public sealed class PostgresWalletStateStoreCorrectnessTests
{
    [Fact]
    public async Task Durable_Receipt_And_Version_Prevent_Replay_And_Stale_Overwrite()
    {
        await using var postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("walletdb")
            .Build();
        await postgres.StartAsync();
        await BootstrapSchemaAsync(postgres.GetConnectionString());
        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());

        var firstStore = new PostgresWalletStateStore(dataSource);
        var operationId = Guid.NewGuid();
        var first = CreateAdd("player", operationId, 10m, 10m);
        var applied = await firstStore.SaveAsync("player", 0, first.State, first.Result, first.Event);
        Assert.Equal(WalletStoreSaveStatus.Applied, applied.Status);

        // A fresh store models process death after commit and before any in-memory cache flush.
        var restartedStore = new PostgresWalletStateStore(dataSource);
        var replay = CreateAdd("player", operationId, 10m, 20m);
        var duplicate = await restartedStore.SaveAsync("player", 1, replay.State, replay.Result, replay.Event);
        Assert.Equal(WalletStoreSaveStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.Result, duplicate.Result);

        var stale = CreateAdd("player", Guid.NewGuid(), 5m, 5m);
        var staleWrite = await restartedStore.SaveAsync("player", 0, stale.State, stale.Result, stale.Event);
        Assert.Equal(WalletStoreSaveStatus.Conflict, staleWrite.Status);

        var loaded = await restartedStore.LoadAsync("player");
        Assert.NotNull(loaded);
        Assert.Equal(new Money(10m, "EUR"), loaded.Balance);
        Assert.Equal(1, loaded.Version);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT COUNT(*) FROM wallet_operations), " +
            "(SELECT COUNT(*) FROM wallet_outbox), " +
            "(SELECT relpersistence FROM pg_class WHERE oid = 'wallet_outbox'::regclass)",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal('p', reader.GetChar(2));
    }

    private static (WalletState State, OperationResult Result, FundsAdded Event) CreateAdd(
        string playerId,
        Guid operationId,
        decimal amount,
        decimal balance)
    {
        var now = DateTimeOffset.UtcNow;
        var money = new Money(amount, "EUR");
        var balanceAfter = new Money(balance, "EUR");
        return (
            new WalletState { Balance = balanceAfter, Initialized = true },
            OperationResult.Success(balanceAfter, now),
            new FundsAdded(Guid.NewGuid(), playerId, operationId, money, balanceAfter, now));
    }

    private static async Task BootstrapSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var assembly = typeof(SchemaBootstrap).Assembly;
        using var stream = assembly.GetManifestResourceStream("GrainWallet.Api.Db.Schema.WalletStateAndOutbox.sql")
            ?? throw new InvalidOperationException("Wallet schema resource not found.");
        using var reader = new StreamReader(stream);
        await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(), connection)
        {
            CommandTimeout = 120,
        };
        await command.ExecuteNonQueryAsync();
    }
}
