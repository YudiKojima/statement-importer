using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Worker.Imports;

namespace StatementImporter.Worker.Hosting;

public sealed class ConsumerHost(
    IConnection connection,
    MessagePublisher publisher,
    IServiceScopeFactory scopes,
    IOptions<ImportOptions> options,
    ILogger<ConsumerHost> logger) : BackgroundService
{
    private readonly List<IChannel> _channels = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartConsumerAsync(QueueTopology.ImportRequested, ContractsJsonContext.Default.ImportRequested,
            (services, message, _, ct) => services.GetRequiredService<ImportRequestedHandler>().HandleAsync(message, ct),
            stoppingToken);
        await StartConsumerAsync(QueueTopology.ProcessChunk, ContractsJsonContext.Default.ProcessChunk,
            (services, message, redelivered, ct) => services.GetRequiredService<ProcessChunkHandler>().HandleAsync(message, redelivered, ct),
            stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!connection.IsOpen || !publisher.IsOpen || _channels.Any(c => !c.IsOpen))
            {
                Environment.ExitCode = 1;
                throw new InvalidOperationException("Conexão ou canal do RabbitMQ fechado; encerrando para o restart policy religar o worker.");
            }
            Heartbeat.Beat();
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task StartConsumerAsync<T>(
        string queue,
        JsonTypeInfo<T> typeInfo,
        Func<IServiceProvider, T, bool, CancellationToken, Task> handle,
        CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        _channels.Add(channel);
        await QueueTopology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize(delivery.Body.Span, typeInfo)
                    ?? throw new InvalidOperationException($"Mensagem vazia na fila {queue}.");
                await using var scope = scopes.CreateAsyncScope();
                await handle(scope.ServiceProvider, message, delivery.Redelivered, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Falha ao processar mensagem da fila {Queue}; devolvendo para a fila em {Delay}.",
                    queue, options.Value.NackDelay);
                await Task.Delay(options.Value.NackDelay, stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
            }
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
    }
}
