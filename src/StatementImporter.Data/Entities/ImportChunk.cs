namespace StatementImporter.Data.Entities;

public sealed class ImportChunk
{
    public long Id { get; set; }
    public Guid ImportId { get; set; }
    public int ChunkIndex { get; set; }
    public long StartOffset { get; set; }
    public int FirstLineNumber { get; set; }
    public int LineCount { get; set; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
