var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL backs Orleans grain state via the AdoNet provider. Server args tune for sustained write load.
// synchronous_commit=off is a dev/bench trade-off (commits land in the OS page cache, not fsync'd) and production flips it back ON once a synchronous standby replica owns the durability story.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(isReadOnly: false)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithArgs("-c", "max_connections=500",
              "-c", "shared_buffers=512MB",
              "-c", "work_mem=16MB",
              "-c", "effective_cache_size=1GB",
              "-c", "checkpoint_timeout=15min",
              "-c", "max_wal_size=2GB",
              "-c", "synchronous_commit=off",
              "-c", "wal_writer_delay=10ms");

var orleansDb = postgres.AddDatabase("orleans");

// PLAYERWALLET_DISABLE_KAFKA=1 skips Kafka registration so the API falls back to NoOpWalletEventPublisher; used to isolate broker round-trip while perf-debugging.
var disableKafka = Environment.GetEnvironmentVariable("PLAYERWALLET_DISABLE_KAFKA") == "1";

var api = builder.AddProject<Projects.PlayerWallet_Api>("api")
    .WithReference(orleansDb)
    .WaitFor(orleansDb);

if (!disableKafka)
{
    // Single-broker Kafka; wallet.events topic is created by KafkaTopicInitializer on API startup. KafkaUI surfaces the topic in the Aspire dashboard for the demo.
    var kafka = builder.AddKafka("kafka")
        .WithKafkaUI()
        .WithLifetime(ContainerLifetime.Persistent);

    api.WithReference(kafka).WaitFor(kafka);
}

builder.Build().Run();
