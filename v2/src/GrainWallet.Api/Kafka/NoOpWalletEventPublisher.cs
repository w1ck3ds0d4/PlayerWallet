using GrainWallet.Contracts;
using GrainWallet.Grains;

namespace GrainWallet.Api.Kafka;

/// <summary>Stand-in publisher used when Kafka is absent (component tests, pre-AppHost local runs). Logs at debug and returns success.</summary>
internal sealed class NoOpWalletEventPublisher(ILogger<NoOpWalletEventPublisher> logger) : IWalletEventPublisher
{
    public Task<bool> PublishAsync(IWalletEvent walletEvent, CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "[NoOp] would publish {EventType} for player {PlayerId} (op {OperationId})",
            walletEvent.GetType().Name,
            walletEvent.PlayerId,
            walletEvent.OperationId);
        return Task.FromResult(true);
    }
}
