namespace GrainWallet.Contracts;

/// <summary>Result of a wallet mutation. Cached in the per-grain idempotency window so a retried <c>operationId</c> returns the same answer.</summary>
[GenerateSerializer]
[Immutable]
public sealed record OperationResult(
    [property: Id(0)] bool Succeeded,
    [property: Id(1)] Money Balance,
    [property: Id(2)] string? RejectionReason,
    [property: Id(3)] RejectionCode RejectionCode,
    [property: Id(4)] DateTimeOffset OccurredAt)
{
    public static OperationResult Success(Money balance, DateTimeOffset occurredAt) =>
        new(true, balance, RejectionReason: null, RejectionCode.None, occurredAt);

    public static OperationResult Reject(
        Money balance,
        RejectionCode code,
        string reason,
        DateTimeOffset occurredAt) =>
        new(false, balance, reason, code, occurredAt);
}

[GenerateSerializer]
public enum RejectionCode
{
    None = 0,
    InsufficientFunds = 1,
    CurrencyMismatch = 2,
    InvalidAmount = 3,
    OutboxFull = 4,
}
