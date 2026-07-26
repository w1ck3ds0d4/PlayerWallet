using System.Net.Http.Json;

namespace GrainWallet.Tests.Load;

/// <summary>Pre-seeded pool of player ids the bench cycles through, plus a reserved hot-key id for the per-grain ceiling scenario. Seed balance is large so deduct measures mutation latency, not 402 rejection rates.</summary>
internal static class WalletPool
{
    public const string HotWalletId = "player_hot";

    public static string[] Ids { get; } = Enumerable
        .Range(0, LoadConfig.WalletPoolSize)
        .Select(i => $"player_{i:D4}")
        .ToArray();

    public static string PickRandom(Random random) => Ids[random.Next(Ids.Length)];

    public static async Task SeedAsync(HttpClient client, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[seed] Pre-seeding {Ids.Length + 1} wallets with {LoadConfig.SeedBalance:N0} {LoadConfig.Currency}...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var allIds = Ids.Append(HotWalletId).ToArray();

        var semaphore = new SemaphoreSlim(64);
        var tasks = allIds.Select(async playerId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await SeedOneAsync(client, playerId, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        Console.WriteLine($"[seed] Done in {sw.Elapsed.TotalSeconds:F1}s.");
    }

    /// <summary>v2: pre-warm with a no-op mutation pair (add 0.01 + deduct 0.01) instead of GET /balance so the first-mutation Postgres SELECT + JSONB write cost amortises into warm-up rather than showing up as the add-funds latency tail in the first ~10s of the bench. GET /balance only warmed the read path.</summary>
    public static async Task PreWarmAsync(HttpClient client, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[prewarm] Warming {Ids.Length + 1} grains with no-op add+deduct mutation pairs...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var allIds = Ids.Append(HotWalletId).ToArray();

        var semaphore = new SemaphoreSlim(64);
        var tasks = allIds.Select(async playerId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await PreWarmOneAsync(client, playerId, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        Console.WriteLine($"[prewarm] Done in {sw.Elapsed.TotalSeconds:F1}s.");
    }

    private static async Task PreWarmOneAsync(HttpClient client, string playerId, CancellationToken cancellationToken)
    {
        var warmupAmount = new { amount = 0.01m, currency = LoadConfig.Currency };

        var addPayload = new { operationId = Guid.NewGuid(), amount = warmupAmount };
        using (var addResponse = await client.PostAsJsonAsync($"/wallets/{playerId}/add-funds", addPayload, cancellationToken))
        {
            await addResponse.Content.CopyToAsync(System.IO.Stream.Null, cancellationToken);
            addResponse.EnsureSuccessStatusCode();
        }

        var deductPayload = new { operationId = Guid.NewGuid(), amount = warmupAmount };
        using var deductResponse = await client.PostAsJsonAsync($"/wallets/{playerId}/deduct-funds", deductPayload, cancellationToken);
        await deductResponse.Content.CopyToAsync(System.IO.Stream.Null, cancellationToken);
        deductResponse.EnsureSuccessStatusCode();
    }

    private static async Task SeedOneAsync(HttpClient client, string playerId, CancellationToken cancellationToken)
    {
        var payload = new
        {
            operationId = Guid.NewGuid(),
            amount = new { amount = LoadConfig.SeedBalance, currency = LoadConfig.Currency },
        };
        using var response = await client.PostAsJsonAsync($"/wallets/{playerId}/add-funds", payload, cancellationToken);
        await response.Content.CopyToAsync(System.IO.Stream.Null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
