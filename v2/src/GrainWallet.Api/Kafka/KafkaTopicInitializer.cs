using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace GrainWallet.Api.Kafka;

/// <summary>Pre-creates <c>wallet.events</c> with the exact partition count via <c>AdminClient.CreateTopicsAsync</c> before the producer's first call. Idempotent: catches <c>TopicAlreadyExists</c>.</summary>
internal sealed class KafkaTopicInitializer(
    IOptions<KafkaWalletEventPublisherOptions> options,
    ILogger<KafkaTopicInitializer> logger) : IHostedService
{
    private const int Partitions = 6;
    private const short ReplicationFactor = 1;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapServers = options.Value.BootstrapServers;
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            logger.LogDebug("Kafka bootstrap servers not configured; skipping topic creation.");
            return;
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = bootstrapServers,
        }).Build();

        var spec = new TopicSpecification
        {
            Name = KafkaWalletEventPublisher.TopicName,
            NumPartitions = Partitions,
            ReplicationFactor = ReplicationFactor,
        };

        try
        {
            await admin.CreateTopicsAsync([spec]);
            logger.LogInformation(
                "Created Kafka topic {Topic} with {Partitions} partitions, replication factor {Replication}.",
                spec.Name, Partitions, ReplicationFactor);
        }
        catch (CreateTopicsException ex) when (ex.Results.Count == 1
            && ex.Results[0].Error.Code == global::Confluent.Kafka.ErrorCode.TopicAlreadyExists)
        {
            logger.LogInformation("Kafka topic {Topic} already exists; skipping create.", spec.Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
