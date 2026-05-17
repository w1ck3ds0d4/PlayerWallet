using Microsoft.Extensions.Logging;
using PlayerWallet.Contracts;
using PlayerWallet.Grains.Telemetry;

namespace PlayerWallet.Grains;

/// <summary>
/// Player wallet grain. One activation per player; turn-based concurrency serializes mutations.
/// Per-mutation flow: check idempotency cache, validate amount + currency, mutate balance + cache,
/// single <c>WriteStateAsync</c> (atomic save of state + outbox), then drain outbox in memory only.
/// Successfully published entries are NOT persisted again; the next mutation's save handles them,
/// and an unflushed entry replays on reactivation (at-least-once contract; consumers idempotent on <c>eventId</c>).
/// Reads use <see cref="Orleans.Concurrency.ReadOnlyAttribute"/> and interleave.
/// </summary>
public sealed class WalletGrain(
    [PersistentState("wallet", "WalletStorage")] IPersistentState<WalletState> state,
    IWalletEventPublisher publisher,
    TimeProvider timeProvider,
    ILogger<WalletGrain> logger) : Grain, IWalletGrain
{
    private readonly IPersistentState<WalletState> _state = state;
    private readonly IWalletEventPublisher _publisher = publisher;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<WalletGrain> _logger = logger;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        if (_state.State.Outbox.Count > 0)
        {
            _logger.LogInformation(
                "Wallet {PlayerId} activated with {Count} pending outbox entries; draining",
                this.GetPrimaryKeyString(),
                _state.State.Outbox.Count);

            await DrainOutboxAsync(cancellationToken);
        }
    }

    public Task<Money> GetBalanceAsync() => Task.FromResult(_state.State.Balance);

    public Task<OperationResult> AddFundsAsync(Guid operationId, Money amount) =>
        ApplyMutationAsync(operationId, amount, isAdd: true);

    public Task<OperationResult> DeductFundsAsync(Guid operationId, Money amount) =>
        ApplyMutationAsync(operationId, amount, isAdd: false);

    private async Task<OperationResult> ApplyMutationAsync(Guid operationId, Money amount, bool isAdd)
    {
        var endpointTag = new KeyValuePair<string, object?>("endpoint", isAdd ? "add-funds" : "deduct-funds");

        if (_state.State.RecentOperations.TryGetValue(operationId, out var cached))
        {
            WalletMeters.IdempotencyHits.Add(1, endpointTag);
            return cached;
        }

        var now = _timeProvider.GetUtcNow();
        var playerId = this.GetPrimaryKeyString();

        if (!amount.IsPositive)
        {
            var rejection = OperationResult.Reject(
                CurrentBalance(amount.Currency),
                RejectionCode.InvalidAmount,
                "Amount must be greater than zero.",
                now);
            await RecordRejectionAsync(operationId, amount, rejection, playerId);
            return rejection;
        }

        if (!_state.State.Initialized)
        {
            if (!isAdd)
            {
                var rejection = OperationResult.Reject(
                    Money.Zero(amount.Currency),
                    RejectionCode.InsufficientFunds,
                    "Wallet is empty; cannot deduct.",
                    now);
                await RecordRejectionAsync(operationId, amount, rejection, playerId);
                return rejection;
            }

            _state.State.Balance = Money.Zero(amount.Currency);
            _state.State.Initialized = true;
        }

        if (!string.Equals(amount.Currency, _state.State.Balance.Currency, StringComparison.Ordinal))
        {
            var rejection = OperationResult.Reject(
                _state.State.Balance,
                RejectionCode.CurrencyMismatch,
                $"Wallet operates in {_state.State.Balance.Currency}; request was {amount.Currency}.",
                now);
            await RecordRejectionAsync(operationId, amount, rejection, playerId);
            return rejection;
        }

        if (!isAdd && _state.State.Balance.Amount < amount.Amount)
        {
            var rejection = OperationResult.Reject(
                _state.State.Balance,
                RejectionCode.InsufficientFunds,
                $"Insufficient funds. Requested {amount} from balance {_state.State.Balance}.",
                now);
            await RecordRejectionAsync(operationId, amount, rejection, playerId);
            return rejection;
        }

        if (_state.State.Outbox.Count >= WalletState.OutboxCap)
        {
            return OperationResult.Reject(
                _state.State.Balance,
                RejectionCode.OutboxFull,
                "Event outbox is at capacity. Retry shortly.",
                now);
        }

        var newBalance = isAdd
            ? _state.State.Balance.Add(amount)
            : _state.State.Balance.Subtract(amount);

        _state.State.Balance = newBalance;

        var result = OperationResult.Success(newBalance, now);
        TrackOperation(operationId, result);

        IWalletEvent walletEvent = isAdd
            ? new FundsAdded(Guid.NewGuid(), playerId, operationId, amount, newBalance, now)
            : new FundsDeducted(Guid.NewGuid(), playerId, operationId, amount, newBalance, now);

        _state.State.Outbox.Add(new PendingEvent(walletEvent.EventId, walletEvent.GetType().Name, walletEvent));

        await _state.WriteStateAsync();

        WalletMeters.BalanceAfterOp.Record(
            (double)newBalance.Amount,
            new KeyValuePair<string, object?>("currency", newBalance.Currency));
        WalletMeters.RecordOutboxDepth(_state.State.Outbox.Count);

        await DrainOutboxAsync(CancellationToken.None);

        return result;
    }

    private async Task RecordRejectionAsync(Guid operationId, Money requestedAmount, OperationResult rejection, string playerId)
    {
        TrackOperation(operationId, rejection);

        if (_state.State.Outbox.Count < WalletState.OutboxCap)
        {
            var rejectedEvent = new DeductionRejected(
                Guid.NewGuid(),
                playerId,
                operationId,
                requestedAmount,
                rejection.Balance,
                rejection.RejectionCode,
                rejection.OccurredAt);

            _state.State.Outbox.Add(new PendingEvent(rejectedEvent.EventId, nameof(DeductionRejected), rejectedEvent));
        }

        await _state.WriteStateAsync();
        await DrainOutboxAsync(CancellationToken.None);
    }

    private void TrackOperation(Guid operationId, OperationResult result)
    {
        _state.State.RecentOperations[operationId] = result;
        _state.State.OperationOrder.Enqueue(operationId);

        while (_state.State.OperationOrder.Count > WalletState.IdempotencyCacheCap)
        {
            var evicted = _state.State.OperationOrder.Dequeue();
            _state.State.RecentOperations.Remove(evicted);
        }
    }

    private async Task DrainOutboxAsync(CancellationToken cancellationToken)
    {
        if (_state.State.Outbox.Count == 0)
        {
            return;
        }

        var drained = new List<PendingEvent>();
        foreach (var entry in _state.State.Outbox.ToArray())
        {
            try
            {
                var acknowledged = await _publisher.PublishAsync(entry.Payload, cancellationToken);
                if (acknowledged)
                {
                    drained.Add(entry);
                }
                else
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to publish wallet event {EventId} for player {PlayerId}; will retry on next drain",
                    entry.EventId,
                    this.GetPrimaryKeyString());
                break;
            }
        }

        if (drained.Count > 0)
        {
            foreach (var entry in drained)
            {
                _state.State.Outbox.Remove(entry);
            }

            // Intentionally no WriteStateAsync here: the second save was the dominant Postgres cost under sustained throughput. The next mutation persists the drained outbox; if the grain deactivates first, the unflushed entry replays on reactivation (at-least-once contract).
        }
    }

    private Money CurrentBalance(string fallbackCurrency) =>
        _state.State.Initialized ? _state.State.Balance : Money.Zero(fallbackCurrency);
}
