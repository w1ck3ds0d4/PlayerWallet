using System.Text;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;

namespace GrainWallet.Tests.Load.Scenarios;

/// <summary>One NBomber scenario per endpoint at 1000 rps for 5 min after a 30 s warmup; the hot-wallet appendix targets a single grain id for 60 s to capture the per-grain ceiling. Every scenario disposes the response and drains the body to <see cref="Stream.Null"/> or Windows ephemeral source ports exhaust within 20 s.</summary>
internal static class WalletScenarios
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ScenarioProps AddFunds(HttpClient client) =>
        BuildPostScenario(
            "add-funds",
            client,
            playerIdSelector: random => WalletPool.PickRandom(random),
            payloadFactory: () => new
            {
                operationId = Guid.NewGuid(),
                amount = new { amount = 1m, currency = LoadConfig.Currency },
            },
            path: "/add-funds",
            duration: LoadConfig.MeasurementDuration);

    public static ScenarioProps DeductFunds(HttpClient client) =>
        BuildPostScenario(
            "deduct-funds",
            client,
            playerIdSelector: random => WalletPool.PickRandom(random),
            payloadFactory: () => new
            {
                operationId = Guid.NewGuid(),
                amount = new { amount = 1m, currency = LoadConfig.Currency },
            },
            path: "/deduct-funds",
            duration: LoadConfig.MeasurementDuration);

    public static ScenarioProps GetBalance(HttpClient client) =>
        Scenario.Create("get-balance", async context =>
        {
            var playerId = WalletPool.PickRandom(context.Random);
            using var response = await client.GetAsync($"/wallets/{playerId}/balance", HttpCompletionOption.ResponseHeadersRead);
            await response.Content.CopyToAsync(System.IO.Stream.Null);
            return response.IsSuccessStatusCode
                ? Response.Ok(statusCode: ((int)response.StatusCode).ToString())
                : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
        })
        .WithWarmUpDuration(LoadConfig.WarmUpDuration)
        .WithLoadSimulations(
            Simulation.Inject(
                rate: LoadConfig.TargetRequestsPerSecond,
                interval: TimeSpan.FromSeconds(1),
                during: LoadConfig.MeasurementDuration));

    public static ScenarioProps HotWalletDeducts(HttpClient client) =>
        Scenario.Create("hot-wallet-deduct", async _ =>
        {
            var payload = JsonSerializer.Serialize(
                new
                {
                    operationId = Guid.NewGuid(),
                    amount = new { amount = 1m, currency = LoadConfig.Currency },
                },
                JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/wallets/{WalletPool.HotWalletId}/deduct-funds", content);
            await response.Content.CopyToAsync(System.IO.Stream.Null);
            return response.IsSuccessStatusCode
                ? Response.Ok(statusCode: ((int)response.StatusCode).ToString())
                : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
        })
        .WithWarmUpDuration(LoadConfig.WarmUpDuration)
        .WithLoadSimulations(
            Simulation.Inject(
                rate: LoadConfig.TargetRequestsPerSecond,
                interval: TimeSpan.FromSeconds(1),
                during: LoadConfig.HotWalletDuration));

    private static ScenarioProps BuildPostScenario(
        string name,
        HttpClient client,
        Func<Random, string> playerIdSelector,
        Func<object> payloadFactory,
        string path,
        TimeSpan duration)
    {
        return Scenario.Create(name, async context =>
        {
            var playerId = playerIdSelector(context.Random);
            var payload = JsonSerializer.Serialize(payloadFactory(), JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/wallets/{playerId}{path}", content);
            await response.Content.CopyToAsync(System.IO.Stream.Null);
            return response.IsSuccessStatusCode
                ? Response.Ok(statusCode: ((int)response.StatusCode).ToString())
                : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
        })
        .WithWarmUpDuration(LoadConfig.WarmUpDuration)
        .WithLoadSimulations(
            Simulation.Inject(
                rate: LoadConfig.TargetRequestsPerSecond,
                interval: TimeSpan.FromSeconds(1),
                during: duration));
    }
}
