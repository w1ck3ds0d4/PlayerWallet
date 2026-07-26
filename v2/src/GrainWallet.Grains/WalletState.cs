using GrainWallet.Contracts;

namespace GrainWallet.Grains;

/// <summary>Persisted state for one player wallet. Idempotency cache LRU-evicts at <see cref="IdempotencyCacheCap"/>; eviction order is recency, not insertion. Back-pressure for the outbox lives in <see cref="OutboxBackpressureGate"/>, not in grain state.</summary>
[GenerateSerializer]
public sealed class WalletState
{
    public const int IdempotencyCacheCap = 256;

    [Id(0)]
    public Money Balance { get; set; } = Money.Zero("EUR");

    [Id(1)]
    public bool Initialized { get; set; }

    [Id(2)]
    public Dictionary<Guid, OperationResult> RecentOperations { get; set; } = [];

    [Id(3)]
    public LinkedList<Guid> OperationOrder { get; set; } = new();

    [Id(4)]
    public Dictionary<Guid, LinkedListNode<Guid>> OperationOrderIndex { get; set; } = [];

    [Id(5)]
    public long Version { get; set; }

    /// <summary>Mark <paramref name="operationId"/> as most-recently-used. Idempotent; no-op for unknown ids.</summary>
    public void TouchOperation(Guid operationId)
    {
        if (!OperationOrderIndex.TryGetValue(operationId, out var node))
        {
            return;
        }
        OperationOrder.Remove(node);
        OperationOrder.AddLast(node);
    }

    /// <summary>Insert <paramref name="result"/> for <paramref name="operationId"/> and evict the least-recently-used entries past <see cref="IdempotencyCacheCap"/>.</summary>
    public void TrackOperation(Guid operationId, OperationResult result)
    {
        if (OperationOrderIndex.TryGetValue(operationId, out var existing))
        {
            OperationOrder.Remove(existing);
            OperationOrder.AddLast(existing);
            RecentOperations[operationId] = result;
            return;
        }

        RecentOperations[operationId] = result;
        var node = OperationOrder.AddLast(operationId);
        OperationOrderIndex[operationId] = node;

        while (OperationOrder.Count > IdempotencyCacheCap)
        {
            var evicted = OperationOrder.First!;
            OperationOrder.RemoveFirst();
            OperationOrderIndex.Remove(evicted.Value);
            RecentOperations.Remove(evicted.Value);
        }
    }
}
