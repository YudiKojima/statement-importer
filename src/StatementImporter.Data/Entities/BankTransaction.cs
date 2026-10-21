namespace StatementImporter.Data.Entities;

public sealed class BankTransaction
{
    public long Id { get; set; }
    public required string TransactionId { get; set; }
    public DateOnly Date { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }
    public Guid ImportId { get; set; }
    public int LineNumber { get; set; }
}
