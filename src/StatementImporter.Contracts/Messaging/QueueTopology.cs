using RabbitMQ.Client;

namespace StatementImporter.Contracts.Messaging;

public static class QueueTopology
{
    public const string ImportRequested = "import-requested";
    public const string ProcessChunk = "process-chunk";

    private static readonly Dictionary<string, object?> QuorumArguments = new() { ["x-queue-type"] = "quorum" };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        foreach (var queue in new[] { ImportRequested, ProcessChunk })
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: QuorumArguments, cancellationToken: cancellationToken);
        }
    }
}
