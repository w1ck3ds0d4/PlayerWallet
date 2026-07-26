using GrainWallet.Contracts;

namespace GrainWallet.Tests.Component.Contracts;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("EUR")]
    [InlineData("USD")]
    [InlineData("JPY")]
    [InlineData("XAU")]
    public void Constructor_Accepts_Valid_Iso4217_Currency(string currency)
    {
        var money = new Money(100m, currency);
        Assert.Equal(100m, money.Amount);
        Assert.Equal(currency, money.Currency);
    }

    [Theory]
    [InlineData("eur", "lowercase")]
    [InlineData("Eur", "mixed case")]
    [InlineData("EU", "two letters")]
    [InlineData("EURO", "four letters")]
    [InlineData("EU1", "contains digit")]
    [InlineData("EU ", "trailing space")]
    [InlineData(" EUR", "leading space")]
    public void Constructor_Rejects_NonIso4217_Currency(string currency, string reason)
    {
        var ex = Assert.Throws<ArgumentException>(() => new Money(10m, currency));
        Assert.Equal("currency", ex.ParamName);
        Assert.Contains(currency, ex.Message);
        _ = reason;
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Constructor_Rejects_BlankCurrency(string currency)
    {
        var ex = Assert.Throws<ArgumentException>(() => new Money(10m, currency));
        Assert.Equal("currency", ex.ParamName);
    }

    [Fact]
    public void Constructor_Rejects_NullCurrency()
    {
        Assert.Throws<ArgumentException>(() => new Money(10m, null!));
    }

    [Fact]
    public void Constructor_Rejects_Precision_Postgres_Would_Round()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(0.00005m, "EUR"));
    }

    [Fact]
    public void Constructor_Rejects_Value_Postgres_Cannot_Store()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(10_000_000_000_000_000m, "EUR"));
    }

    [Fact]
    public void Zero_Returns_Money_With_Zero_Amount_And_Given_Currency()
    {
        var zero = Money.Zero("EUR");
        Assert.Equal(0m, zero.Amount);
        Assert.Equal("EUR", zero.Currency);
    }

    [Fact]
    public void Add_Returns_Sum_When_Currencies_Match()
    {
        var result = new Money(100.50m, "EUR").Add(new Money(49.50m, "EUR"));
        Assert.Equal(150.00m, result.Amount);
        Assert.Equal("EUR", result.Currency);
    }

    [Fact]
    public void Add_Throws_CurrencyMismatch_When_Currencies_Differ()
    {
        var ex = Assert.Throws<CurrencyMismatchException>(
            () => new Money(100m, "EUR").Add(new Money(100m, "USD")));
        Assert.Equal("EUR", ex.ExpectedCurrency);
        Assert.Equal("USD", ex.ActualCurrency);
    }

    [Fact]
    public void Subtract_Returns_Difference_When_Currencies_Match()
    {
        var result = new Money(100m, "EUR").Subtract(new Money(30m, "EUR"));
        Assert.Equal(70m, result.Amount);
    }

    [Fact]
    public void Subtract_Throws_CurrencyMismatch_When_Currencies_Differ()
    {
        Assert.Throws<CurrencyMismatchException>(
            () => new Money(100m, "EUR").Subtract(new Money(30m, "JPY")));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(0.0001, true)]
    public void IsPositive_Reflects_Amount(decimal amount, bool expected)
    {
        Assert.Equal(expected, new Money(amount, "EUR").IsPositive);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    public void IsNonNegative_Reflects_Amount(decimal amount, bool expected)
    {
        Assert.Equal(expected, new Money(amount, "EUR").IsNonNegative);
    }

    [Fact]
    public void Equality_Holds_For_Same_Amount_And_Currency()
    {
        var a = new Money(123.45m, "EUR");
        var b = new Money(123.45m, "EUR");
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Inequality_Holds_When_Amount_Differs()
    {
        Assert.NotEqual(new Money(100m, "EUR"), new Money(101m, "EUR"));
    }

    [Fact]
    public void Inequality_Holds_When_Currency_Differs()
    {
        Assert.NotEqual(new Money(100m, "EUR"), new Money(100m, "USD"));
    }

    [Fact]
    public void ToString_Includes_Amount_And_Currency()
    {
        var s = new Money(42.50m, "EUR").ToString();
        Assert.Contains("42.5", s);
        Assert.Contains("EUR", s);
    }
}
