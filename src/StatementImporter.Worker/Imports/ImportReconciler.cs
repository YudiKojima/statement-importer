using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;

namespace StatementImporter.Worker.Imports;

public sealed class ImportReconciler(
    StatementImporterDbContext db,
    IConnection connection,
    MessagePublisher publisher,
    IOptions<ImportOptions> options,
    ILogger<ImportReconciler> logger)
{
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        var now = DateTime.UtcNow;

        if (await channel.MessageCountAsync(QueueTopology.ImportRequested, cancellationToken) == 0)
            await RepublishQueuedImportsAsync(now - options.Value.QueuedThreshold, cancellationToken);

        if (await channel.MessageCountAsync(QueueTopology.ProcessChunk, cancellationToken) == 0)
            await RepublishStalledChunksAsync(now - options.Value.StalledThreshold, cancellationToken);
    }

    private async Task RepublishQueuedImportsAsync(DateTime createdBefore, CancellationToken cancellationToken)
    {
        var stale = await db.Imports
            .Where(i => i.Status == ImportStatus.Queued && i.CreatedAt < createdBefore)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in stale)
        {
            logger.LogInformation("Republicando importação {ImportId} parada em queued.", id);
            await publisher.PublishAsync(QueueTopology.ImportRequested, new ImportRequested(id),
                ContractsJsonContext.Default.ImportRequested, cancellationToken);
        }
    }

    private async Task RepublishStalledChunksAsync(DateTime noProgressSince, CancellationToken cancellationToken)
    {
        var stalled = await db.ImportChunks
            .Where(c => c.Status == ChunkStatus.Pending
                && db.Imports.Any(i => i.Id == c.ImportId && i.Status == ImportStatus.Processing && i.StartedAt < noProgressSince)
                && !db.ImportChunks.Any(d => d.ImportId == c.ImportId && d.ProcessedAt >= noProgressSince))
            .Select(c => new ProcessChunk(c.ImportId, c.ChunkIndex))
            .ToListAsync(cancellationToken);

        if (stalled.Count > 0)
            logger.LogWarning("Fila de blocos vazia com {Count} blocos pendentes sem progresso; republicando.", stalled.Count);

        foreach (var message in stalled)
            await publisher.PublishAsync(QueueTopology.ProcessChunk, message, ContractsJsonContext.Default.ProcessChunk, cancellationToken);
    }
}
