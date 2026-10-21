namespace StatementImporter.Api;

public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var response = await http.GetAsync("http://localhost:8080/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
