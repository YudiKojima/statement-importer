using Dapper;
using Npgsql;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Http;

namespace StatementImporter.Api.Imports;

public sealed class ImportRepository(NpgsqlDataSource dataSource)
{
    private const string SelectImport = """
        SELECT id AS Id, file_name AS FileName, status AS Status, total_lines AS TotalLines,
               processed_lines AS ProcessedLines, valid_count AS ValidCount, error_count AS ErrorCount,
               failure_reason AS FailureReason, created_at AS CreatedAt, started_at AS StartedAt,
               completed_at AS CompletedAt
        FROM imports
        """;

    public async Task<bool> TryInsertAsync(Guid id, string fileName, string fileHash, string filePath)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var rows = await connection.ExecuteAsync("""
            INSERT INTO imports (id, file_name, file_hash, file_path, status, total_lines, total_chunks,
                                 processed_chunks, processed_lines, valid_count, error_count, created_at)
            VALUES (@id, @fileName, @fileHash, @filePath, @status, 0, 0, 0, 0, 0, 0, now())
            ON CONFLICT (file_hash) DO NOTHING
            """, new { id, fileName, fileHash, filePath, status = ImportStatus.Queued });
        return rows == 1;
    }

    public async Task<ImportResponse?> GetAsync(Guid id)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ImportResponse>(SelectImport + " WHERE id = @id", new { id });
    }

    public async Task<ImportResponse?> GetByHashAsync(string fileHash)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ImportResponse>(SelectImport + " WHERE file_hash = @fileHash", new { fileHash });
    }

    public async Task<IReadOnlyList<ImportResponse>> ListRecentAsync()
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<ImportResponse>(SelectImport + " ORDER BY created_at DESC LIMIT 20");
        return rows.AsList();
    }

    public async Task<PagedResponse<ImportErrorResponse>> GetErrorsAsync(Guid importId, Paging paging)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var items = await connection.QueryAsync<ImportErrorResponse>("""
            SELECT line_number AS LineNumber, raw_line AS RawLine, reason AS Reason
            FROM import_errors WHERE import_id = @importId
            ORDER BY line_number LIMIT @limit OFFSET @offset
            """, new { importId, limit = paging.PageSize, offset = paging.Offset });
        var total = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM import_errors WHERE import_id = @importId", new { importId });
        return new PagedResponse<ImportErrorResponse>(items.AsList(), paging.Page, paging.PageSize, total);
    }

    public async Task<PagedResponse<TransactionResponse>> GetTransactionsAsync(Guid? importId, Paging paging)
    {
        const string columns = """
            SELECT transaction_id AS TransactionId, to_char(date, 'YYYY-MM-DD') AS Date, description AS Description,
                   amount AS Amount, import_id AS ImportId, line_number AS LineNumber
            FROM transactions
            """;
        await using var connection = await dataSource.OpenConnectionAsync();

        IEnumerable<TransactionResponse> items;
        long total;
        if (importId is { } id)
        {
            items = await connection.QueryAsync<TransactionResponse>(
                columns + " WHERE import_id = @id ORDER BY line_number LIMIT @limit OFFSET @offset",
                new { id, limit = paging.PageSize, offset = paging.Offset });
            total = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM transactions WHERE import_id = @id", new { id });
        }
        else
        {
            items = await connection.QueryAsync<TransactionResponse>(
                columns + " ORDER BY id LIMIT @limit OFFSET @offset",
                new { limit = paging.PageSize, offset = paging.Offset });
            total = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM transactions");
        }
        return new PagedResponse<TransactionResponse>(items.AsList(), paging.Page, paging.PageSize, total);
    }
}
