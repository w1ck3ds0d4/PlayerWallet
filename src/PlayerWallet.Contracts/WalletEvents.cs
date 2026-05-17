using System.Text.Json.Serialization;

namespace PlayerWallet.Contracts;

/// <summary>Marker for every event the wallet publishes to Kafka. Polymorphic JSON via <c>$type</c> discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FundsAdded), nameof(FundsAdded))]
[JsonDerivedType(typeof(FundsDeducted), nameof(FundsDeducted))]
[JsonDerivedType(typeof(DeductionRejected), nameof(DeductionRejected))]
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

[GenerateSerializer]
[Immutable]
public sealed record DeductionRejected(
    [property: Id(0)] Guid EventId,
    [property: Id(1)] string PlayerId,
    [property: Id(2)] Guid OperationId,
    [property: Id(3)] Money RequestedAmount,
    [property: Id(4)] Money CurrentBalance,
    [property: Id(5)] RejectionCode Reason,
    [property: Id(6)] DateTimeOffset OccurredAt) : IWalletEvent;
