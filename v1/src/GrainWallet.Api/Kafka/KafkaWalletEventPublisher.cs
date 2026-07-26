using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using GrainWallet.Api.Telemetry;
using GrainWallet.Contracts;
using GrainWallet.Grains;

namespace GrainWallet.Api.Kafka;

/// <summary>
/// Confluent.Kafka producer for wallet events.
/// Single-broker dev/bench config: <c>Acks=Leader</c> + <c>Idempotence=off</c> + <c>MaxInFlight=100</c>; production reverts to <c>Acks=All</c> + idempotent producer on a 3-broker cluster.
/// Wraps every <c>Produce</c> in a <see cref="ActivityKind.Producer"/> <see cref="Activity"/> and injects <c>traceparent</c>/<c>tracestate</c> into Kafka headers so the distributed trace spans HTTP -&gt; grain -&gt; Kafka. Partition key is <c>playerId</c> so per-player events stay ordered.
/// </summary>
internal sealed class KafkaWalletEventPublisher : IWalletEventPublisher, IAsyncDisposable, IHealthCheck
{
    public const string TopicName = "wallet.events";

    public static readonly ActivitySource ActivitySource = new("GrainWallet.Kafka.Producer", "1.0.0");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolverChain = { WalletJsonContext.Default },
    };

    private readonly IProducer<string, byte[]> _producer;
    private readonly ILogger<KafkaWalletEventPublisher> _logger;
    private volatile bool _isHealthy = true;
    private string _lastError = string.Empty;

    public KafkaWalletEventPublisher(
        IOptions<KafkaWalletEventPublisherOptions> options,
        ILogger<KafkaWalletEventPublisher> logger)
    {
        _logger = logger;

        // Single-broker dev/bench config: Acks=Leader (no replicas to ack on a 1-broker cluster), idempotence off so MaxInFlight can lift past the idempotent-mode cap of 5 and the producer can keep many partial batches in flight. Production reverts to Acks.All + idempotent on a 3-broker cluster.
        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            Acks = Acks.Leader,
            EnableIdempotence = false,
            MaxInFlight = 100,
            LingerMs = 5,
            BatchSize = 64 * 1024,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = 10_000,
            ClientId = "GrainWallet.Api",
            // In-process buffer lifted so sustained 1000 rps does not stall on BufferQueueFull while the broker is acking earlier batches.
            QueueBufferingMaxMessages = 500_000,
            QueueBufferingMaxKbytes = 65_536,
        };

        _producer = new ProducerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) =>
            {
                if (error.IsFatal)
                {
                    _isHealthy = false;
                    _lastError = error.Reason;
                    _logger.LogError("Kafka producer fatal error: {Reason}", error.Reason);
                }
                else
                {
                    _logger.LogWarning("Kafka producer error (non-fatal): {Reason}", error.Reason);
                }
            })
            .Build();
    }

    public async Task<bool> PublishAsync(IWalletEvent walletEvent, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity(
            $"publish {TopicName}",
            ActivityKind.Producer);

        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", TopicName);
        activity?.SetTag("messaging.kafka.message.key", walletEvent.PlayerId);
        activity?.SetTag("wallet.event_type", walletEvent.GetType().Name);
        activity?.SetTag("wallet.player_id", walletEvent.PlayerId);
        activity?.SetTag("wallet.operation_id", walletEvent.OperationId.ToString());

        var payload = JsonSerializer.SerializeToUtf8Bytes(walletEvent, walletEvent.GetType(), JsonOptions);
        var message = new Message<string, byte[]>
        {
            Key = walletEvent.PlayerId,
            Value = payload,
            Headers = BuildHeaders(activity),
        };

        try
        {
            var result = await _producer.ProduceAsync(TopicName, message, cancellationToken);
            _isHealthy = true;
            activity?.SetTag("messaging.kafka.message.offset", result.Offset.Value);
            activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
            return true;
        }
        catch (ProduceException<string, byte[]> ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Error.Reason);
            _logger.LogWarning(ex, "Kafka publish failed for event {EventId}: {Reason}", walletEvent.EventId, ex.Error.Reason);
            if (ex.Error.IsFatal)
            {
                _isHealthy = false;
                _lastError = ex.Error.Reason;
            }
            return false;
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_isHealthy
            ? HealthCheckResult.Healthy($"Kafka producer connected (topic {TopicName}).")
            : HealthCheckResult.Unhealthy($"Kafka producer degraded: {_lastError}"));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Producer flush threw on shutdown");
        }
        _producer.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>Builds Kafka headers with the W3C trace context from the current <see cref="Activity"/>; consumers parse <c>traceparent</c>/<c>tracestate</c> back into their own activity.</summary>
    internal static Headers BuildHeaders(Activity? activity)
    {
        var headers = new Headers();
        if (activity is null)
        {
            return headers;
        }

        var traceparent = activity.Id;
        if (!string.IsNullOrEmpty(traceparent))
        {
            headers.Add("traceparent", Encoding.UTF8.GetBytes(traceparent));
        }

        var tracestate = activity.TraceStateString;
        if (!string.IsNullOrEmpty(tracestate))
        {
            headers.Add("tracestate", Encoding.UTF8.GetBytes(tracestate));
        }

        return headers;
    }
}

public sealed class KafkaWalletEventPublisherOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
}
