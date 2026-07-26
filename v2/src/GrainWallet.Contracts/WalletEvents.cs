using System.Text.Json.Serialization;

namespace GrainWallet.Contracts;

/// <summary>Marker for every event the wallet publishes to Kafka. Polymorphic JSON via <c>$type</c> discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FundsAdded), nameof(FundsAdded))]
[JsonDerivedType(typeof(FundsDeducted), nameof(FundsDeducted))]
[JsonDerivedType(typeof(OperationRejected), nameof(OperationRejected))]
public interface IWalletEvent
{
    Guid EventId { get; }
    string PlayerId { get; }
    Guid OperationId { get; }
    DateTimeOffset OccurredAt { get; }
}

[GenerateSerializer]
[Immutable]
public sealed record FundsAdded(
    [property: Id(0)] Guid EventId,
    [property: Id(1)] string PlayerId,
    [property: Id(2)] Guid OperationId,
    [property: Id(3)] Money Amount,
    [property: Id(4)] Money BalanceAfter,
    [property: Id(5)] DateTimeOffset OccurredAt) : IWalletEvent;

[GenerateSerializer]
[Immutable]
public sealed record FundsDeducted(
    [property: Id(0)] Guid EventId,
    [property: Id(1)] string PlayerId,
    [property: Id(2)] Guid OperationId,
    [property: Id(3)] Money Amount,
    [property: Id(4)] Money BalanceAfter,
    [property: Id(5)] DateTimeOffset OccurredAt) : IWalletEvent;

/// <summary>v2: emitted only for state-dependent rejections (InsufficientFunds today; OutboxFull is transient and not persisted). Renamed from <c>DeductionRejected</c> because v1 also fired this for add-funds rejections, which the type name misrepresented.</summary>
[GenerateSerializer]
[Immutable]
public sealed record OperationRejected(
    [property: Id(0)] Guid EventId,
    [property: Id(1)] string PlayerId,
    [property: Id(2)] Guid OperationId,
    [property: Id(3)] Money RequestedAmount,
    [property: Id(4)] Money CurrentBalance,
    [property: Id(5)] RejectionCode Reason,
    [property: Id(6)] DateTimeOffset OccurredAt) : IWalletEvent;
