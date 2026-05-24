using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.TestingHost;
using PlayerWallet.Contracts;
using PlayerWallet.Grains;

namespace PlayerWallet.Tests.Component.Grain;

/// <summary>xUnit fixture that spins up an in-process Orleans test cluster with memory grain storage, an in-memory <see cref="IWalletStateStore"/>, and a singleton fake publisher the tests can inspect.</summary>
public sealed class WalletGrainTestCluster : IAsyncLifetime
{
    private static readonly FakeWalletEventPublisher s_publisher = new();
    private static readonly FakeTimeProvider s_timeProvider = new(DateTimeOffset.UtcNow);

    public TestCluster Cluster { get; private set; } = null!;

    public FakeWalletEventPublisher Publisher => s_publisher;
    public FakeTimeProvider TimeProvider => s_timeProvider;

    public async Task InitializeAsync()
    {
        s_publisher.Clear();

        var builder = new TestClusterBuilder(initialSilosCount: 1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await Cluster.StopAllSilosAsync();
    }

    public IWalletGrain Wallet(string playerId) =>
        Cluster.GrainFactory.GetGrain<IWalletGrain>(playerId);

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(s_timeProvider);
                services.AddSingleton<IWalletEventPublisher>(s_publisher);
                services.AddSingleton<IWalletStateStore, InMemoryWalletStateStore>();
                services.AddSingleton(new OutboxBackpressureGate());
            });
        }
    }
}

[CollectionDefinition(nameof(WalletGrainCollection))]
public sealed class WalletGrainCollection : ICollectionFixture<WalletGrainTestCluster>;
