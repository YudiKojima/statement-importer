using StatementImporter.Worker.Hosting;

namespace StatementImporter.UnitTests.Hosting;

public class HeartbeatTests
{
    [Fact]
    public void Healthy_only_while_the_heartbeat_is_recent()
    {
        Heartbeat.FilePath = Path.Combine(Path.GetTempPath(), $"heartbeat-{Guid.NewGuid()}");
        Assert.Equal(1, Heartbeat.Check(DateTime.UtcNow));

        Heartbeat.Beat();

        Assert.Equal(0, Heartbeat.Check(DateTime.UtcNow));
        Assert.Equal(1, Heartbeat.Check(DateTime.UtcNow + Heartbeat.MaxAge));
        File.Delete(Heartbeat.FilePath);
    }
}
