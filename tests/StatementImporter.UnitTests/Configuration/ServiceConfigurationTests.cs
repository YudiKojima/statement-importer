using Microsoft.Extensions.Configuration;
using Npgsql;
using StatementImporter.Contracts.Configuration;

namespace StatementImporter.UnitTests.Configuration;

public class ServiceConfigurationTests
{
    [Fact]
    public void Connection_string_comes_from_db_settings_and_trims_the_secret_newline()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Db:Host"] = "postgres",
            ["Db:Port"] = "5433",
            ["Db:Database"] = "statement_importer",
            ["Db:Username"] = "importer",
            ["Db:Password"] = "s3cret\n",
        }).Build();

        var builder = new NpgsqlConnectionStringBuilder(ServiceConfiguration.BuildDbConnectionString(config));

        Assert.Equal(("postgres", 5433, "statement_importer", "importer", "s3cret"),
            (builder.Host, builder.Port, builder.Database, builder.Username, builder.Password));
    }

    [Fact]
    public void Docker_secrets_are_read_as_configuration_keys()
    {
        var dir = Directory.CreateTempSubdirectory("secrets").FullName;
        File.WriteAllText(Path.Combine(dir, "Db__Password"), "from-file");

        var config = new ConfigurationBuilder().AddDockerSecrets(dir).Build();

        Assert.Equal("from-file", config["Db:Password"]);
    }

    [Fact]
    public void Missing_secrets_directory_is_ignored()
    {
        var config = new ConfigurationBuilder().AddDockerSecrets(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).Build();

        Assert.Null(config["Db:Password"]);
    }
}
