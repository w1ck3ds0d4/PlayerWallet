using PlayerWallet.Contracts;
using PlayerWallet.Grains.Telemetry;

namespace PlayerWallet.Grains;

/// <summary>
/// Player wallet grain. One activation per player; turn-based concurrency serializes mutations.
/// Per-mutation flow: check idempotency cache, validate amount + currency, mutate balance + cache, single <see cref="IWalletStateStore.SaveAsync"/> that commits state AND enqueues the event in one Postgres transaction. Return immediately; Kafka publishing happens off the request path via <c>WalletOutboxDrainer</c> reading <c>wallet_outbox</c>.
/// State loads on <see cref="OnActivateAsync"/> and lives in memory across turns. Reads use <see cref="Orleans.Concurrency.ReadOnlyAttribute"/> and interleave.
/// </summary>
public sealed class WalletGrain(
    IWalletStateStore stateStore,
    TimeProvider timeProvider,
    OutboxBackpressureGate backpressureGate) : Grain, IWalletGrain
{
    private readonly IWalletStateStore _stateStore = stateStore;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly OutboxBackpressureGate _backpressureGate = backpressureGate;

    private WalletState _state = new();

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        var loaded = await _stateStore.LoadAsync(this.GetPrimaryKeyString(), cancellationToken);
        if (loaded is not null)
        {
            _state = loaded;
        }
    }

    /// <summary>
    /// v2.1: flush the idempotency cache to Postgres on clean grain deactivation. The hot-path
    /// <see cref="IWalletStateStore.SaveAsync"/> no longer writes cache columns; this is the
    /// only place they are persisted. If the process crashes before deactivation, the un-flushed
    /// cache is lost and retries that cross a re-activation will execute as fresh ops.
    /// </summary>
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        try
        {
            if (_state.Initialized)
            {
                await _stateStore.PersistCacheAsync(this.GetPrimaryKeyString(), _state, cancellationToken);
            }
        }
        catch
        {
            // Swallow: deactivation cannot fail the grain. Lost cache means at most a small number
            // of retries within the next activation re-execute; underlying state is consistent.
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task<Money> GetBalanceAsync() => Task.FromResult(_state.Balance);

    public Task<OperationResult> AddFundsAsync(Guid operationId, Money amount) =>
        ApplyMutationAsync(operationId, amount, isAdd: true);

    public Task<OperationResult> DeductFundsAsync(Guid operationId, Money amount) =>
        ApplyMutationAsync(operationId, amount, isAdd: false);

    private async Task<OperationResult> ApplyMutationAsync(Guid operationId, Money amount, bool isAdd)
    {
        var endpointTag = new KeyValuePair<string, object?>("endpoint", isAdd ? "add-funds" : "deduct-funds");

        if (_state.RecentOperations.TryGetValue(operationId, out var cached))
        {
            _state.TouchOperation(operationId);
            WalletMeters.IdempotencyHits.Add(1, endpointTag);
            return cached;
        }

        var now = _timeProvider.GetUtcNow();
        var playerId = this.GetPrimaryKeyString();

        if (_backpressureGate.ShouldRejectNewWrites)
        {
            return OperationResult.Reject(
                CurrentBalance(amount.Currency),
                RejectionCode.OutboxFull,
                $"Outbox at capacity ({_backpressureGate.PendingCount}/{_backpressureGate.Cap} unpublished); retry shortly.",
                now);
        }

        if (!amount.IsPositive)
        {
            // v2: pure input rejection. Cached in memory for idempotency but NOT persisted; cheap to recompute on retry and avoids one Postgres tx per garbage request.
            var rejection = OperationResult.Reject(
                CurrentBalance(amount.Currency),
                RejectionCode.InvalidAmount,
                "Amount must be greater than zero.",
                now);
            _state.TrackOperation(operationId, rejection);
            return rejection;
        }

        if (!_state.Initialized)
        {
            if (!isAdd)
            {
                var rejection = OperationResult.Reject(
                    Money.Zero(amount.Currency),
                    RejectionCode.InsufficientFunds,
                    "Wallet is empty; cannot deduct.",
                    now);
                await RecordStatefulRejectionAsync(operationId, amount, rejection, playerId);
                return rejection;
            }

            _state.Balance = Money.Zero(amount.Currency);
            _state.Initialized = true;
        }

        if (!string.Equals(amount.Currency, _state.Balance.Currency, StringComparison.Ordinal))
        {
            // v2: deterministic from current wallet currency + request; cache only, do not persist.
            var rejection = OperationResult.Reject(
                _state.Balance,
                RejectionCode.CurrencyMismatch,
                $"Wallet operates in {_state.Balance.Currency}; request was {amount.Currency}.",
                now);
            _state.TrackOperation(operationId, rejection);
            return rejection;
        }

        if (!isAdd && _state.Balance.Amount < amount.Amount)
        {
            var rejection = OperationResult.Reject(
                _state.Balance,
                RejectionCode.InsufficientFunds,
                $"Insufficient funds. Requested {amount} from balance {_state.Balance}.",
                now);
            await RecordStatefulRejectionAsync(operationId, amount, rejection, playerId);
            return rejection;
        }

        var newBalance = isAdd
            ? _state.Balance.Add(amount)
            : _state.Balance.Subtract(amount);

        _state.Balance = newBalance;

        var result = OperationResult.Success(newBalance, now);
        _state.TrackOperation(operationId, result);

        IWalletEvent walletEvent = isAdd
            ? new FundsAdded(Guid.NewGuid(), playerId, operationId, amount, newBalance, now)
            : new FundsDeducted(Guid.NewGuid(), playerId, operationId, amount, newBalance, now);

        await _stateStore.SaveAsync(playerId, _state, walletEvent, CancellationToken.None);

        WalletMeters.BalanceAfterOp.Record(
            (double)newBalance.Amount,
            new KeyValuePair<string, object?>("currency", newBalance.Currency));

        return result;
    }

    private async Task RecordStatefulRejectionAsync(Guid operationId, Money requestedAmount, OperationResult rejection, string playerId)
    {
        _state.TrackOperation(operationId, rejection);

        var rejectedEvent = new OperationRejected(
            Guid.NewGuid(),
            playerId,
            operationId,
            requestedAmount,
            rejection.Balance,
            rejection.RejectionCode,
            rejection.OccurredAt);

        await _stateStore.SaveAsync(playerId, _state, rejectedEvent, CancellationToken.None);
    }

    private Money CurrentBalance(string fallbackCurrency) =>
        _state.Initialized ? _state.Balance : Money.Zero(fallbackCurrency);
}
