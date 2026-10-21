namespace StatementImporter.Data.Entities;

public sealed class ImportError
{
    public long Id { get; set; }
    public Guid ImportId { get; set; }
    public int LineNumber { get; set; }
    public required string RawLine { get; set; }
    public required string Reason { get; set; }
}
