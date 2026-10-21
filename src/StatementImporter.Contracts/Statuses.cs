namespace StatementImporter.Contracts;

public static class ImportStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Failed = "failed";
}

public static class ChunkStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Failed = "failed";
}
