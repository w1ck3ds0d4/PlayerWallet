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

            if (request.RequestsPerSecond is { } rps && (rps < 10 || rps > 2000))
            {
                return Results.Problem(
                    title: "Invalid rps",
                    detail: "requestsPerSecond must be between 10 and 2000.",
                    statusCode: 400);
            }

            if (runner.IsRunning)
            {
                return Results.Problem(title: "Bench in progress", detail: "Another benchmark is currently running. Wait for it to finish.", statusCode: 409);
            }

            var run = await runner.StartAsync(request.Scenario, request.Projects, request.DurationSeconds, request.RequestsPerSecond, ct);
            return Results.Accepted($"/api/bench/{run.Id}", new { id = run.Id });
        });

        group.MapGet("/bench", (BenchRunner runner) => Results.Ok(runner.RecentRuns));

        group.MapGet("/bench/{id}", (string id, BenchRunner runner) =>
        {
            var run = runner.GetRun(id);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        // Proxies the dev-only /admin/db-stats endpoint on each project so the dashboard can
        // surface pg_stat_user_tables without anyone shelling into psql. Same v1-vs-v2 asymmetry
        // as reset-outbox: only v2 ships the endpoint, v1 reports unavailable.
        group.MapGet("/db-stats", async (IHttpClientFactory clientFactory, IOptions<DashboardOptions> options, CancellationToken ct) =>
        {
            var results = new List<object>();
            foreach (var project in options.Value.Projects)
            {
                var client = clientFactory.CreateClient(project.Name);
                client.BaseAddress = new Uri(project.Url);
                client.Timeout = TimeSpan.FromSeconds(5);
                try
                {
                    using var resp = await client.GetAsync("/admin/db-stats", ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        results.Add(new { project = project.Name, ok = true, data = System.Text.Json.JsonDocument.Parse(body).RootElement });
                    }
                    else
                    {
                        results.Add(new { project = project.Name, ok = false, statusCode = (int)resp.StatusCode, note = "endpoint not available (only v2 exposes /admin/db-stats in Development)" });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { project = project.Name, ok = false, statusCode = 0, note = ex.Message });
                }
            }
            return Results.Ok(results);
        });

        // Proxies the dev-only /admin/reset-outbox endpoint on each configured project so a single
        // dashboard click can wipe wallet_outbox on both v1 and v2 before a clean-slate bench.
        // Best-effort: v1 doesn't ship the endpoint so it returns a not-supported note in the
        // response. The dashboard surfaces this per-project so the operator knows what cleared.
        group.MapPost("/reset-outboxes", async (IHttpClientFactory clientFactory, IOptions<DashboardOptions> options, CancellationToken ct) =>
        {
            var results = new List<object>();
            foreach (var project in options.Value.Projects)
            {
                var client = clientFactory.CreateClient(project.Name);
                client.BaseAddress = new Uri(project.Url);
                client.Timeout = TimeSpan.FromSeconds(30);
                try
                {
                    using var resp = await client.PostAsync("/admin/reset-outbox", content: null, ct);
                    results.Add(new
                    {
                        project = project.Name,
                        ok = resp.IsSuccessStatusCode,
                        statusCode = (int)resp.StatusCode,
                        note = resp.IsSuccessStatusCode ? "outbox truncated + vacuumed" : "endpoint not available (only v2 exposes /admin/reset-outbox in Development)",
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new
                    {
                        project = project.Name,
                        ok = false,
                        statusCode = 0,
                        note = ex.Message,
                    });
                }
            }
            return Results.Ok(results);
        });

        return app;
    }
}

public sealed record BenchRequest(string Scenario, string[] Projects, int? DurationSeconds = null, int? RequestsPerSecond = null);
