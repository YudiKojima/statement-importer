using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using StatementImporter.Contracts.Configuration;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;
using StatementImporter.Worker;
using StatementImporter.Worker.Hosting;
using StatementImporter.Worker.Imports;

if (args.Contains("--healthcheck"))
    return Heartbeat.Check(DateTime.UtcNow);

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddDockerSecrets();

builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection("Import"));

var connectionString = ServiceConfiguration.BuildDbConnectionString(builder.Configuration);
builder.Services.AddDbContext<StatementImporterDbContext>(o => StatementImporterDbContext.Configure(o, connectionString));

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
builder.Services.AddSingleton(rabbit);
builder.Services.AddSingleton(await MessagePublisher.CreateAsync(rabbit));

builder.Services.AddScoped<ImportRequestedHandler>();
builder.Services.AddScoped<ProcessChunkHandler>();
builder.Services.AddScoped<ImportReconciler>();
builder.Services.AddHostedService<ConsumerHost>();
builder.Services.AddHostedService<Reconciler>();

await builder.Build().RunAsync();
return Environment.ExitCode;
