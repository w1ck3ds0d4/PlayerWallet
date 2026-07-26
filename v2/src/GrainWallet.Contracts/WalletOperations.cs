namespace GrainWallet.Contracts;

/// <summary>HTTP request body for POST /wallets/{playerId}/add-funds and /deduct-funds.</summary>
public sealed record WalletOperationRequest(Guid OperationId, Money Amount);

/// <summary>HTTP response body for a successful mutation or balance query.</summary>
public sealed record WalletBalanceResponse(string PlayerId, Money Balance);
