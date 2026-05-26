using PlayerWallet.Contracts;

namespace PlayerWallet.Tests.Component.Grain;

[Collection(nameof(WalletGrainCollection))]
public sealed class WalletGrainTests(WalletGrainTestCluster cluster)
{
    [Fact]
    public async Task AddFunds_Creates_Wallet_With_Currency_Of_First_Add()
    {
        var wallet = cluster.Wallet($"new-{Guid.NewGuid():N}");
        var result = await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));
        Assert.True(result.Succeeded);
        Assert.Equal(new Money(100m, "EUR"), result.Balance);
    }

    [Fact]
    public async Task AddFunds_Accumulates_When_Currency_Matches()
    {
        var wallet = cluster.Wallet($"accumulate-{Guid.NewGuid():N}");
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(50m, "EUR"));
        Assert.Equal(new Money(150m, "EUR"), await wallet.GetBalanceAsync());
    }

    [Fact]
    public async Task AddFunds_Rejects_When_Currency_Differs()
    {
        var wallet = cluster.Wallet($"currency-{Guid.NewGuid():N}");
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));
        var result = await wallet.AddFundsAsync(Guid.NewGuid(), new Money(50m, "USD"));
        Assert.False(result.Succeeded);
        Assert.Equal(RejectionCode.CurrencyMismatch, result.RejectionCode);
        Assert.Equal(new Money(100m, "EUR"), result.Balance);
    }

    [Fact]
    public async Task AddFunds_Rejects_Non_Positive_Amount()
    {
        var wallet = cluster.Wallet($"nonpositive-{Guid.NewGuid():N}");
        var result = await wallet.AddFundsAsync(Guid.NewGuid(), new Money(0m, "EUR"));
        Assert.False(result.Succeeded);
        Assert.Equal(RejectionCode.InvalidAmount, result.RejectionCode);
    }

    [Fact]
    public async Task DeductFunds_Succeeds_When_Balance_Sufficient()
    {
        var wallet = cluster.Wallet($"deduct-{Guid.NewGuid():N}");
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));
        var result = await wallet.DeductFundsAsync(Guid.NewGuid(), new Money(30m, "EUR"));
        Assert.True(result.Succeeded);
        Assert.Equal(new Money(70m, "EUR"), result.Balance);
    }

    [Fact]
    public async Task DeductFunds_Rejects_When_Insufficient()
    {
        var wallet = cluster.Wallet($"insufficient-{Guid.NewGuid():N}");
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(50m, "EUR"));
        var result = await wallet.DeductFundsAsync(Guid.NewGuid(), new Money(80m, "EUR"));
        Assert.False(result.Succeeded);
        Assert.Equal(RejectionCode.InsufficientFunds, result.RejectionCode);
        Assert.Equal(new Money(50m, "EUR"), result.Balance);
    }

    [Fact]
    public async Task DeductFunds_From_Empty_Wallet_Rejects_As_Insufficient()
    {
        var wallet = cluster.Wallet($"empty-{Guid.NewGuid():N}");
        var result = await wallet.DeductFundsAsync(Guid.NewGuid(), new Money(10m, "EUR"));
        Assert.False(result.Succeeded);
        Assert.Equal(RejectionCode.InsufficientFunds, result.RejectionCode);
    }

    [Fact]
    public async Task Same_OperationId_Returns_Identical_Result_Without_Mutation()
    {
        var wallet = cluster.Wallet($"idempotent-{Guid.NewGuid():N}");
        var opId = Guid.NewGuid();
        var first = await wallet.AddFundsAsync(opId, new Money(100m, "EUR"));
        var second = await wallet.AddFundsAsync(opId, new Money(100m, "EUR"));
        var third = await wallet.AddFundsAsync(opId, new Money(100m, "EUR"));
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(new Money(100m, "EUR"), await wallet.GetBalanceAsync());
    }

    [Fact]
    public async Task Same_OperationId_Returns_Cached_Rejection()
    {
        var wallet = cluster.Wallet($"cached-reject-{Guid.NewGuid():N}");
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(20m, "EUR"));
        var opId = Guid.NewGuid();
        var first = await wallet.DeductFundsAsync(opId, new Money(100m, "EUR"));
        var second = await wallet.DeductFundsAsync(opId, new Money(100m, "EUR"));
        Assert.False(first.Succeeded);
        Assert.Equal(first, second);
        Assert.Equal(new Money(20m, "EUR"), await wallet.GetBalanceAsync());
    }

    [Fact]
    public async Task Successful_Mutation_Publishes_Matching_Event()
    {
        var playerId = $"events-{Guid.NewGuid():N}";
        cluster.Publisher.Clear();
        var wallet = cluster.Wallet(playerId);
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));
        await wallet.DeductFundsAsync(Guid.NewGuid(), new Money(30m, "EUR"));
        var ours = cluster.Publisher.Published.Where(e => e.PlayerId == playerId).ToList();
        Assert.Equal(2, ours.Count);
        Assert.IsType<FundsAdded>(ours[0]);
        Assert.IsType<FundsDeducted>(ours[1]);
    }

    [Fact]
    public async Task Insufficient_Funds_Publishes_OperationRejected_Event()
    {
        var playerId = $"rejected-{Guid.NewGuid():N}";
        cluster.Publisher.Clear();
        var wallet = cluster.Wallet(playerId);
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(20m, "EUR"));
        await wallet.DeductFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));
        var rejected = cluster.Publisher.Published
            .OfType<OperationRejected>()
            .Where(e => e.PlayerId == playerId)
            .ToList();
        Assert.Single(rejected);
        Assert.Equal(RejectionCode.InsufficientFunds, rejected[0].Reason);
        Assert.Equal(new Money(100m, "EUR"), rejected[0].RequestedAmount);
        Assert.Equal(new Money(20m, "EUR"), rejected[0].CurrentBalance);
    }

    [Fact]
    public async Task Invalid_Amount_Does_Not_Publish_Event()
    {
        var playerId = $"invalid-{Guid.NewGuid():N}";
        cluster.Publisher.Clear();
        var wallet = cluster.Wallet(playerId);
        var result = await wallet.AddFundsAsync(Guid.NewGuid(), new Money(0m, "EUR"));
        Assert.False(result.Succeeded);
        Assert.Equal(RejectionCode.InvalidAmount, result.RejectionCode);
        Assert.DoesNotContain(cluster.Publisher.Published, e => e.PlayerId == playerId);
    }

    [Fact]
    public async Task Currency_Mismatch_Does_Not_Publish_Event()
    {
        var playerId = $"mismatch-{Guid.NewGuid():N}";
        cluster.Publisher.Clear();
        var wallet = cluster.Wallet(playerId);
        await wallet.AddFundsAsync(Guid.NewGuid(), new Money(100m, "EUR"));
        cluster.Publisher.Clear();
        var result = await wallet.AddFundsAsync(Guid.NewGuid(), new Money(50m, "USD"));
        Assert.False(result.Succeeded);
        Assert.Equal(RejectionCode.CurrencyMismatch, result.RejectionCode);
        Assert.DoesNotContain(cluster.Publisher.Published, e => e.PlayerId == playerId);
    }
}
