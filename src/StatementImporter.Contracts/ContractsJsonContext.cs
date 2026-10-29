using System.Text.Json;
using System.Text.Json.Serialization;
using StatementImporter.Contracts.Http;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.Contracts;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ImportRequested))]
[JsonSerializable(typeof(ProcessChunk))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(IReadOnlyList<ImportResponse>))]
[JsonSerializable(typeof(PagedResponse<ImportErrorResponse>))]
[JsonSerializable(typeof(PagedResponse<TransactionResponse>))]
[JsonSerializable(typeof(ErrorResponse))]
// Parâmetros de query (importId, page, pageSize): o gerador de OpenAPI sob AOT pede os metadados deles.
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(int?))]
public partial class ContractsJsonContext : JsonSerializerContext;
