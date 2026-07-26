using System.Collections.Concurrent;
using GrainWallet.Contracts;

namespace GrainWallet.Grains;

/// <summary>
/// In-memory store for component tests and non-Postgres hosts. Forwards each save's event to the registered <see cref="IWalletEventPublisher"/> synchronously so tests that assert on <c>Published</c> right after <c>AddFundsAsync</c> still see the event. Not durable.
/// </summary>
public sealed class InMemoryWalletStateStore(IWalletEventPublisher publisher) : IWalletStateStore
{
    private readonly ConcurrentDictionary<string, WalletState> _states = new();

    public Task<WalletState?> LoadAsync(string playerId, CancellationToken cancellationToken = default)
    {
        if (_states.TryGetValue(playerId, out var existing))
        {
            return Task.FromResult<WalletState?>(Clone(existing));
        }
        return Task.FromResult<WalletState?>(null);
    }

    public async Task SaveAsync(string playerId, WalletState state, IWalletEvent walletEvent, CancellationToken cancellationToken = default)
    {
        _states[playerId] = Clone(state);
        var acknowledged = await publisher.PublishAsync(walletEvent, cancellationToken);
        if (!acknowledged)
        {
            throw new InvalidOperationException(
                $"Publisher refused event {walletEvent.EventId}; treat the mutation as failed.");
        }
    }

    /// <summary>Deep-copies the state on the boundary; <see cref="WalletState"/> has mutable collections and two grains for the same player must not share them.</summary>
    private static WalletState Clone(WalletState source) => new()
    {
        Balance = source.Balance,
        Initialized = source.Initialized,
        RecentOperations = new Dictionary<Guid, OperationResult>(source.RecentOperations),
        OperationOrder = new Queue<Guid>(source.OperationOrder),
    };
}
