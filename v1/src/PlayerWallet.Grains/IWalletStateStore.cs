using PlayerWallet.Contracts;

namespace PlayerWallet.Grains;

/// <summary>
/// State backend for the wallet grain. Replaces <c>IPersistentState&lt;WalletState&gt;</c> so a mutation can persist state AND enqueue the outbox event in one Postgres transaction (one fsync, two writes).
/// <see cref="LoadAsync"/> returns <c>null</c> for an unknown player (grain treats null as new). <see cref="SaveAsync"/> commits state + event atomically. Per-player ordering is Orleans turn-based concurrency, not the store.
/// </summary>
public interface IWalletStateStore
{
    Task<WalletState?> LoadAsync(string playerId, CancellationToken cancellationToken = default);

    Task SaveAsync(string playerId, WalletState state, IWalletEvent walletEvent, CancellationToken cancellationToken = default);
}
