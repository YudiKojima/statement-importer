namespace StatementImporter.Data.Entities;

public sealed class Import
{
    public Guid Id { get; set; }
    public required string FileName { get; set; }
    public required string FileHash { get; set; }
    public required string FilePath { get; set; }
    public required string Status { get; set; }
    public int TotalLines { get; set; }
    public int TotalChunks { get; set; }
    public int ProcessedChunks { get; set; }
    public int ProcessedLines { get; set; }
    public int ValidCount { get; set; }
    public int ErrorCount { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
