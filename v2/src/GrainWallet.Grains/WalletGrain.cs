using System.Diagnostics;
using GrainWallet.Contracts;
using GrainWallet.Grains.Telemetry;

namespace GrainWallet.Grains;

/// <summary>
/// Player wallet grain. One activation per player; turn-based concurrency serializes mutations.
/// Per-mutation flow: validate, stage candidate state, then use one <see cref="IWalletStateStore.SaveAsync"/> call to atomically commit versioned state, a durable operation receipt, and the outbox event. Live state changes only after the commit is confirmed.
/// State loads on <see cref="OnActivateAsync"/> and lives in memory across turns. Reads use <see cref="Orleans.Concurrency.ReadOnlyAttribute"/> and interleave.
/// </summary>
public sealed class WalletGrain(
    IWalletStateStore stateStore,
    TimeProvider timeProvider,
    OutboxBackpressureGate backpressureGate) : Grain, IWalletGrain
{
    /// <summary>OTel ActivitySource for grain hot path. Each mutation emits a wallet.grain.mutate span with the operation tag and the rejection code when applicable, so the Aspire trace viewer can show whether a slow request was stuck in the grain queue, the store, or somewhere else.</summary>
    public static readonly ActivitySource ActivitySource = new("GrainWallet.Grains");

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

    public Task<Money> GetBalanceAsync() => Task.FromResult(_state.Balance);

    public Task<OperationResult> AddFundsAsync(Guid operationId, Money amount) =>
        ApplyMutationAsync(operationId, amount, isAdd: true);

    public Task<OperationResult> DeductFundsAsync(Guid operationId, Money amount) =>
        ApplyMutationAsync(operationId, amount, isAdd: false);

    private async Task<OperationResult> ApplyMutationAsync(Guid operationId, Money amount, bool isAdd)
    {
        using var activity = ActivitySource.StartActivity("wallet.grain.mutate", ActivityKind.Internal);
        activity?.SetTag("operation", isAdd ? "add-funds" : "deduct-funds");
        activity?.SetTag("player_id", this.GetPrimaryKeyString());

        var now = _timeProvider.GetUtcNow();
        var playerId = this.GetPrimaryKeyString();

        if (_backpressureGate.ShouldRejectNewWrites)
        {
            activity?.SetTag("path", "backpressure-rejected");
            return OperationResult.Reject(
                CurrentBalance(amount.Currency),
                RejectionCode.OutboxFull,
                $"Outbox at capacity ({_backpressureGate.PendingCount}/{_backpressureGate.Cap} unpublished); retry shortly.",
                now);
        }

        if (!amount.IsPositive)
        {
            // Pure input rejection is not persisted; it is cheap to recompute and creates no event.
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
                return await RecordStatefulRejectionAsync(operationId, amount, rejection, playerId);
            }

            // Stage initialization locally. The live grain changes only after the store commits.
        }

        if (_state.Initialized &&
            !string.Equals(amount.Currency, _state.Balance.Currency, StringComparison.Ordinal))
        {
            // Deterministic input/state mismatch; no event is emitted.
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
            return await RecordStatefulRejectionAsync(operationId, amount, rejection, playerId);
        }

        var currentBalance = _state.Initialized ? _state.Balance : Money.Zero(amount.Currency);
        var newBalance = isAdd
            ? currentBalance.Add(amount)
            : currentBalance.Subtract(amount);

        var result = OperationResult.Success(newBalance, now);
        IWalletEvent walletEvent = isAdd
            ? new FundsAdded(Guid.NewGuid(), playerId, operationId, amount, newBalance, now)
            : new FundsDeducted(Guid.NewGuid(), playerId, operationId, amount, newBalance, now);

        var candidate = CloneState(_state);
        candidate.Balance = newBalance;
        candidate.Initialized = true;
        var saved = await _stateStore.SaveAsync(
            playerId, _state.Version, candidate, result, walletEvent, CancellationToken.None);
        var finalResult = await ApplySaveResultAsync(saved, operationId, amount, isAdd);
        if (saved.Status != WalletStoreSaveStatus.Applied)
        {
            return finalResult;
        }

        candidate.Version = saved.Version;
        candidate.TrackOperation(operationId, result);
        _state = candidate;

        WalletMeters.BalanceAfterOp.Record(
            (double)newBalance.Amount,
            new KeyValuePair<string, object?>("currency", newBalance.Currency));

        activity?.SetTag("path", "success");
        return finalResult;
    }

    private async Task<OperationResult> RecordStatefulRejectionAsync(Guid operationId, Money requestedAmount, OperationResult rejection, string playerId)
    {
        var rejectedEvent = new OperationRejected(
            Guid.NewGuid(),
            playerId,
            operationId,
            requestedAmount,
            rejection.Balance,
            rejection.RejectionCode,
            rejection.OccurredAt);

        var candidate = CloneState(_state);
        var saved = await _stateStore.SaveAsync(
            playerId, _state.Version, candidate, rejection, rejectedEvent, CancellationToken.None);
        var finalResult = await ApplySaveResultAsync(saved, operationId, requestedAmount, isAdd: false);
        if (saved.Status == WalletStoreSaveStatus.Applied)
        {
            candidate.Version = saved.Version;
            candidate.TrackOperation(operationId, rejection);
            _state = candidate;
        }

        return finalResult;
    }

    private async Task<OperationResult> ApplySaveResultAsync(
        WalletStoreSaveResult saved,
        Guid operationId,
        Money amount,
        bool isAdd)
    {
        if (saved.Status == WalletStoreSaveStatus.Duplicate)
        {
            WalletMeters.IdempotencyHits.Add(
                1,
                new KeyValuePair<string, object?>("endpoint", isAdd ? "add-funds" : "deduct-funds"));
            var durableResult = saved.Result
                ?? throw new InvalidOperationException("Durable duplicate operation has no result.");
            _state.TrackOperation(operationId, durableResult);
            return durableResult;
        }

        if (saved.Status == WalletStoreSaveStatus.Conflict)
        {
            _state = await _stateStore.LoadAsync(this.GetPrimaryKeyString(), CancellationToken.None) ?? new WalletState();
            return await ApplyMutationAsync(operationId, amount, isAdd);
        }

        if (saved.Status == WalletStoreSaveStatus.OperationMismatch)
        {
            throw new InvalidOperationException(
                $"Operation id {operationId} was already used for a different wallet request.");
        }

        return saved.Result
            ?? throw new InvalidOperationException("Applied wallet operation has no result.");
    }

    private static WalletState CloneState(WalletState source)
    {
        var clone = new WalletState
        {
            Balance = source.Balance,
            Initialized = source.Initialized,
            Version = source.Version,
        };
        foreach (var id in source.OperationOrder)
        {
            if (source.RecentOperations.TryGetValue(id, out var result))
            {
                clone.TrackOperation(id, result);
            }
        }
        return clone;
    }

    private Money CurrentBalance(string fallbackCurrency) =>
        _state.Initialized ? _state.Balance : Money.Zero(fallbackCurrency);
}
