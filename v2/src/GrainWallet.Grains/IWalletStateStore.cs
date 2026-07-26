using GrainWallet.Contracts;

namespace GrainWallet.Grains;

/// <summary>
/// State backend for the wallet grain. Replaces <c>IPersistentState&lt;WalletState&gt;</c> so a mutation can persist state AND enqueue the outbox event in one Postgres round-trip (one CTE statement, one fsync).
/// <see cref="LoadAsync"/> returns <c>null</c> for an unknown player. <see cref="SaveAsync"/> atomically commits a versioned state, a durable operation receipt, and the outbox event.
/// </summary>
public interface IWalletStateStore
{
    Task<WalletState?> LoadAsync(string playerId, CancellationToken cancellationToken = default);

    Task<WalletStoreSaveResult> SaveAsync(
        string playerId,
        long expectedVersion,
        WalletState state,
        OperationResult result,
        IWalletEvent walletEvent,
        CancellationToken cancellationToken = default);
}

public sealed record WalletStoreSaveResult(
    WalletStoreSaveStatus Status,
    OperationResult? Result,
    long Version);

public enum WalletStoreSaveStatus
{
    Applied,
    Duplicate,
    Conflict,
    OperationMismatch,
}
