using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GrainWallet.Grains;
using GrainWallet.Tests.Component.Grain;

namespace GrainWallet.Tests.Component.Api;

/// <summary>In-memory API host with the same Orleans silo + memory grain storage, but the NoOp publisher swapped for the inspectable <see cref="FakeWalletEventPublisher"/>.</summary>
public sealed class ApiTestApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public FakeWalletEventPublisher Publisher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWalletEventPublisher>();
            services.AddSingleton<IWalletEventPublisher>(Publisher);
        });
    }

    public Task InitializeAsync()
    {
        Publisher.Clear();
        _ = Server;
        return Task.CompletedTask;
    }

    public new Task DisposeAsync()
    {
        base.Dispose();
        return Task.CompletedTask;
    }
}
