using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using StatementImporter.Contracts.Configuration;

namespace StatementImporter.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<StatementImporterDbContext>
{
    public StatementImporterDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddDockerSecrets()
            .Build();

        var options = new DbContextOptionsBuilder<StatementImporterDbContext>();
        StatementImporterDbContext.Configure(options, ServiceConfiguration.BuildDbConnectionString(config));
        return new StatementImporterDbContext(options.Options);
    }
}
