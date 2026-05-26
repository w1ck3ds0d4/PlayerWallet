using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PlayerWallet.Contracts;

namespace PlayerWallet.Api.Telemetry;

/// <summary>Source-generated JSON for the hot HTTP path. Compile-time metadata avoids reflection at sustained 1000 rps. Registered via <c>ConfigureHttpJsonOptions</c>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(WalletOperationRequest))]
[JsonSerializable(typeof(WalletBalanceResponse))]
[JsonSerializable(typeof(Money))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(IWalletEvent))]
[JsonSerializable(typeof(FundsAdded))]
[JsonSerializable(typeof(FundsDeducted))]
[JsonSerializable(typeof(DeductionRejected))]
internal sealed partial class WalletJsonContext : JsonSerializerContext;
