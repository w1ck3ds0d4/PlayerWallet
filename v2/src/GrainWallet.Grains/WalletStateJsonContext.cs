using System.Text.Json.Serialization;
using GrainWallet.Contracts;

namespace GrainWallet.Grains;

/// <summary>Source-gen JSON for the JSONB columns (idempotency cache + outbox payloads). Used by <c>PostgresWalletStateStore</c> on write and <c>WalletOutboxDrainer</c> on read.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<Guid, OperationResult>))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(IWalletEvent))]
[JsonSerializable(typeof(FundsAdded))]
[JsonSerializable(typeof(FundsDeducted))]
[JsonSerializable(typeof(OperationRejected))]
public sealed partial class WalletStateJsonContext : JsonSerializerContext;
