using StatementImporter.Contracts.Csv;

namespace StatementImporter.UnitTests.Csv;

public class CsvFormatTests
{
    [Theory]
    [InlineData("transaction_id,date,description,amount")]
    [InlineData("transaction_id,date,description,amount\r")]
    [InlineData("﻿transaction_id,date,description,amount")]
    public void Header_is_recognized_with_bom_and_crlf(string line) => Assert.True(CsvFormat.IsHeader(line));

    [Theory]
    [InlineData("id,date,description,amount")]
    [InlineData("transaction_id,date,amount,description")]
    [InlineData("")]
    public void Other_headers_are_rejected(string line) => Assert.False(CsvFormat.IsHeader(line));

    [Theory]
    [InlineData("", true)]
    [InlineData("   \t\r", true)]
    [InlineData(" x ", false)]
    public void Blank_lines(string line, bool expected) => Assert.Equal(expected, CsvFormat.IsBlank(line));
}
