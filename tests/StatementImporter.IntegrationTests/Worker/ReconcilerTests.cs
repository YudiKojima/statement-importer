using StatementImporter.Contracts;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.IntegrationTests.Worker;

[Collection(nameof(InfrastructureCollection))]
public class ReconcilerTests(InfrastructureFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task DrainAllAsync()
    {
        await fixture.DrainAsync(QueueTopology.ImportRequested, ContractsJsonContext.Default.ImportRequested);
        await fixture.DrainAsync(QueueTopology.ProcessChunk, ContractsJsonContext.Default.ProcessChunk);
    }

    private async Task<Guid> CreateStalledImportAsync(int lines, int chunkSize)
    {
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines($"STALL-{Guid.NewGuid():N}", lines));
        await using var db = fixture.CreateDbContext();
        await (await fixture.CreateSplitterAsync(db, chunkSize)).HandleAsync(new ImportRequested(import.Id), Ct);
        return import.Id;
    }

    [Fact]
    public async Task Queued_import_without_a_message_is_republished()
    {
        await DrainAllAsync();
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines($"ORPHAN-{Guid.NewGuid():N}", 2));

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateReconcilerAsync(db)).ReconcileAsync(Ct);

        var published = await fixture.DrainAsync(QueueTopology.ImportRequested, ContractsJsonContext.Default.ImportRequested);
        Assert.Contains(new ImportRequested(import.Id), published);
    }

    [Fact]
    public async Task Pending_chunks_of_a_stalled_import_are_republished_when_the_queue_is_empty()
    {
        var importId = await CreateStalledImportAsync(lines: 12, chunkSize: 5);
        await DrainAllAsync();

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateReconcilerAsync(db)).ReconcileAsync(Ct);

        var published = await fixture.DrainAsync(QueueTopology.ProcessChunk, ContractsJsonContext.Default.ProcessChunk);
        Assert.Equal([0, 1, 2], published.Where(m => m.ImportId == importId).Select(m => m.ChunkIndex).Order());
    }

    [Fact]
    public async Task Nothing_is_republished_while_the_queue_still_has_messages()
    {
        var importId = await CreateStalledImportAsync(lines: 12, chunkSize: 5);
        await DrainAllAsync();
        var marker = new ProcessChunk(Guid.NewGuid(), 0);
        await using (var publisher = await MessagePublisher.CreateAsync(await fixture.CreateRabbitConnectionAsync(), Ct))
            await publisher.PublishAsync(QueueTopology.ProcessChunk, marker, ContractsJsonContext.Default.ProcessChunk, Ct);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateReconcilerAsync(db)).ReconcileAsync(Ct);

        var published = await fixture.DrainAsync(QueueTopology.ProcessChunk, ContractsJsonContext.Default.ProcessChunk);
        Assert.Equal([marker], published);
        Assert.DoesNotContain(published, m => m.ImportId == importId);
    }
}
