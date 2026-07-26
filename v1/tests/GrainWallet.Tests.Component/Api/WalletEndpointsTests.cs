using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GrainWallet.Contracts;

namespace GrainWallet.Tests.Component.Api;

[Collection(nameof(WalletEndpointsCollection))]
public sealed class WalletEndpointsTests(ApiTestApplicationFactory factory) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync()
    {
        factory.Publisher.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddFunds_Returns_200_And_New_Balance()
    {
        using var client = factory.CreateClient();
        var playerId = $"add-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(100m, "EUR")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WalletBalanceResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(playerId, body!.PlayerId);
        Assert.Equal(new Money(100m, "EUR"), body.Balance);
    }

    [Fact]
    public async Task AddFunds_With_Same_OperationId_Returns_Same_Balance_Twice()
    {
        using var client = factory.CreateClient();
        var playerId = $"add-idempotent-{Guid.NewGuid():N}";
        var opId = Guid.NewGuid();

        var first = await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(opId, new Money(50m, "EUR")));
        var second = await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(opId, new Money(50m, "EUR")));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var balance = await client.GetFromJsonAsync<WalletBalanceResponse>(
            $"/wallets/{playerId}/balance", JsonOptions);
        Assert.Equal(new Money(50m, "EUR"), balance!.Balance);
    }

    [Fact]
    public async Task DeductFunds_When_Sufficient_Returns_200_And_Reduced_Balance()
    {
        using var client = factory.CreateClient();
        var playerId = $"deduct-ok-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(100m, "EUR")));

        var response = await client.PostAsJsonAsync(
            $"/wallets/{playerId}/deduct-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(30m, "EUR")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WalletBalanceResponse>(JsonOptions);
        Assert.Equal(new Money(70m, "EUR"), body!.Balance);
    }

    [Fact]
    public async Task DeductFunds_When_Insufficient_Returns_402_With_ProblemDetails()
    {
        using var client = factory.CreateClient();
        var playerId = $"deduct-insufficient-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(20m, "EUR")));

        var response = await client.PostAsJsonAsync(
            $"/wallets/{playerId}/deduct-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(100m, "EUR")));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient funds", doc.GetProperty("title").GetString());
        Assert.Equal("InsufficientFunds", doc.GetProperty("rejectionCode").GetString());
    }

    [Fact]
    public async Task AddFunds_With_Mismatched_Currency_Returns_400_With_ProblemDetails()
    {
        using var client = factory.CreateClient();
        var playerId = $"currency-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(100m, "EUR")));

        var response = await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(10m, "USD")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CurrencyMismatch", doc.GetProperty("rejectionCode").GetString());
    }

    [Fact]
    public async Task AddFunds_With_Empty_OperationId_Returns_400()
    {
        using var client = factory.CreateClient();
        var playerId = $"empty-op-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(Guid.Empty, new Money(10m, "EUR")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetBalance_For_Unknown_Player_Returns_Zero()
    {
        using var client = factory.CreateClient();
        var playerId = $"unknown-{Guid.NewGuid():N}";

        var response = await client.GetAsync($"/wallets/{playerId}/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WalletBalanceResponse>(JsonOptions);
        Assert.Equal(0m, body!.Balance.Amount);
    }

    [Fact]
    public async Task Successful_Mutation_Hands_Event_To_Publisher()
    {
        using var client = factory.CreateClient();
        var playerId = $"events-http-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync(
            $"/wallets/{playerId}/add-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(100m, "EUR")));
        await client.PostAsJsonAsync(
            $"/wallets/{playerId}/deduct-funds",
            new WalletOperationRequest(Guid.NewGuid(), new Money(30m, "EUR")));

        var events = factory.Publisher.Published.Where(e => e.PlayerId == playerId).ToList();
        Assert.Equal(2, events.Count);
        Assert.IsType<FundsAdded>(events[0]);
        Assert.IsType<FundsDeducted>(events[1]);
    }

    [Fact]
    public async Task HealthLive_Returns_200()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_Returns_200_When_Cluster_And_Publisher_Ready()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

[CollectionDefinition(nameof(WalletEndpointsCollection))]
public sealed class WalletEndpointsCollection : ICollectionFixture<ApiTestApplicationFactory>;
