using Microsoft.Extensions.Options;
using StatementImporter.Worker.Imports;

namespace StatementImporter.Worker.Hosting;

public sealed class Reconciler(
    IServiceScopeFactory scopes,
    IOptions<ImportOptions> options,
    ILogger<Reconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ImportReconciler>().ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Falha no reconciliador; nova tentativa no próximo ciclo.");
            }
        }
    }
}
