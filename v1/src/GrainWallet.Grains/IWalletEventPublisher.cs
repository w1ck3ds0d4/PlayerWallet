using GrainWallet.Contracts;

namespace GrainWallet.Grains;

/// <summary>Sink the grain drains its outbox into. Real impl publishes to Kafka with W3C trace context; tests use a fake.</summary>
public interface IWalletEventPublisher
{
    /// <summary>Publishes a wallet event. Returns true on broker ack, false to retry next drain. Throws only on unrecoverable errors.</summary>
    Task<bool> PublishAsync(IWalletEvent walletEvent, CancellationToken cancellationToken = default);
}
