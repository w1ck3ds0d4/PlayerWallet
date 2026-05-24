using PlayerWallet.Dashboard.Bench;
using PlayerWallet.Dashboard.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.AddSingleton<BenchRunner>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDashboardEndpoints();

app.Logger.LogInformation("PlayerWallet dashboard listening; open http://localhost:5100/ in a browser.");

await app.RunAsync();
