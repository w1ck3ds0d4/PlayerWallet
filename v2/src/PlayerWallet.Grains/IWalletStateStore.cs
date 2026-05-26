using PlayerWallet.Contracts;

namespace PlayerWallet.Grains;

/// <summary>
/// State backend for the wallet grain. Replaces <c>IPersistentState&lt;WalletState&gt;</c> so a mutation can persist state AND enqueue the outbox event in one Postgres round-trip (one CTE statement, one fsync).
/// <see cref="LoadAsync"/> returns <c>null</c> for an unknown player (grain treats null as new). <see cref="SaveAsync"/> commits balance + event atomically. Per-player ordering is Orleans turn-based concurrency, not the store.
/// v2.1: idempotency cache is no longer persisted on every mutation. <see cref="PersistCacheAsync"/> is called from <c>WalletGrain.OnDeactivateAsync</c> so the cache survives clean deactivations. On crash the in-flight cache is lost; retries within seconds normally hit the same activation and dedupe before any persistence is involved.
/// </summary>
public interface IWalletStateStore
{
    Task<WalletState?> LoadAsync(string playerId, CancellationToken cancellationToken = default);

    Task SaveAsync(string playerId, WalletState state, IWalletEvent walletEvent, CancellationToken cancellationToken = default);

    Task PersistCacheAsync(string playerId, WalletState state, CancellationToken cancellationToken = default);
}
