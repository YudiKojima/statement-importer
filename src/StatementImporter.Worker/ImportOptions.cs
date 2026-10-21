namespace StatementImporter.Worker;

public sealed class ImportOptions
{
    public int ChunkSize { get; set; } = 10_000;
    public int MaxAttempts { get; set; } = 5;
    public int MaxStoredErrors { get; set; } = 10_000;
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan QueuedThreshold { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan StalledThreshold { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan NackDelay { get; set; } = TimeSpan.FromSeconds(1);
}
