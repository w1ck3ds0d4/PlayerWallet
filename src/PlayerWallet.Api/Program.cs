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
    tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1))));

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IWalletEventPublisher, NoOpWalletEventPublisher>();

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();

    if (usingPostgres)
    {
        silo.AddAdoNetGrainStorage("WalletStorage", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = orleansConnectionString!;
        });
    }
    else
    {
        silo.AddMemoryGrainStorage("WalletStorage");
    }
});

builder.Services.AddHealthChecks()
    .AddCheck<OrleansClusterReadinessCheck>("orleans", tags: ["ready"])
    .AddCheck<WalletEventPublisherReadinessCheck>("wallet-event-publisher", tags: ["ready"]);

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

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync();

public partial class Program;

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
