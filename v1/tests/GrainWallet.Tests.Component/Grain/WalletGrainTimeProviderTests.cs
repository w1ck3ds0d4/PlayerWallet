using GrainWallet.Contracts;

namespace GrainWallet.Tests.Component.Grain;

/// <summary>Proves the grain reads time from the injected <see cref="TimeProvider"/>, not <c>DateTimeOffset.UtcNow</c>. Order-independent: tests use <c>Advance()</c> rather than absolute <c>SetUtcNow</c>.</summary>
[Collection(nameof(WalletGrainCollection))]
public sealed class WalletGrainTimeProviderTests(WalletGrainTestCluster cluster)
{
    [Fact]
    public async Task OccurredAt_Comes_From_Injected_TimeProvider()
    {
        cluster.Publisher.Clear();
        var instant = cluster.TimeProvider.GetUtcNow();

        var playerId = $"time-{Guid.NewGuid():N}";
        var wallet = cluster.Wallet(playerId);
        var result = await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));

        Assert.True(result.Succeeded);
        Assert.Equal(instant, result.OccurredAt);

        var added = cluster.Publisher.Published
            .OfType<FundsAdded>()
            .Single(e => e.PlayerId == playerId);
        Assert.Equal(instant, added.OccurredAt);
    }

    [Fact]
    public async Task Successive_Operations_Use_Successive_TimeProvider_Values()
    {
        cluster.Publisher.Clear();
        var playerId = $"time-advance-{Guid.NewGuid():N}";
        var wallet = cluster.Wallet(playerId);

        var t0 = cluster.TimeProvider.GetUtcNow();
        var first = await wallet.AddFundsAsync(Guid.NewGuid(), new Money(50m, "EUR"));
        Assert.Equal(t0, first.OccurredAt);

        cluster.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        var t1 = cluster.TimeProvider.GetUtcNow();

        var second = await wallet.DeductFundsAsync(Guid.NewGuid(), new Money(10m, "EUR"));
        Assert.Equal(t1, second.OccurredAt);
        Assert.Equal(TimeSpan.FromMinutes(5), second.OccurredAt - first.OccurredAt);
    }
}
