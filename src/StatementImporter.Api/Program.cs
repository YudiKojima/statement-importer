using Dapper;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Scalar.AspNetCore;
using StatementImporter.Api;
using StatementImporter.Api.Imports;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Configuration;
using StatementImporter.Contracts.Messaging;

[module: DapperAot]

if (args.Contains("--healthcheck"))
    return await HealthCheck.RunAsync();

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddDockerSecrets();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = UploadStorage.MaxUploadBytes);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default));
builder.Services.AddOpenApi();

builder.Services.AddSingleton(NpgsqlDataSource.Create(ServiceConfiguration.BuildDbConnectionString(builder.Configuration)));
builder.Services.AddSingleton<ImportRepository>();
builder.Services.AddSingleton(new UploadStorage(builder.Configuration["Import:UploadsPath"] ?? "/data/uploads"));

IConnection rabbit;
try
{
    rabbit = await ServiceConfiguration.CreateRabbitMqFactory(builder.Configuration).CreateConnectionAsync();
}
catch (BrokerUnreachableException ex)
{
    Console.Error.WriteLine($"RabbitMQ indisponível: {ex.Message}");
    return 1;
}
var publisher = await MessagePublisher.CreateAsync(rabbit);
builder.Services.AddSingleton(rabbit);
builder.Services.AddSingleton(publisher);

var app = builder.Build();

Task StopOnQueueLoss(object _, ShutdownEventArgs e)
{
    if (e.Initiator != ShutdownInitiator.Application)
    {
        app.Logger.LogCritical("Conexão ou canal do RabbitMQ fechado ({Reason}); encerrando para o restart policy religar a API.", e.ReplyText);
        Environment.ExitCode = 1;
        app.Lifetime.StopApplication();
    }
    return Task.CompletedTask;
}
rabbit.ConnectionShutdownAsync += StopOnQueueLoss;
publisher.ShutdownAsync += StopOnQueueLoss;

app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapScalarApiReference("/api/docs", o => o.OpenApiRoutePattern = "/api/openapi/{documentName}.json");

app.MapGet("/health", async (NpgsqlDataSource dataSource, IConnection queue, CancellationToken cancellationToken) =>
{
    if (!queue.IsOpen || !publisher.IsOpen)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return Results.Ok();
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapImportEndpoints();

await app.RunAsync();
return Environment.ExitCode;

public partial class Program;
