using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using PlayerWallet.Api.Db;
using PlayerWallet.Api.Endpoints;
using PlayerWallet.Api.Kafka;
using PlayerWallet.Api.Telemetry;
using PlayerWallet.Grains;
using PlayerWallet.Grains.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Aspire injects ConnectionStrings:orleans when the AppHost runs; absent in
// component tests and standalone runs.
var orleansConnectionString = builder.Configuration.GetConnectionString("orleans");
var usingPostgres = !string.IsNullOrWhiteSpace(orleansConnectionString);

if (usingPostgres)
{
    DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, WalletJsonContext.Default);
});

builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(WalletMeters.MeterName));
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
    tracing
        .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1)))
        .AddSource(WalletGrain.ActivitySource.Name)
        .AddSource(PostgresWalletStateStore.ActivitySource.Name));

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

// Outbox back-pressure gate: drainer publishes pending-row counts here; the wallet grain reads it before every mutation and returns 503 OutboxFull when the cap is breached. Configurable via Wallet:OutboxCap.
var outboxCap = builder.Configuration.GetValue<int?>("Wallet:OutboxCap") ?? OutboxBackpressureGate.DefaultCap;
builder.Services.AddSingleton(new OutboxBackpressureGate(outboxCap));

// When Aspire injects ConnectionStrings:kafka, register the real producer
// (which also implements IHealthCheck). Otherwise fall back to the NoOp
// publisher so component tests and pre-AppHost runs still work.
var kafkaConnectionString = builder.Configuration.GetConnectionString("kafka");
var usingKafka = !string.IsNullOrWhiteSpace(kafkaConnectionString);

if (usingKafka)
{
    builder.Services.Configure<KafkaWalletEventPublisherOptions>(o =>
        o.BootstrapServers = kafkaConnectionString!);
    builder.Services.AddSingleton<KafkaWalletEventPublisher>();
    builder.Services.AddSingleton<IWalletEventPublisher>(sp => sp.GetRequiredService<KafkaWalletEventPublisher>());
    builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
        tracing.AddSource(KafkaWalletEventPublisher.ActivitySource.Name));
    // Pre-create wallet.events explicitly so the partition count is owned by
    // the service rather than inferred from broker defaults.
    builder.Services.AddHostedService<KafkaTopicInitializer>();
}
else
{
    builder.Services.AddSingleton<IWalletEventPublisher, NoOpWalletEventPublisher>();
}

// Custom state store: PostgresWalletStateStore commits state + outbox row in one transaction; WalletOutboxDrainer ships outbox to Kafka off-thread. Without Postgres, InMemoryWalletStateStore forwards events to the publisher synchronously so test assertions still see them.
if (usingPostgres)
{
    // v2.1 perf: Min Pool Size dropped from 20 -> 4. Fewer idle connections mean each pooled connection sees MORE requests, so Npgsql's server-side AutoPrepare reaches its 5-call warmup threshold per statement template much sooner. Max Pool Size stays at 300 so burst capacity is unchanged.
    var tunedStoreConnectionString = AppendIfMissing(
        orleansConnectionString!,
        "Maximum Pool Size=300;Minimum Pool Size=4;Pooling=true;Connection Idle Lifetime=60;Max Auto Prepare=10;Auto Prepare Min Usages=5");
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(tunedStoreConnectionString));
    builder.Services.AddSingleton<IWalletStateStore, PostgresWalletStateStore>();
    builder.Services.AddHostedService<WalletOutboxDrainer>();
}
else
{
    builder.Services.AddSingleton<IWalletStateStore, InMemoryWalletStateStore>();
}

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
});

var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck<OrleansClusterReadinessCheck>("orleans", tags: ["ready"])
    .AddCheck<WalletEventPublisherReadinessCheck>("wallet-event-publisher", tags: ["ready"]);

if (usingKafka)
{
    healthChecks.AddCheck<KafkaWalletEventPublisher>("kafka-producer", tags: ["ready"]);
}

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<WalletOpenApiSchemaTransformer>();
});

var app = builder.Build();

// Schema bootstrap: Development by default, opt-in via Wallet:BootstrapSchema
// in any other environment so a deployment pipeline owns the schema in
// staging or prod.
var shouldBootstrapSchema = usingPostgres &&
    (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Wallet:BootstrapSchema"));

if (shouldBootstrapSchema)
{
    var bootstrapLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SchemaBootstrap");
    await SchemaBootstrap.EnsureOrleansSchemaAsync(orleansConnectionString!, bootstrapLogger, app.Lifetime.ApplicationStopping);
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "PlayerWallet API";
        options.WithTheme(ScalarTheme.Mars);
    });
}

app.MapWalletEndpoints();

// v2.3 admin endpoint: explicit reset of wallet_outbox between bench sessions so accumulated rows + autovacuum churn from prior runs don't contaminate the next bench. Wired only when Postgres is configured (no-op in test/InMemory mode). Dev-only by design; if a future deployment exposes this it needs authn/authz.
if (usingPostgres && app.Environment.IsDevelopment())
{
    app.MapPost("/admin/reset-outbox", async (NpgsqlDataSource dataSource, ILogger<Program> logger, CancellationToken ct) =>
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE TABLE wallet_outbox RESTART IDENTITY; VACUUM ANALYZE wallet_outbox;", connection);
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync(ct);
        logger.LogInformation("wallet_outbox truncated + vacuumed by /admin/reset-outbox.");
        return Results.Ok(new { reset = "wallet_outbox", at = DateTimeOffset.UtcNow });
    })
    .WithTags("Admin")
    .WithSummary("Dev-only: TRUNCATE + VACUUM wallet_outbox for a clean bench slate.");
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync();

public partial class Program
{
    /// <summary>Appends keys to a connection string only when Aspire did not already set them.</summary>
    internal static string AppendIfMissing(string connectionString, string additions)
    {
        var existing = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd = additions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part =>
            {
                var key = part.Split('=', 2)[0].Trim();
                return !existing.Contains(key);
            });

        var separator = connectionString.TrimEnd().EndsWith(';') ? string.Empty : ";";
        return connectionString + separator + string.Join(';', toAdd);
    }
}

/// <summary>Readiness probe: green once the local Orleans cluster client is connected.</summary>
internal sealed class OrleansClusterReadinessCheck(IClusterClient cluster) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(cluster is null
            ? HealthCheckResult.Unhealthy("Orleans cluster client is not registered.")
            : HealthCheckResult.Healthy("Orleans cluster client is registered."));
    }
}

/// <summary>Readiness probe for the event publisher. The Kafka publisher surfaces producer-disconnect state here; the NoOp publisher is always ready.</summary>
internal sealed class WalletEventPublisherReadinessCheck(IWalletEventPublisher publisher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(publisher is null
            ? HealthCheckResult.Unhealthy("No wallet event publisher registered.")
            : HealthCheckResult.Healthy($"Publisher: {publisher.GetType().Name}"));
    }
}
