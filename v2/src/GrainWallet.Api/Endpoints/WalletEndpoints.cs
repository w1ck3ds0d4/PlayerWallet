using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using GrainWallet.Contracts;
using GrainWallet.Grains.Telemetry;

namespace GrainWallet.Api.Endpoints;

/// <summary>HTTP surface for the player wallet. POST endpoints are idempotent on caller-supplied <c>operationId</c>. Validation failures return RFC 7807 ProblemDetails.</summary>
public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/wallets/{playerId:minlength(1):maxlength(64)}").WithTags("Wallet");

        const string addFundsDescription = """
            Idempotent on `operationId`. Sets the wallet currency on first call.
            Returns 400 if the amount is non-positive or the currency mismatches
            an existing wallet, 503 if the event outbox is at capacity.

            **Example request**

            ```json
            {
              "operationId": "8c5b8c8e-1d2a-4d2a-8d2a-1d2a4d2a8d2a",
              "amount": { "amount": 100.50, "currency": "EUR" }
            }
            ```

            **Example response (200)**

            ```json
            {
              "playerId": "player_42",
              "balance": { "amount": 100.50, "currency": "EUR" }
            }
            ```
            """;

        const string deductFundsDescription = """
            Idempotent on `operationId`. Returns 402 Payment Required if the
            wallet has insufficient funds, 400 on validation problems, 503 if
            the event outbox is at capacity. State-dependent rejections
            (insufficient funds) emit an `OperationRejected` event for audit.

            **Example response (402 Insufficient Funds)**

            ```json
            {
              "title": "Insufficient funds",
              "status": 402,
              "detail": "Insufficient funds. Requested 9999 EUR from balance 70 EUR.",
              "rejectionCode": "InsufficientFunds",
              "balance": { "amount": 70.00, "currency": "EUR" }
            }
            ```
            """;

        const string getBalanceDescription = """
            Read-only. Concurrent reads against the same player wallet
            interleave on the grain. Returns a zero balance in the wallet's
            currency if the player has never had any funds added.
            """;

        group.MapPost("/add-funds", AddFundsAsync)
            .WithName("AddFunds")
            .WithSummary("Credit funds to a player wallet")
            .WithDescription(addFundsDescription)
            .Produces<WalletBalanceResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/deduct-funds", DeductFundsAsync)
            .WithName("DeductFunds")
            .WithSummary("Debit funds from a player wallet")
            .WithDescription(deductFundsDescription)
            .Produces<WalletBalanceResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/balance", GetBalanceAsync)
            .WithName("GetBalance")
            .WithSummary("Get the current balance for a player wallet")
            .WithDescription(getBalanceDescription)
            .Produces<WalletBalanceResponse>(StatusCodes.Status200OK);

        return app;
    }

    private static Task<Results<Ok<WalletBalanceResponse>, ProblemHttpResult>> AddFundsAsync(
        [FromRoute] string playerId,
        [FromBody] WalletOperationRequest request,
        [FromServices] IGrainFactory grains,
        CancellationToken cancellationToken)
        => HandleMutationAsync(playerId, request, grains, isAdd: true, cancellationToken);

    private static Task<Results<Ok<WalletBalanceResponse>, ProblemHttpResult>> DeductFundsAsync(
        [FromRoute] string playerId,
        [FromBody] WalletOperationRequest request,
        [FromServices] IGrainFactory grains,
        CancellationToken cancellationToken)
        => HandleMutationAsync(playerId, request, grains, isAdd: false, cancellationToken);

    private static async Task<Ok<WalletBalanceResponse>> GetBalanceAsync(
        [FromRoute] string playerId,
        [FromServices] IGrainFactory grains,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpointTag = new KeyValuePair<string, object?>("endpoint", "balance");
        try
        {
            var balance = await grains.GetGrain<IWalletGrain>(playerId).GetBalanceAsync();
            WalletMeters.Requests.Add(1, endpointTag, new KeyValuePair<string, object?>("result", "accepted"));
            return TypedResults.Ok(new WalletBalanceResponse(playerId, balance));
        }
        finally
        {
            WalletMeters.RequestDuration.Record(stopwatch.Elapsed.TotalMilliseconds, endpointTag);
        }
    }

    private static async Task<Results<Ok<WalletBalanceResponse>, ProblemHttpResult>> HandleMutationAsync(
        string playerId,
        WalletOperationRequest request,
        IGrainFactory grains,
        bool isAdd,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpoint = isAdd ? "add-funds" : "deduct-funds";
        var endpointTag = new KeyValuePair<string, object?>("endpoint", endpoint);

        try
        {
            if (request.OperationId == Guid.Empty)
            {
                WalletMeters.Requests.Add(1, endpointTag, new KeyValuePair<string, object?>("result", "rejected"));
                return TypedResults.Problem(
                    title: "Invalid request",
                    detail: "operationId is required and must be a non-empty Guid.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // v2: pre-grain amount validation. Bad input never opens a Postgres tx, never writes an outbox row, never activates the grain.
            if (!request.Amount.IsPositive)
            {
                WalletMeters.Requests.Add(1, endpointTag, new KeyValuePair<string, object?>("result", "rejected"));
                return TypedResults.Problem(
                    title: "Invalid amount",
                    detail: "amount must be greater than zero.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?>
                    {
                        ["rejectionCode"] = RejectionCode.InvalidAmount.ToString(),
                    });
            }

            OperationResult result;
            try
            {
                var grain = grains.GetGrain<IWalletGrain>(playerId);
                result = isAdd
                    ? await grain.AddFundsAsync(request.OperationId, request.Amount)
                    : await grain.DeductFundsAsync(request.OperationId, request.Amount);
            }
            catch (ArgumentException ex)
            {
                WalletMeters.Requests.Add(1, endpointTag, new KeyValuePair<string, object?>("result", "rejected"));
                return TypedResults.Problem(
                    title: "Invalid request",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (result.Succeeded)
            {
                WalletMeters.Requests.Add(1, endpointTag, new KeyValuePair<string, object?>("result", "accepted"));
                return TypedResults.Ok(new WalletBalanceResponse(playerId, result.Balance));
            }

            var (status, title) = MapRejection(result.RejectionCode);
            WalletMeters.Requests.Add(1, endpointTag, new KeyValuePair<string, object?>("result", "rejected"));
            return TypedResults.Problem(
                title: title,
                detail: result.RejectionReason,
                statusCode: status,
                extensions: new Dictionary<string, object?>
                {
                    ["rejectionCode"] = result.RejectionCode.ToString(),
                    ["balance"] = result.Balance,
                });
        }
        finally
        {
            WalletMeters.RequestDuration.Record(stopwatch.Elapsed.TotalMilliseconds, endpointTag);
        }
    }

    private static (int Status, string Title) MapRejection(RejectionCode code) => code switch
    {
        RejectionCode.InsufficientFunds => (StatusCodes.Status402PaymentRequired, "Insufficient funds"),
        RejectionCode.CurrencyMismatch => (StatusCodes.Status400BadRequest, "Currency mismatch"),
        RejectionCode.InvalidAmount => (StatusCodes.Status400BadRequest, "Invalid amount"),
        RejectionCode.OutboxFull => (StatusCodes.Status503ServiceUnavailable, "Event outbox at capacity; retry shortly"),
        _ => (StatusCodes.Status400BadRequest, "Operation rejected"),
    };
}
