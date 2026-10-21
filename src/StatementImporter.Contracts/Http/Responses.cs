namespace StatementImporter.Contracts.Http;

public sealed record ImportResponse(
    Guid Id,
    string FileName,
    string Status,
    int TotalLines,
    int ProcessedLines,
    int ValidCount,
    int ErrorCount,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record ImportErrorResponse(int LineNumber, string RawLine, string Reason);

public sealed record TransactionResponse(string TransactionId, string Date, string Description, decimal Amount, Guid ImportId, int LineNumber);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public sealed record ErrorResponse(string Message);
