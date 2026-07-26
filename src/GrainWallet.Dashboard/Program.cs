using System.Text.Json.Serialization;
using GrainWallet.Dashboard.Bench;
using GrainWallet.Dashboard.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.AddSingleton<BenchRunner>();
builder.Services.AddSingleton<SuiteRunner>();

// Serialize BenchStatus enum as its string name ("Completed") not its underlying int (4). The dashboard JS compares run.status against string names; numeric serialization left the Run button disabled after each run.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDashboardEndpoints();

app.Logger.LogInformation("GrainWallet dashboard listening; open http://localhost:5100/ in a browser.");

await app.RunAsync();
