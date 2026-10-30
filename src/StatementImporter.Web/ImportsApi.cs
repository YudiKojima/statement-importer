using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StatementImporter.Contracts.Http;

namespace StatementImporter.Web;

public sealed class ImportsApi(HttpClient http)
{
    public const int PageSize = 50;

    public async Task<IReadOnlyList<ImportResponse>> ListAsync() =>
        await http.GetFromJsonAsync("api/imports", WebJsonContext.Default.IReadOnlyListImportResponse) ?? [];

    public async Task<ImportResponse?> GetAsync(Guid id)
    {
        using var response = await http.GetAsync($"api/imports/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(WebJsonContext.Default.ImportResponse);
    }

    public Task<PagedResponse<ImportErrorResponse>?> GetErrorsAsync(Guid id, int page) =>
        http.GetFromJsonAsync($"api/imports/{id}/errors?page={page}&pageSize={PageSize}",
            WebJsonContext.Default.PagedResponseImportErrorResponse);

    public Task<PagedResponse<TransactionResponse>?> GetTransactionsAsync(Guid importId, int page) =>
        http.GetFromJsonAsync($"api/transactions?importId={importId}&page={page}&pageSize={PageSize}",
            WebJsonContext.Default.PagedResponseTransactionResponse);

    public static ImportResponse? ParseImport(string json) =>
        JsonSerializer.Deserialize(json, WebJsonContext.Default.ImportResponse);

    public static string? ParseError(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, WebJsonContext.Default.ErrorResponse)?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
