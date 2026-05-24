using System.Collections.Concurrent;
using PlayerWallet.Contracts;
using PlayerWallet.Grains;

namespace PlayerWallet.Tests.Component.Grain;

/// <summary>Records every event the grain publishes. <see cref="ShouldThrow"/> and <see cref="ShouldReturnFalse"/> simulate Kafka failure modes for retry tests.</summary>
public sealed class FakeWalletEventPublisher : IWalletEventPublisher
{
    private readonly ConcurrentQueue<IWalletEvent> _published = new();

    public IReadOnlyCollection<IWalletEvent> Published => _published;

    public bool ShouldThrow { get; set; }
    public bool ShouldReturnFalse { get; set; }

    public Task<bool> PublishAsync(IWalletEvent walletEvent, CancellationToken cancellationToken = default)
    {
        if (ShouldThrow)
        {
            throw new InvalidOperationException("Simulated Kafka outage.");
        }

        if (ShouldReturnFalse)
        {
            return Task.FromResult(false);
        }

        _published.Enqueue(walletEvent);
        return Task.FromResult(true);
    }

    public void Clear()
    {
        while (_published.TryDequeue(out _))
        {
        }
    }
}
