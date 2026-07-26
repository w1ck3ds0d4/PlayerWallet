using System.Collections.Concurrent;
using GrainWallet.Contracts;

namespace GrainWallet.Grains;

/// <summary>
/// In-memory store for component tests and non-Postgres hosts. Forwards each save's event to the registered <see cref="IWalletEventPublisher"/> synchronously so tests that assert on <c>Published</c> right after <c>AddFundsAsync</c> still see the event. Not durable.
/// </summary>
public sealed class InMemoryWalletStateStore(IWalletEventPublisher publisher) : IWalletStateStore
{
    private readonly ConcurrentDictionary<string, WalletState> _states = new();
    private readonly ConcurrentDictionary<(string PlayerId, Guid OperationId), OperationReceipt> _operations = new();

    public Task<WalletState?> LoadAsync(string playerId, CancellationToken cancellationToken = default)
    {
        if (_states.TryGetValue(playerId, out var existing))
        {
            return Task.FromResult<WalletState?>(Clone(existing));
        }
        return Task.FromResult<WalletState?>(null);
    }

    public async Task<WalletStoreSaveResult> SaveAsync(
        string playerId,
        long expectedVersion,
        WalletState state,
        OperationResult result,
        IWalletEvent walletEvent,
        CancellationToken cancellationToken = default)
    {
        if (_operations.TryGetValue((playerId, walletEvent.OperationId), out var existing))
        {
            var (operationType, amount) = RequestFor(walletEvent);
            var status = existing.OperationType == operationType && existing.Amount == amount
                ? WalletStoreSaveStatus.Duplicate
                : WalletStoreSaveStatus.OperationMismatch;
            return new(status, existing.Result, state.Version);
        }

        var currentVersion = _states.TryGetValue(playerId, out var current) ? current.Version : 0;
        if (currentVersion != expectedVersion)
        {
            return new(WalletStoreSaveStatus.Conflict, null, currentVersion);
        }

        var acknowledged = await publisher.PublishAsync(walletEvent, cancellationToken);
        if (!acknowledged)
        {
            throw new InvalidOperationException(
                $"Publisher refused event {walletEvent.EventId}; treat the mutation as failed.");
        }

        state.Version = expectedVersion + 1;
        _states[playerId] = Clone(state);
        var request = RequestFor(walletEvent);
        _operations[(playerId, walletEvent.OperationId)] = new(result, request.OperationType, request.Amount);
        return new(WalletStoreSaveStatus.Applied, result, state.Version);
    }

    /// <summary>Deep-copies the state on the boundary; <see cref="WalletState"/> has mutable collections and two grains for the same player must not share them.</summary>
    private static WalletState Clone(WalletState source)
    {
        var clone = new WalletState
        {
            Balance = source.Balance,
            Initialized = source.Initialized,
            Version = source.Version,
        };
        foreach (var id in source.OperationOrder)
        {
            if (source.RecentOperations.TryGetValue(id, out var result))
            {
                clone.TrackOperation(id, result);
            }
        }
        return clone;
    }

    private static (string OperationType, Money Amount) RequestFor(IWalletEvent walletEvent) => walletEvent switch
    {
        FundsAdded added => ("add", added.Amount),
        FundsDeducted deducted => ("deduct", deducted.Amount),
        OperationRejected rejected => ("deduct", rejected.RequestedAmount),
        _ => throw new InvalidOperationException($"Unsupported wallet event {walletEvent.GetType().Name}."),
    };

    private sealed record OperationReceipt(OperationResult Result, string OperationType, Money Amount);
}
