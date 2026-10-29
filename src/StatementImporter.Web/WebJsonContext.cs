using System.Text.Json;
using System.Text.Json.Serialization;
using StatementImporter.Contracts.Http;

namespace StatementImporter.Web;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(IReadOnlyList<ImportResponse>))]
[JsonSerializable(typeof(PagedResponse<ImportErrorResponse>))]
[JsonSerializable(typeof(PagedResponse<TransactionResponse>))]
[JsonSerializable(typeof(ErrorResponse))]
internal partial class WebJsonContext : JsonSerializerContext;
