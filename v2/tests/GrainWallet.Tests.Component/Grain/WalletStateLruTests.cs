using GrainWallet.Contracts;
using GrainWallet.Grains;

namespace GrainWallet.Tests.Component.Grain;

/// <summary>v2 regression: idempotency cache must behave as real LRU, not FIFO. Touching an entry on cache hit should protect it from eviction when the cap is exceeded.</summary>
public sealed class WalletStateLruTests
{
    [Fact]
    public void TouchOperation_Reorders_Recent_Entry_To_Tail()
    {
        var state = new WalletState();
        var oldest = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var newest = Guid.NewGuid();

        state.TrackOperation(oldest, OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));
        state.TrackOperation(newer, OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));
        state.TrackOperation(newest, OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));

        state.TouchOperation(oldest);

        Assert.Equal(new[] { newer, newest, oldest }, state.OperationOrder.ToArray());
    }

    [Fact]
    public void Touched_Entry_Survives_Cap_Eviction_While_FIFO_Would_Evict_It()
    {
        var state = new WalletState();
        var protectedId = Guid.NewGuid();
        state.TrackOperation(protectedId, OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));

        for (var i = 0; i < WalletState.IdempotencyCacheCap - 1; i++)
        {
            state.TrackOperation(Guid.NewGuid(), OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));
        }

        state.TouchOperation(protectedId);

        // Now insert one more so the cap is breached. The least-recently-used (NOT protectedId) should evict.
        var newcomer = Guid.NewGuid();
        state.TrackOperation(newcomer, OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));

        Assert.True(state.RecentOperations.ContainsKey(protectedId), "Touched entry must survive eviction.");
        Assert.True(state.RecentOperations.ContainsKey(newcomer));
        Assert.Equal(WalletState.IdempotencyCacheCap, state.OperationOrder.Count);
    }

    [Fact]
    public void TrackOperation_Same_Id_Twice_Refreshes_Order_Without_Duplicating()
    {
        var state = new WalletState();
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        state.TrackOperation(id, OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));
        state.TrackOperation(other, OperationResult.Success(Money.Zero("EUR"), DateTimeOffset.UtcNow));
        state.TrackOperation(id, OperationResult.Success(new Money(5m, "EUR"), DateTimeOffset.UtcNow));

        Assert.Equal(2, state.OperationOrder.Count);
        Assert.Equal(new[] { other, id }, state.OperationOrder.ToArray());
        Assert.Equal(new Money(5m, "EUR"), state.RecentOperations[id].Balance);
    }
}
