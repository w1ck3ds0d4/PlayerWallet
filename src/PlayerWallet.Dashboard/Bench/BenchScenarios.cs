using System.Text;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;

namespace PlayerWallet.Dashboard.Bench;

/// <summary>NBomber scenario builders for the dashboard. Targets one configured project (URL) per scenario instance so the dashboard can register v1 + v2 simultaneously and have them race.</summary>
internal static class BenchScenarios
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task SeedAndWarmAsync(HttpClient client, string[] playerIds, decimal seedBalance, string currency, CancellationToken cancellationToken)
    {
        var semaphore = new SemaphoreSlim(32);
        var seedTasks = playerIds.Select(async id =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var payload = new { operationId = Guid.NewGuid(), amount = new { amount = seedBalance, currency } };
                using var response = await client.PostAsJsonAsync($"/wallets/{id}/add-funds", payload, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(seedTasks);

        var warmTasks = playerIds.Select(async id =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var add = new { operationId = Guid.NewGuid(), amount = new { amount = 0.01m, currency } };
                using var ar = await client.PostAsJsonAsync($"/wallets/{id}/add-funds", add, cancellationToken);
                ar.EnsureSuccessStatusCode();
                var sub = new { operationId = Guid.NewGuid(), amount = new { amount = 0.01m, currency } };
                using var sr = await client.PostAsJsonAsync($"/wallets/{id}/deduct-funds", sub, cancellationToken);
                sr.EnsureSuccessStatusCode();
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(warmTasks);
    }

    public static ScenarioProps Build(string scenario, string projectName, HttpClient client, string[] playerIds, BenchOptions opts)
    {
        var name = $"{projectName}-{scenario}";

        ScenarioProps props = scenario switch
        {
            "get-balance" => Scenario.Create(name, async ctx =>
            {
                var id = playerIds[ctx.Random.Next(playerIds.Length)];
                using var resp = await client.GetAsync($"/wallets/{id}/balance", HttpCompletionOption.ResponseHeadersRead);
                await resp.Content.CopyToAsync(Stream.Null);
                return resp.IsSuccessStatusCode
                    ? Response.Ok(statusCode: ((int)resp.StatusCode).ToString())
                    : Response.Fail(statusCode: ((int)resp.StatusCode).ToString());
            }),
            "add-funds" => BuildPost(name, client, playerIds, "/add-funds", opts),
            "deduct-funds" => BuildPost(name, client, playerIds, "/deduct-funds", opts),
            _ => throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario)),
        };

        return props
            .WithWarmUpDuration(TimeSpan.FromSeconds(opts.WarmUpSeconds))
            .WithLoadSimulations(Simulation.Inject(
                rate: opts.RequestsPerSecond,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(opts.DurationSeconds)));
    }

    private static ScenarioProps BuildPost(string name, HttpClient client, string[] playerIds, string path, BenchOptions opts)
    {
        return Scenario.Create(name, async ctx =>
        {
            var id = playerIds[ctx.Random.Next(playerIds.Length)];
            var body = JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid(),
                amount = new { amount = 1m, currency = opts.Currency },
            }, JsonOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync($"/wallets/{id}{path}", content);
            await resp.Content.CopyToAsync(Stream.Null);
            return resp.IsSuccessStatusCode
                ? Response.Ok(statusCode: ((int)resp.StatusCode).ToString())
                : Response.Fail(statusCode: ((int)resp.StatusCode).ToString());
        });
    }

    public static string[] BuildPlayerIds(string projectName, int count) =>
        Enumerable.Range(0, count).Select(i => $"dash-{projectName}-{i:D4}").ToArray();
}
