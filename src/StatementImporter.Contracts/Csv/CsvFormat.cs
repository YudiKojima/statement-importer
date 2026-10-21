namespace StatementImporter.Contracts.Csv;

public static class CsvFormat
{
    public const string Header = "transaction_id,date,description,amount";

    public static bool IsHeader(string line) => line.TrimStart('﻿').TrimEnd('\r') == Header;

    public static bool IsBlank(ReadOnlySpan<char> line) => line.Trim(" \t\r").IsEmpty;
}
