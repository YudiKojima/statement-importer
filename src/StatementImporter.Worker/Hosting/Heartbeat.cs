namespace StatementImporter.Worker.Hosting;

public static class Heartbeat
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    public static string FilePath { get; set; } = Path.Combine(Path.GetTempPath(), "heartbeat");

    public static void Beat() => File.WriteAllText(FilePath, DateTime.UtcNow.ToString("O"));

    public static int Check(DateTime utcNow) =>
        File.Exists(FilePath) && utcNow - File.GetLastWriteTimeUtc(FilePath) < MaxAge ? 0 : 1;
}
