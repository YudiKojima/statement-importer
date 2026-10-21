using RabbitMQ.Client.Exceptions;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Http;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.Api.Imports;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var imports = app.MapGroup("/api/imports");

        imports.MapPost("", UploadAsync)
            .Produces<ImportResponse>(StatusCodes.Status202Accepted)
            .Produces<ImportResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        imports.MapGet("", async (ImportRepository repository) => Results.Ok(await repository.ListRecentAsync()))
            .Produces<IReadOnlyList<ImportResponse>>();

        imports.MapGet("{id:guid}", async (Guid id, ImportRepository repository) =>
                await repository.GetAsync(id) is { } import ? Results.Ok(import) : Results.NotFound())
            .Produces<ImportResponse>()
            .Produces(StatusCodes.Status404NotFound);

        imports.MapGet("{id:guid}/errors", async (Guid id, int? page, int? pageSize, ImportRepository repository) =>
                await repository.GetAsync(id) is null
                    ? Results.NotFound()
                    : Results.Ok(await repository.GetErrorsAsync(id, Paging.From(page, pageSize))))
            .Produces<PagedResponse<ImportErrorResponse>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/transactions", async (Guid? importId, int? page, int? pageSize, ImportRepository repository) =>
                Results.Ok(await repository.GetTransactionsAsync(importId, Paging.From(page, pageSize))))
            .Produces<PagedResponse<TransactionResponse>>();
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        string? fileName,
        UploadStorage storage,
        ImportRepository repository,
        MessagePublisher publisher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
            return Results.BadRequest(new ErrorResponse("Informe fileName na query string (até 255 caracteres)."));

        var id = Guid.CreateVersion7();
        StoredUpload? upload;
        try
        {
            upload = await storage.SaveAsync(id, request.Body, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (upload is null)
            return Results.BadRequest(new ErrorResponse($"Cabeçalho inválido. Esperado: {CsvFormat.Header}"));

        if (!await repository.TryInsertAsync(id, fileName, upload.Hash, upload.Path))
        {
            storage.Delete(upload.Path);
            return Results.Ok(await repository.GetByHashAsync(upload.Hash));
        }

        try
        {
            await publisher.PublishAsync(QueueTopology.ImportRequested, new ImportRequested(id),
                ContractsJsonContext.Default.ImportRequested, cancellationToken);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or PublishException)
        {
            loggerFactory.CreateLogger(nameof(ImportEndpoints)).LogWarning(ex,
                "Fila indisponível; a importação {ImportId} ficou em queued e será republicada pelo reconciliador.", id);
        }

        return Results.Accepted($"/api/imports/{id}", await repository.GetAsync(id));
    }
}
