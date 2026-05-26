using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using PlayerWallet.Contracts;

namespace PlayerWallet.Api.Telemetry;

/// <summary>Attaches concrete example bodies (valid Guid, numeric amount) to the wallet request/response schemas so Scalar pre-fills its editor instead of showing placeholder strings.</summary>
internal sealed class WalletOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    private static readonly JsonNode WalletOperationRequestExample = JsonNode.Parse(/*lang=json,strict*/ """
        {
          "operationId": "11111111-1111-1111-1111-111111111111",
          "amount": { "amount": 100.50, "currency": "EUR" }
        }
        """)!;

    private static readonly JsonNode MoneyExample = JsonNode.Parse(/*lang=json,strict*/ """
        { "amount": 100.50, "currency": "EUR" }
        """)!;

    private static readonly JsonNode WalletBalanceResponseExample = JsonNode.Parse(/*lang=json,strict*/ """
        {
          "playerId": "player_42",
          "balance": { "amount": 100.50, "currency": "EUR" }
        }
        """)!;

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        schema.Example = type switch
        {
            _ when type == typeof(WalletOperationRequest) => WalletOperationRequestExample.DeepClone(),
            _ when type == typeof(Money) => MoneyExample.DeepClone(),
            _ when type == typeof(WalletBalanceResponse) => WalletBalanceResponseExample.DeepClone(),
            _ => schema.Example,
        };
        return Task.CompletedTask;
    }
}
