using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;
using StatementImporter.Data.Entities;
using StatementImporter.Worker;
using StatementImporter.Worker.Imports;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace StatementImporter.IntegrationTests;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private IConnection? _rabbitConnection;

    public string UploadsPath { get; } = Directory.CreateTempSubdirectory("statement-importer-uploads").FullName;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        await using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        var pg = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        var amqp = new Uri(_rabbit.GetConnectionString());
        var credentials = amqp.UserInfo.Split(':');
        var settings = new Dictionary<string, string?>
        {
            ["Db__Host"] = pg.Host,
            ["Db__Port"] = pg.Port.ToString(),
            ["Db__Database"] = pg.Database,
            ["Db__Username"] = pg.Username,
            ["Db__Password"] = pg.Password,
            ["RabbitMq__Host"] = amqp.Host,
            ["RabbitMq__Port"] = amqp.Port.ToString(),
            ["RabbitMq__Username"] = Uri.UnescapeDataString(credentials[0]),
            ["RabbitMq__Password"] = Uri.UnescapeDataString(credentials[1]),
            ["Import__UploadsPath"] = UploadsPath,
        };
        foreach (var (key, value) in settings)
            Environment.SetEnvironmentVariable(key, value);

        _rabbitConnection = await new ConnectionFactory { Uri = amqp }.CreateConnectionAsync();
    }

    public StatementImporterDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<StatementImporterDbContext>();
        StatementImporterDbContext.Configure(options, _postgres.GetConnectionString());
        return new StatementImporterDbContext(options.Options);
    }

    public async Task<ImportRequestedHandler> CreateSplitterAsync(StatementImporterDbContext db, int chunkSize) =>
        new(db, await MessagePublisher.CreateAsync(_rabbitConnection!), Options(chunkSize), NullLogger<ImportRequestedHandler>.Instance);

    public static ProcessChunkHandler CreateProcessor(StatementImporterDbContext db, int chunkSize, int maxStoredErrors = 10_000) =>
        new(db, Options(chunkSize, maxStoredErrors), NullLogger<ProcessChunkHandler>.Instance);

    public async Task<ImportReconciler> CreateReconcilerAsync(StatementImporterDbContext db) =>
        new(db, _rabbitConnection!, await MessagePublisher.CreateAsync(_rabbitConnection!),
            Microsoft.Extensions.Options.Options.Create(new ImportOptions { QueuedThreshold = TimeSpan.Zero, StalledThreshold = TimeSpan.Zero }),
            NullLogger<ImportReconciler>.Instance);

    public Task<IConnection> CreateRabbitConnectionAsync() =>
        new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()) }.CreateConnectionAsync();

    public async Task<List<T>> DrainAsync<T>(string queue, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        await using var channel = await _rabbitConnection!.CreateChannelAsync();
        var messages = new List<T>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
            messages.Add(System.Text.Json.JsonSerializer.Deserialize(message.Body.Span, typeInfo)!);
        return messages;
    }

    public async Task<Import> CreateQueuedImportAsync(IEnumerable<string> dataLines)
    {
        var id = Guid.CreateVersion7();
        var path = Path.Combine(UploadsPath, $"{id}.csv");
        await File.WriteAllLinesAsync(path, [CsvFormat.Header, .. dataLines]);

        var import = new Import
        {
            Id = id,
            FileName = "teste.csv",
            FileHash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, '0'),
            FilePath = path,
            Status = ImportStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        await using var db = CreateDbContext();
        db.Imports.Add(import);
        await db.SaveChangesAsync();
        return import;
    }

    public async Task ProcessAllAsync(Guid importId, int chunkSize)
    {
        await using (var db = CreateDbContext())
            await (await CreateSplitterAsync(db, chunkSize)).HandleAsync(new ImportRequested(importId), CancellationToken.None);

        await using var reader = CreateDbContext();
        var indexes = await reader.ImportChunks.Where(c => c.ImportId == importId).Select(c => c.ChunkIndex).ToListAsync();
        foreach (var index in indexes)
        {
            await using var db = CreateDbContext();
            await CreateProcessor(db, chunkSize).HandleAsync(new ProcessChunk(importId, index), redelivered: false, CancellationToken.None);
        }
    }

    public static IEnumerable<string> ValidLines(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => $"{prefix}-{i:D6},2026-03-15,LINHA {i},{i}.50");

    private static IOptions<ImportOptions> Options(int chunkSize, int maxStoredErrors = 10_000) =>
        Microsoft.Extensions.Options.Options.Create(new ImportOptions { ChunkSize = chunkSize, MaxStoredErrors = maxStoredErrors });

    public async ValueTask DisposeAsync()
    {
        if (_rabbitConnection is not null)
            await _rabbitConnection.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

[CollectionDefinition(nameof(InfrastructureCollection))]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>;
