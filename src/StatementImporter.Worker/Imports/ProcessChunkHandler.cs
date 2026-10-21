using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;

namespace StatementImporter.Worker.Imports;

public sealed class ProcessChunkHandler(
    StatementImporterDbContext db,
    IOptions<ImportOptions> options,
    ILogger<ProcessChunkHandler> logger)
{
    public async Task HandleAsync(ProcessChunk message, bool redelivered, CancellationToken cancellationToken)
    {
        var (importId, index) = (message.ImportId, message.ChunkIndex);

        if (redelivered && await RegisterAttemptAsync(importId, index, cancellationToken) > options.Value.MaxAttempts)
        {
            await FailAsync(importId, index, cancellationToken);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var chunk = (await db.ImportChunks
            .FromSql($"SELECT * FROM import_chunks WHERE import_id = {importId} AND chunk_index = {index} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        var import = await db.Imports.AsNoTracking().SingleAsync(i => i.Id == importId, cancellationToken);

        if (chunk is null || chunk.Status != ChunkStatus.Pending || import.Status == ImportStatus.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        List<RawLine> lines;
        await using (var file = File.OpenRead(import.FilePath))
        {
            lines = ChunkReader.Read(file, new ChunkInfo(chunk.ChunkIndex, chunk.StartOffset, chunk.FirstLineNumber, chunk.LineCount));
        }

        if (lines.Count != chunk.LineCount)
            throw new InvalidDataException(
                $"Bloco {index} da importação {importId}: esperadas {chunk.LineCount} linhas, lidas {lines.Count}. O arquivo mudou depois da divisão.");

        var accepted = new List<AcceptedLine>();
        var rejected = new List<RejectedLine>();
        foreach (var line in lines)
        {
            var result = LineValidator.Validate(line.Text);
            if (result.IsValid)
                accepted.Add(new AcceptedLine(line, result.Line!));
            else
                rejected.Add(new RejectedLine(line.LineNumber, line.Text, result.ErrorReason!));
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var inserted = await ChunkWriter.InsertTransactionsAsync(connection, importId, accepted, cancellationToken);
        rejected.AddRange(accepted
            .Where(a => !inserted.Contains(a.Raw.LineNumber))
            .Select(a => new RejectedLine(a.Raw.LineNumber, a.Raw.Text, ErrorReasons.Duplicate)));

        var storeLimit = Math.Max(0, options.Value.MaxStoredErrors - import.ErrorCount);
        await ChunkWriter.InsertErrorsAsync(connection, importId, rejected.OrderBy(r => r.LineNumber).Take(storeLimit), cancellationToken);

        chunk.Status = ChunkStatus.Done;
        chunk.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var lineCount = chunk.LineCount;
        var validCount = inserted.Count;
        var errorCount = rejected.Count;
        await db.Imports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.ProcessedChunks, i => i.ProcessedChunks + 1)
            .SetProperty(i => i.ProcessedLines, i => i.ProcessedLines + lineCount)
            .SetProperty(i => i.ValidCount, i => i.ValidCount + validCount)
            .SetProperty(i => i.ErrorCount, i => i.ErrorCount + errorCount), cancellationToken);

        var progress = await db.Imports.AsNoTracking()
            .Where(i => i.Id == importId)
            .Select(i => new { i.ProcessedChunks, i.TotalChunks, i.ErrorCount })
            .SingleAsync(cancellationToken);

        if (progress.ProcessedChunks == progress.TotalChunks)
        {
            var finalStatus = progress.ErrorCount > 0 ? ImportStatus.CompletedWithErrors : ImportStatus.Completed;
            var completedAt = DateTime.UtcNow;
            await db.Imports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, finalStatus)
                .SetProperty(i => i.CompletedAt, completedAt), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Bloco {ChunkIndex} da importação {ImportId}: {Valid} válidas, {Errors} rejeitadas.",
            index, importId, validCount, errorCount);
    }

    private async Task<int> RegisterAttemptAsync(Guid importId, int index, CancellationToken cancellationToken)
    {
        await db.ImportChunks
            .Where(c => c.ImportId == importId && c.ChunkIndex == index && c.Status == ChunkStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken);

        return await db.ImportChunks
            .Where(c => c.ImportId == importId && c.ChunkIndex == index)
            .Select(c => c.Attempts)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task FailAsync(Guid importId, int index, CancellationToken cancellationToken)
    {
        var reason = $"Bloco {index} falhou após {options.Value.MaxAttempts} tentativas.";
        var completedAt = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var failed = await db.ImportChunks
            .Where(c => c.ImportId == importId && c.ChunkIndex == index && c.Status == ChunkStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ChunkStatus.Failed), cancellationToken);
        if (failed == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        logger.LogError("Importação {ImportId}: {Reason}", importId, reason);
        await db.Imports
            .Where(i => i.Id == importId && i.Status != ImportStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, ImportStatus.Failed)
                .SetProperty(i => i.FailureReason, reason)
                .SetProperty(i => i.CompletedAt, completedAt), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
