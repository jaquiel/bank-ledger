using Ledger.Domain;
using Shouldly;
using Xunit;

namespace Ledger.UnitTests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0.01")]
    [InlineData("10")]
    [InlineData("1234.50")]
    [InlineData("1000000000")]
    public void Create_accepts_positive_values_with_up_to_two_decimals(string value)
    {
        var result = Money.Create(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.001")]
    [InlineData("1000000000.01")]
    public void Create_rejects_zero_negative_excess_precision_and_above_maximum(string value)
    {
        var result = Money.Create(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("invalid_amount");
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Create_treats_trailing_zeros_as_the_same_value()
    {
        Money.Create(10.100m).Value.ShouldBe(Money.Create(10.1m).Value);
    }
}

public sealed class MovementTests
{
    [Theory]
    [InlineData(EntryType.Credit, 25.50, 25.50)]
    [InlineData(EntryType.Debit, 25.50, -25.50)]
    public void Delta_is_positive_for_credit_and_negative_for_debit(EntryType type, decimal amount, decimal expectedDelta)
    {
        Movement.Create(type, amount, null).Value.Delta.ShouldBe(expectedDelta);
    }

    [Fact]
    public void Create_trims_description_and_treats_blank_as_null()
    {
        Movement.Create(EntryType.Credit, 1, "  Pix recebido  ").Value.Description.ShouldBe("Pix recebido");
        Movement.Create(EntryType.Credit, 1, "   ").Value.Description.ShouldBeNull();
    }

    [Fact]
    public void Create_rejects_description_longer_than_limit()
    {
        var result = Movement.Create(EntryType.Debit, 1, new string('x', Movement.MaxDescriptionLength + 1));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("invalid_description");
    }

    [Fact]
    public void Create_rejects_undefined_entry_type()
    {
        Movement.Create((EntryType)42, 1, null).Error!.Code.ShouldBe("invalid_type");
    }

    [Theory]
    [InlineData(EntryType.Credit, EntryType.Debit)]
    [InlineData(EntryType.Debit, EntryType.Credit)]
    public void Reversal_uses_the_opposite_entry_type(EntryType original, EntryType reversal)
    {
        original.Opposite().ShouldBe(reversal);
    }

    [Theory]
    [InlineData(EntryType.Credit)]
    [InlineData(EntryType.Debit)]
    public void Entry_type_code_round_trips(EntryType type)
    {
        EntryTypeExtensions.FromCode(type.ToCode()).ShouldBe(type);
    }
}
