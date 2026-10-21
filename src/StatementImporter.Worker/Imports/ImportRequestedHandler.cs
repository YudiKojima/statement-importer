using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;
using StatementImporter.Data.Entities;

namespace StatementImporter.Worker.Imports;

public sealed class ImportRequestedHandler(
    StatementImporterDbContext db,
    MessagePublisher publisher,
    IOptions<ImportOptions> options,
    ILogger<ImportRequestedHandler> logger)
{
    public async Task HandleAsync(ImportRequested message, CancellationToken cancellationToken)
    {
        string status;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var import = (await db.Imports
                .FromSql($"SELECT * FROM imports WHERE id = {message.ImportId} FOR UPDATE")
                .ToListAsync(cancellationToken)).SingleOrDefault();

            if (import is null)
            {
                logger.LogWarning("Importação {ImportId} não encontrada.", message.ImportId);
                return;
            }

            if (import.Status == ImportStatus.Queued)
                Split(import);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            status = import.Status;
        }

        if (status != ImportStatus.Processing)
            return;

        var pending = await db.ImportChunks
            .Where(c => c.ImportId == message.ImportId && c.Status == ChunkStatus.Pending)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.ChunkIndex)
            .ToListAsync(cancellationToken);

        foreach (var index in pending)
        {
            await publisher.PublishAsync(QueueTopology.ProcessChunk, new ProcessChunk(message.ImportId, index),
                ContractsJsonContext.Default.ProcessChunk, cancellationToken);
        }
    }

    private void Split(Import import)
    {
        var now = DateTime.UtcNow;
        ChunkPlan plan;
        try
        {
            using var file = File.OpenRead(import.FilePath);
            plan = ChunkPlanner.Plan(file, options.Value.ChunkSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Não foi possível ler o arquivo da importação {ImportId}.", import.Id);
            import.Status = ImportStatus.Failed;
            import.FailureReason = $"Arquivo ausente ou ilegível: {ex.Message}";
            import.CompletedAt = now;
            return;
        }

        import.TotalLines = plan.TotalLines;
        import.TotalChunks = plan.Chunks.Count;
        import.StartedAt = now;

        if (plan.TotalLines == 0)
        {
            import.Status = ImportStatus.Completed;
            import.CompletedAt = now;
            return;
        }

        import.Status = ImportStatus.Processing;
        db.ImportChunks.AddRange(plan.Chunks.Select(c => new ImportChunk
        {
            ImportId = import.Id,
            ChunkIndex = c.Index,
            StartOffset = c.StartOffset,
            FirstLineNumber = c.FirstLineNumber,
            LineCount = c.LineCount,
            Status = ChunkStatus.Pending,
        }));
    }
}
