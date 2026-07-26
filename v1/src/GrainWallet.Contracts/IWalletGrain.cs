using Orleans.Concurrency;

namespace GrainWallet.Contracts;

/// <summary>Player wallet grain interface. One grain per player; mutations serialize via turn-based concurrency, reads are <see cref="ReadOnlyAttribute"/> so they interleave. All mutations are idempotent on <c>operationId</c>.</summary>
public interface IWalletGrain : IGrainWithStringKey
{
    Task<OperationResult> AddFundsAsync(Guid operationId, Money amount);

    Task<OperationResult> DeductFundsAsync(Guid operationId, Money amount);

    [ReadOnly]
    Task<Money> GetBalanceAsync();
}
