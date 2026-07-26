using GrainWallet.Contracts;
using GrainWallet.Grains.Telemetry;

namespace GrainWallet.Grains;

/// <summary>
/// Player wallet grain. One activation per player; turn-based concurrency serializes mutations.
/// Per-mutation flow: check idempotency cache, validate amount + currency, mutate balance + cache, single <see cref="IWalletStateStore.SaveAsync"/> that commits state AND enqueues the event in one Postgres transaction. Return immediately; Kafka publishing happens off the request path via <c>WalletOutboxDrainer</c> reading <c>wallet_outbox</c>.
/// State loads on <see cref="OnActivateAsync"/> and lives in memory across turns. Reads use <see cref="Orleans.Concurrency.ReadOnlyAttribute"/> and interleave.
/// </summary>
public sealed class WalletGrain(
    IWalletStateStore stateStore,
    TimeProvider timeProvider) : Grain, IWalletGrain
{
    private readonly IWalletStateStore _stateStore = stateStore;
    private readonly TimeProvider _timeProvider = timeProvider;

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

        if (!_state.Initialized)
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

            _state.Balance = Money.Zero(amount.Currency);
            _state.Initialized = true;
        }

        if (!string.Equals(amount.Currency, _state.Balance.Currency, StringComparison.Ordinal))
        {
            var rejection = OperationResult.Reject(
                _state.Balance,
                RejectionCode.CurrencyMismatch,
                $"Wallet operates in {_state.Balance.Currency}; request was {amount.Currency}.",
                now);
            await RecordRejectionAsync(operationId, amount, rejection, playerId);
            return rejection;
        }

        if (!isAdd && _state.Balance.Amount < amount.Amount)
        {
            var rejection = OperationResult.Reject(
                _state.Balance,
                RejectionCode.InsufficientFunds,
                $"Insufficient funds. Requested {amount} from balance {_state.Balance}.",
                now);
            await RecordRejectionAsync(operationId, amount, rejection, playerId);
            return rejection;
        }

        var newBalance = isAdd
            ? _state.Balance.Add(amount)
            : _state.Balance.Subtract(amount);

        _state.Balance = newBalance;

        var result = OperationResult.Success(newBalance, now);
        TrackOperation(operationId, result);

        IWalletEvent walletEvent = isAdd
            ? new FundsAdded(Guid.NewGuid(), playerId, operationId, amount, newBalance, now)
            : new FundsDeducted(Guid.NewGuid(), playerId, operationId, amount, newBalance, now);

        await _stateStore.SaveAsync(playerId, _state, walletEvent, CancellationToken.None);

        WalletMeters.BalanceAfterOp.Record(
            (double)newBalance.Amount,
            new KeyValuePair<string, object?>("currency", newBalance.Currency));

        return result;
    }

    private async Task RecordRejectionAsync(Guid operationId, Money requestedAmount, OperationResult rejection, string playerId)
    {
        TrackOperation(operationId, rejection);

        var rejectedEvent = new DeductionRejected(
            Guid.NewGuid(),
            playerId,
            operationId,
            requestedAmount,
            rejection.Balance,
            rejection.RejectionCode,
            rejection.OccurredAt);

        await _stateStore.SaveAsync(playerId, _state, rejectedEvent, CancellationToken.None);
    }

    private void TrackOperation(Guid operationId, OperationResult result)
    {
        _state.RecentOperations[operationId] = result;
        _state.OperationOrder.Enqueue(operationId);

        while (_state.OperationOrder.Count > WalletState.IdempotencyCacheCap)
        {
            var evicted = _state.OperationOrder.Dequeue();
            _state.RecentOperations.Remove(evicted);
        }
    }

    private Money CurrentBalance(string fallbackCurrency) =>
        _state.Initialized ? _state.Balance : Money.Zero(fallbackCurrency);
}
