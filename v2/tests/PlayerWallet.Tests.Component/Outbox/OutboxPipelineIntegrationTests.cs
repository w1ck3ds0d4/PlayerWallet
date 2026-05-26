using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PlayerWallet.Api.Db;
using PlayerWallet.Api.Kafka;
using PlayerWallet.Contracts;
using PlayerWallet.Grains;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace PlayerWallet.Tests.Component.Outbox;

/// <summary>
/// End-to-end test of the outbox pipeline:
/// <c>PostgresWalletStateStore.SaveAsync</c> -&gt; <c>wallet_outbox</c> -&gt; <c>WalletOutboxDrainer</c> -&gt; <c>KafkaWalletEventPublisher</c> -&gt; Kafka topic -&gt; consumer in this test.
/// Brings up real Postgres + Kafka via Testcontainers and wires the production components manually. Pushes N events through the store and asserts the consumer sees exactly N with no duplicates, every operationId once, and <c>wallet_outbox.pending = 0</c> after the drain budget. Guards the at-least-once delivery contract against future architectural changes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OutboxPipelineIntegrationTests : IAsyncLifetime
{
    private const int EventCount = 50;
    private const string TopicName = KafkaWalletEventPublisher.TopicName;
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(60);

    private PostgreSqlContainer _postgres = null!;
    private KafkaContainer _kafka = null!;
    private NpgsqlDataSource _dataSource = null!;
    private KafkaWalletEventPublisher _publisher = null!;
    private WalletOutboxDrainer _drainer = null!;
    private CancellationTokenSource _drainerStop = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("walletdb")
            .Build();
        _kafka = new KafkaBuilder()
            .WithImage("confluentinc/cp-kafka:7.6.1")
            .Build();

        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());

        await BootstrapSchemaAsync(_postgres.GetConnectionString());

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());

        _publisher = new KafkaWalletEventPublisher(
            Options.Create(new KafkaWalletEventPublisherOptions
            {
                BootstrapServers = _kafka.GetBootstrapAddress(),
            }),
            NullLogger<KafkaWalletEventPublisher>.Instance);

        await EnsureTopicAsync(_kafka.GetBootstrapAddress());

        _drainer = new WalletOutboxDrainer(_dataSource, _publisher, new OutboxBackpressureGate(), NullLogger<WalletOutboxDrainer>.Instance);
        _drainerStop = new CancellationTokenSource();
        await _drainer.StartAsync(_drainerStop.Token);
    }

    public async Task DisposeAsync()
    {
        await _drainerStop.CancelAsync();
        await _drainer.StopAsync(CancellationToken.None);
        await _publisher.DisposeAsync();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask());
        _drainerStop.Dispose();
    }

    [Fact]
    public async Task End_To_End_Pipeline_Delivers_Each_Event_Exactly_Once()
    {
        var store = new PostgresWalletStateStore(_dataSource);

        var balance = new Money(0m, "EUR");
        var produced = new List<FundsAdded>(EventCount);

        for (var i = 0; i < EventCount; i++)
        {
            var amount = new Money(1m, "EUR");
            balance = balance.Add(amount);
            var operationId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var walletEvent = new FundsAdded(
                EventId: Guid.NewGuid(),
                PlayerId: "integration-player",
                OperationId: operationId,
                Amount: amount,
                BalanceAfter: balance,
                OccurredAt: now);

            var state = new WalletState
            {
                Balance = balance,
                Initialized = true,
            };
            state.TrackOperation(operationId, OperationResult.Success(balance, now));

            await store.SaveAsync("integration-player", state, walletEvent);
            produced.Add(walletEvent);
        }

        var consumed = await ConsumeUntilAsync(EventCount, DrainBudget);

        Assert.Equal(EventCount, consumed.Count);

        var consumedIds = consumed.Select(e => e.EventId).ToHashSet();
        Assert.Equal(EventCount, consumedIds.Count);

        var producedIds = produced.Select(e => e.EventId).ToHashSet();
        Assert.True(producedIds.SetEquals(consumedIds),
            "Each produced event id should appear in the consumed stream exactly once.");

        var producedOperationIds = produced.Select(e => e.OperationId).ToHashSet();
        var consumedOperationIds = consumed.Select(e => e.OperationId).ToHashSet();
        Assert.True(producedOperationIds.SetEquals(consumedOperationIds),
            "Each produced operationId should appear in the consumed stream exactly once.");

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FILTER (WHERE published_at IS NULL) FROM wallet_outbox",
            connection);
        var pending = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(0, pending);
    }

    private async Task<List<FundsAdded>> ConsumeUntilAsync(int expected, TimeSpan budget)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            GroupId = $"integration-test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(TopicName);

        var consumed = new List<FundsAdded>(expected);
        var deadline = Stopwatch.StartNew();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolverChain = { WalletStateJsonContext.Default },
        };

        while (consumed.Count < expected && deadline.Elapsed < budget)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result?.Message?.Value is null)
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize<FundsAdded>(result.Message.Value, jsonOptions);
            if (evt is not null)
            {
                consumed.Add(evt);
            }
        }

        consumer.Close();
        return consumed;
    }

    private static async Task BootstrapSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var assembly = typeof(SchemaBootstrap).Assembly;
        var sql = ReadEmbeddedResource(assembly, "PlayerWallet.Api.Db.Schema.WalletStateAndOutbox.sql");

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task EnsureTopicAsync(string bootstrapServers)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = bootstrapServers,
        }).Build();

        try
        {
            await admin.CreateTopicsAsync(new[]
            {
                new Confluent.Kafka.Admin.TopicSpecification
                {
                    Name = TopicName,
                    NumPartitions = 1,
                    ReplicationFactor = 1,
                },
            });
        }
        catch (Confluent.Kafka.Admin.CreateTopicsException ex)
            when (ex.Results.All(r => r.Error.Code == global::Confluent.Kafka.ErrorCode.TopicAlreadyExists))
        {
            // already exists, no-op
        }
    }
}
