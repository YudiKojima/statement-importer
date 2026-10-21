using Npgsql;
using NpgsqlTypes;
using StatementImporter.Contracts.Csv;
using StatementImporter.Data;

namespace StatementImporter.Worker.Imports;

public sealed record AcceptedLine(RawLine Raw, ValidLine Line);

public sealed record RejectedLine(int LineNumber, string RawText, string Reason);

public static class ChunkWriter
{
    public static async Task<HashSet<int>> InsertTransactionsAsync(
        NpgsqlConnection connection, Guid importId, IReadOnlyCollection<AcceptedLine> lines, CancellationToken cancellationToken)
    {
        var inserted = new HashSet<int>();
        if (lines.Count == 0)
            return inserted;

        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE staging_transactions (
                transaction_id varchar(64), date date, description varchar(200), amount numeric(18,2), line_number int
            ) ON COMMIT DROP
            """, connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var importer = await connection.BeginBinaryImportAsync(
            "COPY staging_transactions (transaction_id, date, description, amount, line_number) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var line in lines)
            {
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(line.Line.TransactionId, NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(line.Line.Date, NpgsqlDbType.Date, cancellationToken);
                await importer.WriteAsync(line.Line.Description, NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(line.Line.Amount, NpgsqlDbType.Numeric, cancellationToken);
                await importer.WriteAsync(line.Raw.LineNumber, NpgsqlDbType.Integer, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO transactions (transaction_id, date, description, amount, import_id, line_number)
            SELECT transaction_id, date, description, amount, @import_id, line_number
            FROM staging_transactions
            ORDER BY transaction_id, line_number
            ON CONFLICT (transaction_id) DO NOTHING
            RETURNING line_number
            """, connection);
        insert.Parameters.AddWithValue("import_id", importId);

        await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            inserted.Add(reader.GetInt32(0));

        return inserted;
    }

    public static async Task InsertErrorsAsync(
        NpgsqlConnection connection, Guid importId, IEnumerable<RejectedLine> errors, CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY import_errors (import_id, line_number, raw_line, reason) FROM STDIN (FORMAT BINARY)", cancellationToken);

        foreach (var error in errors)
        {
            var raw = error.RawText.Length > StatementImporterDbContext.MaxRawLineLength
                ? error.RawText[..StatementImporterDbContext.MaxRawLineLength]
                : error.RawText;

            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(importId, NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync(error.LineNumber, NpgsqlDbType.Integer, cancellationToken);
            await importer.WriteAsync(raw, NpgsqlDbType.Varchar, cancellationToken);
            await importer.WriteAsync(error.Reason, NpgsqlDbType.Varchar, cancellationToken);
        }
        await importer.CompleteAsync(cancellationToken);
    }
}
