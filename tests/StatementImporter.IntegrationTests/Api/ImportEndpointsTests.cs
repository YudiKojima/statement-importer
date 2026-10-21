using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Http;

namespace StatementImporter.IntegrationTests.Api;

[Collection(nameof(InfrastructureCollection))]
public sealed class ImportEndpointsTests(InfrastructureFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _ = fixture;
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    private static StringContent Csv(string content) => new(content, Encoding.UTF8, "text/csv");

    private static string UniqueCsv() =>
        CsvFormat.Header + "\n" + string.Join("\n", InfrastructureFixture.ValidLines($"API-{Guid.NewGuid():N}", 3)) + "\n";

    [Fact]
    public async Task Upload_is_accepted_and_queued()
    {
        var response = await _client.PostAsync("/api/imports?fileName=extrato.csv", Csv(UniqueCsv()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var import = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        Assert.Equal(("extrato.csv", ImportStatus.Queued), (import!.FileName, import.Status));
        Assert.Equal($"/api/imports/{import.Id}", response.Headers.Location!.OriginalString);
        Assert.True(File.Exists(Path.Combine(fixture.UploadsPath, $"{import.Id}.csv")));
    }

    [Fact]
    public async Task Uploading_the_same_file_twice_returns_the_same_import()
    {
        var content = UniqueCsv();
        var filesBefore = Directory.GetFiles(fixture.UploadsPath).Length;

        var first = await _client.PostAsync("/api/imports?fileName=a.csv", Csv(content), TestContext.Current.CancellationToken);
        var second = await _client.PostAsync("/api/imports?fileName=b.csv", Csv(content), TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var firstImport = await first.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        var secondImport = await second.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        Assert.Equal(firstImport!.Id, secondImport!.Id);
        Assert.Single(Directory.GetFiles(fixture.UploadsPath), f => f.EndsWith($"{firstImport.Id}.csv"));
        Assert.Equal(filesBefore + 1, Directory.GetFiles(fixture.UploadsPath).Length);
    }

    [Theory]
    [InlineData("id,valor\nTX-1,10.00\n")]
    [InlineData("")]
    public async Task Invalid_header_is_rejected(string content)
    {
        var response = await _client.PostAsync("/api/imports?fileName=ruim.csv", Csv(content), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_file_name_is_rejected()
    {
        var response = await _client.PostAsync("/api/imports", Csv(UniqueCsv()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_import_returns_not_found()
    {
        var response = await _client.GetAsync($"/api/imports/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Processed_import_exposes_errors_and_transactions()
    {
        var prefix = $"READ-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync([$"{prefix}-1,2026-03-15,OK,10.50", $"{prefix}-2,data,RUIM,1.00"]);
        await fixture.ProcessAllAsync(import.Id, chunkSize: 10);

        var status = await _client.GetFromJsonAsync($"/api/imports/{import.Id}", ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        var errors = await _client.GetFromJsonAsync($"/api/imports/{import.Id}/errors", ContractsJsonContext.Default.PagedResponseImportErrorResponse, TestContext.Current.CancellationToken);
        var transactions = await _client.GetFromJsonAsync($"/api/transactions?importId={import.Id}", ContractsJsonContext.Default.PagedResponseTransactionResponse, TestContext.Current.CancellationToken);

        Assert.Equal((ImportStatus.CompletedWithErrors, 1, 1), (status!.Status, status.ValidCount, status.ErrorCount));
        Assert.Equal((3, ErrorReasons.InvalidDate), (errors!.Items.Single().LineNumber, errors.Items.Single().Reason));
        Assert.Equal(new TransactionResponse($"{prefix}-1", "2026-03-15", "OK", 10.50m, import.Id, 2), transactions!.Items.Single());
    }

    [Fact]
    public async Task Health_reports_ok_when_the_database_is_reachable()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }
}
