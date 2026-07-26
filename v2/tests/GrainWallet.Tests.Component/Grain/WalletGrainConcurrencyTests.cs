using GrainWallet.Contracts;

namespace GrainWallet.Tests.Component.Grain;

/// <summary>Financial-consistency proofs under concurrent mutations. Turn-based grain concurrency serializes per-player ops; these tests fire many concurrent ops and assert exact final balance + no double-spend + no duplicate events.</summary>
[Collection(nameof(WalletGrainCollection))]
public sealed class WalletGrainConcurrencyTests(WalletGrainTestCluster cluster)
{
    [Fact]
    public async Task One_Hundred_Parallel_Deductions_Each_Settle_Cleanly()
    {
        var playerId = $"race-clean-{Guid.NewGuid():N}";
        cluster.Publisher.Clear();
        var wallet = cluster.Wallet(playerId);

        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(1000m, "EUR"));

        var tasks = Enumerable.Range(0, 100)
            .Select(_ => wallet.DeductFundsAsync(Guid.NewGuid(), new Money(1m, "EUR")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(new Money(900m, "EUR"), await wallet.GetBalanceAsync());

        var deductions = cluster.Publisher.Published
            .OfType<FundsDeducted>()
            .Where(e => e.PlayerId == playerId)
            .ToList();
        Assert.Equal(100, deductions.Count);

        var balances = deductions.Select(e => e.BalanceAfter.Amount).ToArray();
        for (var i = 1; i < balances.Length; i++)
        {
            Assert.True(
                balances[i] < balances[i - 1],
                $"BalanceAfter must strictly decrease: {balances[i - 1]} -> {balances[i]} at index {i}.");
        }
    }

    [Fact]
    public async Task Parallel_Deductions_Beyond_Balance_Reject_Without_OverDraw()
    {
        var playerId = $"race-overspend-{Guid.NewGuid():N}";
        cluster.Publisher.Clear();
        var wallet = cluster.Wallet(playerId);

        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(50m, "EUR"));

        var tasks = Enumerable.Range(0, 100)
            .Select(_ => wallet.DeductFundsAsync(Guid.NewGuid(), new Money(1m, "EUR")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(50, results.Count(r => r.Succeeded));
        Assert.Equal(50, results.Count(r => !r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r =>
            Assert.Equal(RejectionCode.InsufficientFunds, r.RejectionCode));
        Assert.Equal(new Money(0m, "EUR"), await wallet.GetBalanceAsync());
    }

    [Fact]
    public async Task Parallel_Duplicate_OperationIds_Apply_Exactly_Once()
    {
        var playerId = $"race-idempotent-{Guid.NewGuid():N}";
        cluster.Publisher.Clear();
        var wallet = cluster.Wallet(playerId);

        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));

        var sharedOperationId = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 25)
            .Select(_ => wallet.DeductFundsAsync(sharedOperationId, new Money(10m, "EUR")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.All(results, r => Assert.Equal(results[0], r));
        Assert.Equal(new Money(90m, "EUR"), await wallet.GetBalanceAsync());

        var ours = cluster.Publisher.Published
            .OfType<FundsDeducted>()
            .Where(e => e.PlayerId == playerId)
            .ToList();
        Assert.Single(ours);
    }
}
