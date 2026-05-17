using NBomber.CSharp;
using PlayerWallet.Tests.Load;
using PlayerWallet.Tests.Load.Scenarios;

var baseUrl = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase))
    ?? Environment.GetEnvironmentVariable("WALLET_API_URL")
    ?? LoadConfig.DefaultBaseUrl;

var scenarioFilter = args.FirstOrDefault(a => !a.StartsWith("http", StringComparison.OrdinalIgnoreCase));

Console.WriteLine($"[load] Target API: {baseUrl}");
Console.WriteLine($"[load] Scenarios:  {scenarioFilter ?? "all"}");

using var httpClient = HttpClientFactory.Create(baseUrl);

await WaitForApiReadyAsync(httpClient, TimeSpan.FromMinutes(2));
await WalletPool.SeedAsync(httpClient, CancellationToken.None);
await WalletPool.PreWarmAsync(httpClient, CancellationToken.None);

var reportsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "reports");
Directory.CreateDirectory(reportsRoot);

var scenarios = new (string Name, Func<NBomber.Contracts.ScenarioProps> Build)[]
{
    ("add-funds",    () => WalletScenarios.AddFunds(httpClient)),
    ("deduct-funds", () => WalletScenarios.DeductFunds(httpClient)),
    ("get-balance",  () => WalletScenarios.GetBalance(httpClient)),
    ("hot-wallet",   () => WalletScenarios.HotWalletDeducts(httpClient)),
};

// Cooldown between scenarios so each starts against a quiet API; skipped when only one scenario is filtered.
var cooldown = TimeSpan.FromSeconds(60);
var scenariosToRun = scenarios
    .Where(s => scenarioFilter is null || scenarioFilter.Equals(s.Name, StringComparison.OrdinalIgnoreCase))
    .ToArray();

for (var i = 0; i < scenariosToRun.Length; i++)
{
    var (name, build) = scenariosToRun[i];

    Console.WriteLine();
    Console.WriteLine($"=== Running scenario: {name} ===");

    var scenarioReports = Path.Combine(reportsRoot, name);
    Directory.CreateDirectory(scenarioReports);

    var stats = NBomberRunner
        .RegisterScenarios(build())
        .WithReportFolder(scenarioReports)
        .WithReportFileName($"{name}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}")
        .Run();

    PrintSummary(name, stats);

    if (i < scenariosToRun.Length - 1)
    {
        Console.WriteLine($"[load] Cooling down for {cooldown.TotalSeconds:F0}s before next scenario...");
        await Task.Delay(cooldown);
    }
}

return 0;

static async Task WaitForApiReadyAsync(HttpClient client, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    Console.WriteLine("[load] Waiting for /health/ready...");
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var response = await client.GetAsync("/health/ready");
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("[load] API is ready.");
                return;
            }
        }
        catch (HttpRequestException)
        {
        }
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
    throw new InvalidOperationException(
        $"Wallet API was not ready within {timeout.TotalSeconds:F0}s. Start the AppHost first.");
}

static void PrintSummary(string name, NBomber.Contracts.Stats.NodeStats stats)
{
    var scenarioStats = stats.ScenarioStats.FirstOrDefault();
    if (scenarioStats is null)
    {
        Console.WriteLine($"[load] {name}: no scenario stats produced.");
        return;
    }

    var ok = scenarioStats.Ok.Request;
    var lat = scenarioStats.Ok.Latency;
    Console.WriteLine($"[{name}] OK: {ok.Count:N0} ({ok.RPS:F1} rps avg)");
    Console.WriteLine($"[{name}] mean: {lat.MeanMs:F1} ms | p50: {lat.Percent50:F1} ms | p95: {lat.Percent95:F1} ms | p99: {lat.Percent99:F1} ms | stddev: {lat.StdDev:F2}");
    if (scenarioStats.Fail.Request.Count > 0)
    {
        Console.WriteLine($"[{name}] FAIL: {scenarioStats.Fail.Request.Count:N0} ({(double)scenarioStats.Fail.Request.Count / Math.Max(1, ok.Count + scenarioStats.Fail.Request.Count) * 100:F2}%)");
    }
}
