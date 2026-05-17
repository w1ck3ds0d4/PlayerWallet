using System.Net.Http.Json;

namespace PlayerWallet.Tests.Load;

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
