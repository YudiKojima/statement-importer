using StatementImporter.Contracts.Csv;

namespace StatementImporter.UnitTests.Csv;

public class LineValidatorTests
{
    [Fact]
    public void Valid_line_is_parsed()
    {
        var result = LineValidator.Validate("TX-1,2026-03-15,PIX RECEBIDO JOAO,1500.00");

        Assert.True(result.IsValid);
        Assert.Equal(new ValidLine("TX-1", new DateOnly(2026, 3, 15), "PIX RECEBIDO JOAO", 1500.00m), result.Line);
    }

    [Theory]
    [InlineData("TX-1,2026-03-15,COMPRA,-234.9", -234.9)]
    [InlineData("TX-1,2026-03-15,COMPRA,10", 10)]
    [InlineData("TX-1,2026-03-15,COMPRA,0.01\r", 0.01)]
    public void Accepted_amount_formats(string line, double expected)
    {
        var result = LineValidator.Validate(line);

        Assert.True(result.IsValid);
        Assert.Equal((decimal)expected, result.Line!.Amount);
    }

    [Theory]
    [InlineData("TX-1,2026-03-15,COMPRA", ErrorReasons.ColumnCount)]
    [InlineData("TX-1,2026-03-15,COMPRA,10.00,extra", ErrorReasons.ColumnCount)]
    [InlineData(",2026-03-15,COMPRA,10.00", ErrorReasons.InvalidTransactionId)]
    [InlineData("   ,2026-03-15,COMPRA,10.00", ErrorReasons.InvalidTransactionId)]
    [InlineData("TX-1,2026-02-30,COMPRA,10.00", ErrorReasons.InvalidDate)]
    [InlineData("TX-1,15/03/2026,COMPRA,10.00", ErrorReasons.InvalidDate)]
    [InlineData("TX-1,2026-03-15,,10.00", ErrorReasons.InvalidDescription)]
    [InlineData("TX-1,2026-03-15,COMPRA,10,50", ErrorReasons.ColumnCount)]
    [InlineData("TX-1,2026-03-15,COMPRA,10.555", ErrorReasons.InvalidAmount)]
    [InlineData("TX-1,2026-03-15,COMPRA,abc", ErrorReasons.InvalidAmount)]
    [InlineData("TX-1,2026-03-15,COMPRA,+10.00", ErrorReasons.InvalidAmount)]
    [InlineData("TX-1,2026-03-15,COMPRA,12345678901234567.00", ErrorReasons.InvalidAmount)]
    public void Invalid_lines_report_the_first_failing_rule(string line, string expectedReason)
    {
        var result = LineValidator.Validate(line);

        Assert.False(result.IsValid);
        Assert.Equal(expectedReason, result.ErrorReason);
    }

    [Fact]
    public void Transaction_id_longer_than_64_characters_is_invalid()
    {
        var result = LineValidator.Validate($"{new string('X', 65)},2026-03-15,COMPRA,10.00");

        Assert.Equal(ErrorReasons.InvalidTransactionId, result.ErrorReason);
    }

    [Fact]
    public void Description_limit_is_200_characters()
    {
        Assert.True(LineValidator.Validate($"TX-1,2026-03-15,{new string('D', 200)},10.00").IsValid);
        Assert.Equal(ErrorReasons.InvalidDescription, LineValidator.Validate($"TX-1,2026-03-15,{new string('D', 201)},10.00").ErrorReason);
    }
}
