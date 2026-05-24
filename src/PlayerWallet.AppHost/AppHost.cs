// AssemblyName is what Aspire 13.x surfaces as the application name in the dashboard title bar and OTel resource.service.name. v1's AppHost uses the default (the actual PlayerWallet.AppHost assembly name); v2 explicitly sets "PlayerWalletv2" so when both dashboards are open in browser tabs the titles distinguish them.
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    AssemblyName = "PlayerWalletv2",
});

// Resource names stay as "postgres" / "kafka" so the API's GetConnectionString("orleans") / GetConnectionString("kafka") keys are unchanged. To run v2 side-by-side with v1 we need DISTINCT Docker container names and volume names; set PLAYERWALLET_INSTANCE_TAG to anything non-empty (default "v2") and Aspire will create v2-specific containers and volumes so the two stacks don't fight.
var instanceTag = Environment.GetEnvironmentVariable("PLAYERWALLET_INSTANCE_TAG") ?? "v2";

// PostgreSQL backs the wallet state store and outbox. v2 runs synchronous_commit=on by default so headline numbers reflect durable single-node performance. Set PLAYERWALLET_PG_SYNC=off to opt back into the v1 dev-bench trade-off.
var pgSyncCommit = Environment.GetEnvironmentVariable("PLAYERWALLET_PG_SYNC") == "off" ? "off" : "on";
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume($"playerwallet-{instanceTag}-postgres-data", isReadOnly: false)
    .WithContainerName($"playerwallet-{instanceTag}-postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithArgs("-c", "max_connections=500",
              "-c", "shared_buffers=512MB",
              "-c", "work_mem=16MB",
              "-c", "effective_cache_size=1GB",
              "-c", "checkpoint_timeout=15min",
              "-c", "max_wal_size=2GB",
              "-c", $"synchronous_commit={pgSyncCommit}",
              "-c", "wal_writer_delay=10ms");

var orleansDb = postgres.AddDatabase("orleans");

// PLAYERWALLET_DISABLE_KAFKA=1 skips Kafka registration so the API falls back to NoOpWalletEventPublisher; used to isolate broker round-trip while perf-debugging.
var disableKafka = Environment.GetEnvironmentVariable("PLAYERWALLET_DISABLE_KAFKA") == "1";

// Pin the API HTTP endpoint so the load harness and manual smoke tests can target a stable URL. Override via WALLET_API_HOST_PORT.
var apiHostPort = int.TryParse(Environment.GetEnvironmentVariable("WALLET_API_HOST_PORT"), out var apiPort) ? apiPort : 5000;
var api = builder.AddProject<Projects.PlayerWallet_Api>("api")
    .WithHttpEndpoint(port: apiHostPort, name: "http")
    .WithReference(orleansDb)
    .WaitFor(orleansDb);

if (!disableKafka)
{
    // Host port pinned and container recreated each run so KAFKA_ADVERTISED_LISTENERS matches the actual Docker bind (Aspire 13.3.3 + persistent lifetime each cause an advertised-vs-bound port mismatch on their own). Override via WALLET_KAFKA_HOST_PORT.
    var kafkaHostPort = int.TryParse(Environment.GetEnvironmentVariable("WALLET_KAFKA_HOST_PORT"), out var p) ? p : 19092;
    var kafka = builder.AddKafka("kafka", port: kafkaHostPort)
        .WithContainerName($"playerwallet-{instanceTag}-kafka")
        .WithKafkaUI();

    api.WithReference(kafka).WaitFor(kafka);
}

builder.Build().Run();
