using Microsoft.Extensions.Options;
using PlayerWallet.Dashboard.Bench;

namespace PlayerWallet.Dashboard.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Dashboard");

        group.MapGet("/projects", (IOptions<DashboardOptions> options) =>
            Results.Ok(options.Value.Projects.Select(p => new
            {
                p.Name,
                p.Url,
                p.Color,
            })));

        group.MapGet("/config", (IOptions<DashboardOptions> options, BenchRunner runner) =>
            Results.Ok(new
            {
                options.Value.Bench.WarmUpSeconds,
                options.Value.Bench.DurationSeconds,
                options.Value.Bench.RequestsPerSecond,
                options.Value.Bench.WalletPoolSize,
                options.Value.Bench.SeedBalance,
                options.Value.Bench.Currency,
                options.Value.Bench.HttpTimeoutSeconds,
                options.Value.Bench.ScenarioRpsOverrides,
                ReportsRoot = runner.ReportsRoot,
            }));

        group.MapGet("/health/{project}", async (string project, IHttpClientFactory clientFactory, IOptions<DashboardOptions> options, CancellationToken ct) =>
        {
            var cfg = options.Value.Projects.FirstOrDefault(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));
            if (cfg is null)
            {
                return Results.NotFound(new { error = $"Project '{project}' not configured." });
            }

            var client = clientFactory.CreateClient(project);
            client.BaseAddress = new Uri(cfg.Url);
            client.Timeout = TimeSpan.FromSeconds(3);
            try
            {
                using var resp = await client.GetAsync("/health/ready", ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                return Results.Ok(new
                {
                    project,
                    cfg.Url,
                    statusCode = (int)resp.StatusCode,
                    healthy = resp.IsSuccessStatusCode,
                    detail = body,
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    project,
                    cfg.Url,
                    statusCode = 0,
                    healthy = false,
                    detail = ex.Message,
                });
            }
        });

        group.MapPost("/bench", async (BenchRequest request, BenchRunner runner, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Scenario) || !BenchRunner.SupportedScenarios.Contains(request.Scenario))
            {
                return Results.Problem(
                    title: "Invalid scenario",
                    detail: $"Scenario must be one of: {string.Join(", ", BenchRunner.SupportedScenarios)}.",
                    statusCode: 400);
            }

            if (request.Projects is null || request.Projects.Length == 0)
            {
                return Results.Problem(title: "Invalid projects", detail: "At least one project must be specified.", statusCode: 400);
            }

            if (request.DurationSeconds is { } dur && (dur < 10 || dur > 600))
            {
                return Results.Problem(
                    title: "Invalid duration",
                    detail: "durationSeconds must be between 10 and 600.",
                    statusCode: 400);
            }

            if (runner.IsRunning)
            {
                return Results.Problem(title: "Bench in progress", detail: "Another benchmark is currently running. Wait for it to finish.", statusCode: 409);
            }

            var run = await runner.StartAsync(request.Scenario, request.Projects, request.DurationSeconds, ct);
            return Results.Accepted($"/api/bench/{run.Id}", new { id = run.Id });
        });

        group.MapGet("/bench", (BenchRunner runner) => Results.Ok(runner.RecentRuns));

        group.MapGet("/bench/{id}", (string id, BenchRunner runner) =>
        {
            var run = runner.GetRun(id);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        return app;
    }
}

public sealed record BenchRequest(string Scenario, string[] Projects, int? DurationSeconds = null);
