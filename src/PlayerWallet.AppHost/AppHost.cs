var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL backs Orleans grain state via the AdoNet provider. Server args tune for sustained write load; synchronous_commit stays ON (default) because this is a wallet ledger.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(isReadOnly: false)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithArgs("-c", "max_connections=500",
              "-c", "shared_buffers=512MB",
              "-c", "work_mem=16MB",
              "-c", "effective_cache_size=1GB",
              "-c", "checkpoint_timeout=15min",
              "-c", "max_wal_size=2GB");

var orleansDb = postgres.AddDatabase("orleans");

// Single-broker Kafka via the official Confluent local image. The wallet.events
// topic itself is created explicitly by KafkaTopicInitializer in the API on
// startup (lands in PR #6) rather than relying on broker auto-create defaults.
// KafkaUI is wired so the Aspire dashboard surfaces a browser view of
// wallet.events during the demo.
var kafka = builder.AddKafka("kafka")
    .WithKafkaUI()
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddProject<Projects.PlayerWallet_Api>("api")
    .WithReference(orleansDb)
    .WithReference(kafka)
    .WaitFor(orleansDb)
    .WaitFor(kafka);

builder.Build().Run();
