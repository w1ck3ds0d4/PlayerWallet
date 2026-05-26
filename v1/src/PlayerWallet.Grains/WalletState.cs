using PlayerWallet.Contracts;

namespace PlayerWallet.Grains;

/// <summary>Persisted state for one player wallet. Idempotency cache LRU-evicts at <see cref="IdempotencyCacheCap"/>; outbox hard-caps at <see cref="OutboxCap"/> and triggers 503 back-pressure when full.</summary>
[GenerateSerializer]
public sealed class WalletState
{
    public const int IdempotencyCacheCap = 256;
    public const int OutboxCap = 64;

    [Id(0)]
    public Money Balance { get; set; } = Money.Zero("EUR");

    [Id(1)]
    public bool Initialized { get; set; }

    [Id(2)]
    public Dictionary<Guid, OperationResult> RecentOperations { get; set; } = [];

    [Id(3)]
    public Queue<Guid> OperationOrder { get; set; } = new();

    [Id(4)]
    public List<PendingEvent> Outbox { get; set; } = [];
}

[GenerateSerializer]
[Immutable]
public sealed record PendingEvent(
    [property: Id(0)] Guid EventId,
    [property: Id(1)] string EventType,
    [property: Id(2)] IWalletEvent Payload);
