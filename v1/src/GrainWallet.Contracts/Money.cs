using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GrainWallet.Contracts;

/// <summary>Decimal amount paired with an ISO 4217 currency code. Constructor validates the code (3 uppercase ASCII letters) and fails fast on anything else.</summary>
[GenerateSerializer]
[Immutable]
public readonly partial record struct Money
{
    [Id(0)]
    public decimal Amount { get; init; }

    [Id(1)]
    public string Currency { get; init; }

    public Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = ValidateCurrency(currency);
    }

    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    [JsonIgnore]
    public bool IsPositive => Amount > 0m;

    [JsonIgnore]
    public bool IsNonNegative => Amount >= 0m;

    public override string ToString() => $"{Amount:0.####} {Currency}";

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Iso4217Regex();

    private static string ValidateCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency code is required.", nameof(currency));
        }

        if (!Iso4217Regex().IsMatch(currency))
        {
            throw new ArgumentException(
                $"Currency code '{currency}' is not ISO 4217 (must be three uppercase ASCII letters).",
                nameof(currency));
        }

        return currency;
    }
}

public sealed class CurrencyMismatchException(string expected, string actual)
    : InvalidOperationException($"Cannot operate on amounts with different currencies: expected {expected} but got {actual}.")
{
    public string ExpectedCurrency { get; } = expected;
    public string ActualCurrency { get; } = actual;
}
