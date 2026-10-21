using System.Globalization;
using System.Text.RegularExpressions;

namespace StatementImporter.Contracts.Csv;

public sealed record ValidLine(string TransactionId, DateOnly Date, string Description, decimal Amount);

public readonly record struct LineResult(ValidLine? Line, string? ErrorReason)
{
    public bool IsValid => Line is not null;

    public static LineResult Valid(ValidLine line) => new(line, null);

    public static LineResult Invalid(string reason) => new(null, reason);
}

public static partial class LineValidator
{
    public const int MaxTransactionIdLength = 64;
    public const int MaxDescriptionLength = 200;

    public static LineResult Validate(string line)
    {
        var columns = line.TrimEnd('\r').Split(',');
        if (columns.Length != 4)
            return LineResult.Invalid(ErrorReasons.ColumnCount);

        var (transactionId, date, description, amount) = (columns[0], columns[1], columns[2], columns[3]);

        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > MaxTransactionIdLength)
            return LineResult.Invalid(ErrorReasons.InvalidTransactionId);

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            return LineResult.Invalid(ErrorReasons.InvalidDate);

        if (string.IsNullOrWhiteSpace(description) || description.Length > MaxDescriptionLength)
            return LineResult.Invalid(ErrorReasons.InvalidDescription);

        if (!AmountPattern().IsMatch(amount)
            || !decimal.TryParse(amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedAmount))
            return LineResult.Invalid(ErrorReasons.InvalidAmount);

        return LineResult.Valid(new ValidLine(transactionId, parsedDate, description, parsedAmount));
    }

    [GeneratedRegex(@"^-?[0-9]{1,16}(\.[0-9]{1,2})?$")]
    private static partial Regex AmountPattern();
}
