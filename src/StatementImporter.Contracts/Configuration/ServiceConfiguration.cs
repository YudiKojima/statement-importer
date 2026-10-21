using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;

namespace StatementImporter.Contracts.Configuration;

public static class ServiceConfiguration
{
    public const string DockerSecretsPath = "/run/secrets";

    public static IConfigurationBuilder AddDockerSecrets(this IConfigurationBuilder builder, string path = DockerSecretsPath) =>
        Directory.Exists(path) ? builder.AddKeyPerFile(path, optional: true) : builder;

    public static string BuildDbConnectionString(IConfiguration config) => new NpgsqlConnectionStringBuilder
    {
        Host = config["Db:Host"] ?? "localhost",
        Port = int.TryParse(config["Db:Port"], out var port) ? port : 5432,
        Database = config["Db:Database"] ?? "statement_importer",
        Username = config["Db:Username"] ?? "postgres",
        Password = config["Db:Password"]?.TrimEnd('\r', '\n'),
    }.ConnectionString;

    public static ConnectionFactory CreateRabbitMqFactory(IConfiguration config) => new()
    {
        HostName = config["RabbitMq:Host"] ?? "localhost",
        Port = int.TryParse(config["RabbitMq:Port"], out var port) ? port : AmqpTcpEndpoint.UseDefaultPort,
        UserName = config["RabbitMq:Username"] ?? "guest",
        Password = config["RabbitMq:Password"] ?? "guest",
        AutomaticRecoveryEnabled = false,
    };
}
