using Microsoft.EntityFrameworkCore;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.IntegrationTests.Worker;

[Collection(nameof(InfrastructureCollection))]
public class ImportProcessingTests(InfrastructureFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Split_covers_every_line_exactly_once()
    {
        var prefix = $"SPLIT-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync([.. InfrastructureFixture.ValidLines(prefix, 23), "", "   "]);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 5)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        var chunks = await check.ImportChunks.Where(c => c.ImportId == import.Id).OrderBy(c => c.ChunkIndex).ToListAsync(Ct);

        Assert.Equal(ImportStatus.Processing, saved.Status);
        Assert.Equal(23, saved.TotalLines);
        Assert.Equal(5, saved.TotalChunks);
        Assert.Equal([5, 5, 5, 5, 3], chunks.Select(c => c.LineCount));
        Assert.All(chunks, c => Assert.Equal(ChunkStatus.Pending, c.Status));
    }

    [Fact]
    public async Task Header_only_file_completes_without_chunks()
    {
        var import = await fixture.CreateQueuedImportAsync([]);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal((ImportStatus.Completed, 0, 0), (saved.Status, saved.TotalLines, saved.TotalChunks));
    }

    [Fact]
    public async Task Missing_file_fails_the_import()
    {
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines($"GONE-{Guid.NewGuid():N}", 2));
        File.Delete(import.FilePath);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(ImportStatus.Failed, saved.Status);
        Assert.NotNull(saved.FailureReason);
    }

    [Fact]
    public async Task Processing_the_same_chunk_twice_does_not_duplicate()
    {
        var prefix = $"TWICE-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines(prefix, 25));
        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        foreach (var (index, redelivered) in new[] { (0, false), (0, true), (1, false), (2, false), (2, true) })
        {
            await using var db = fixture.CreateDbContext();
            await InfrastructureFixture.CreateProcessor(db, chunkSize: 10)
                .HandleAsync(new ProcessChunk(import.Id, index), redelivered, Ct);
        }

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(25, await check.Transactions.CountAsync(t => t.ImportId == import.Id, Ct));
        Assert.Equal((ImportStatus.Completed, 3, 25, 25, 0), (saved.Status, saved.ProcessedChunks, saved.ProcessedLines, saved.ValidCount, saved.ErrorCount));
    }

    [Fact]
    public async Task Concurrent_and_duplicate_deliveries_produce_exact_counts()
    {
        var prefix = $"CONC-{Guid.NewGuid():N}";
        var lines = InfrastructureFixture.ValidLines(prefix, 48).ToList();
        lines.Add($"{prefix}-000001,2026-03-16,REPETIDA NO FIM,1.00");
        lines.Add($"{prefix}-000006,2026-03-16,REPETIDA NO FIM,1.00");
        var import = await fixture.CreateQueuedImportAsync(lines);
        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 5)).HandleAsync(new ImportRequested(import.Id), Ct);

        await Task.WhenAll(Enumerable.Range(0, 10).SelectMany(i => new[] { i, i }).Select(async index =>
        {
            await using var db = fixture.CreateDbContext();
            await InfrastructureFixture.CreateProcessor(db, chunkSize: 5)
                .HandleAsync(new ProcessChunk(import.Id, index), redelivered: false, Ct);
        }));

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(48, await check.Transactions.CountAsync(t => t.ImportId == import.Id, Ct));
        Assert.Equal((ImportStatus.CompletedWithErrors, 10, 50, 48, 2),
            (saved.Status, saved.ProcessedChunks, saved.ProcessedLines, saved.ValidCount, saved.ErrorCount));
        Assert.Equal(2, await check.ImportErrors.CountAsync(e => e.ImportId == import.Id && e.Reason == ErrorReasons.Duplicate, Ct));
    }

    [Fact]
    public async Task File_shorter_than_planned_fails_loudly_without_writing()
    {
        var prefix = $"TRUNC-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines(prefix, 10));
        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);
        await File.WriteAllLinesAsync(import.FilePath, [CsvFormat.Header, .. InfrastructureFixture.ValidLines(prefix, 5)], Ct);

        await using (var db = fixture.CreateDbContext())
            await Assert.ThrowsAsync<InvalidDataException>(() => InfrastructureFixture.CreateProcessor(db, chunkSize: 10)
                .HandleAsync(new ProcessChunk(import.Id, 0), redelivered: false, Ct));

        await using var check = fixture.CreateDbContext();
        Assert.Equal(0, await check.Transactions.CountAsync(t => t.ImportId == import.Id, Ct));
        Assert.Equal(ChunkStatus.Pending, (await check.ImportChunks.SingleAsync(c => c.ImportId == import.Id, Ct)).Status);
    }

    [Fact]
    public async Task Invalid_lines_are_reported_and_valid_ones_are_kept()
    {
        var prefix = $"MIX-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync(
        [
            $"{prefix}-1,2026-03-15,OK,1.00",
            $"{prefix}-2,2026-02-30,DATA RUIM,1.00",
            $"{prefix}-3,2026-03-15,VALOR RUIM,1,00",
            $"{prefix}-4,2026-03-15,OK,2.00",
        ]);

        await fixture.ProcessAllAsync(import.Id, chunkSize: 3);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        var errors = await check.ImportErrors.Where(e => e.ImportId == import.Id).OrderBy(e => e.LineNumber).ToListAsync(Ct);

        Assert.Equal((ImportStatus.CompletedWithErrors, 4, 2, 2), (saved.Status, saved.TotalLines, saved.ValidCount, saved.ErrorCount));
        Assert.Equal([(3, ErrorReasons.InvalidDate), (4, ErrorReasons.ColumnCount)], errors.Select(e => (e.LineNumber, e.Reason)));
        Assert.Equal(saved.TotalLines, saved.ValidCount + saved.ErrorCount);
    }

    [Fact]
    public async Task Transaction_id_repeated_across_files_is_reported_as_duplicate()
    {
        var prefix = $"DUP-{Guid.NewGuid():N}";
        var first = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines(prefix, 3));
        await fixture.ProcessAllAsync(first.Id, chunkSize: 10);

        var second = await fixture.CreateQueuedImportAsync([$"{prefix}-000002,2026-03-16,REPETIDA,9.99", $"{prefix}-NOVA,2026-03-16,NOVA,1.00"]);
        await fixture.ProcessAllAsync(second.Id, chunkSize: 10);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == second.Id, Ct);
        var error = await check.ImportErrors.SingleAsync(e => e.ImportId == second.Id, Ct);

        Assert.Equal((1, 1), (saved.ValidCount, saved.ErrorCount));
        Assert.Equal((2, ErrorReasons.Duplicate), (error.LineNumber, error.Reason));
    }

    [Fact]
    public async Task Stored_errors_respect_the_limit_but_every_error_is_counted()
    {
        var import = await fixture.CreateQueuedImportAsync(Enumerable.Range(1, 8).Select(i => $"BAD-{i},nao-e-data,X,1.00"));

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);
        await using (var db = fixture.CreateDbContext())
            await InfrastructureFixture.CreateProcessor(db, chunkSize: 10, maxStoredErrors: 5)
                .HandleAsync(new ProcessChunk(import.Id, 0), redelivered: false, Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(8, saved.ErrorCount);
        Assert.Equal(5, await check.ImportErrors.CountAsync(e => e.ImportId == import.Id, Ct));
    }

    [Fact]
    public async Task Chunk_redelivered_beyond_the_limit_fails_the_import()
    {
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines($"FAIL-{Guid.NewGuid():N}", 2));
        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);
        await using (var db = fixture.CreateDbContext())
            await db.ImportChunks.Where(c => c.ImportId == import.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, 5), Ct);

        await using (var db = fixture.CreateDbContext())
            await InfrastructureFixture.CreateProcessor(db, chunkSize: 10)
                .HandleAsync(new ProcessChunk(import.Id, 0), redelivered: true, Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(ImportStatus.Failed, saved.Status);
        Assert.Equal(ChunkStatus.Failed, (await check.ImportChunks.SingleAsync(c => c.ImportId == import.Id, Ct)).Status);
        Assert.Equal(0, await check.Transactions.CountAsync(t => t.ImportId == import.Id, Ct));
    }
}
