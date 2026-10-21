using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace StatementImporter.Contracts.Messaging;

public sealed class MessagePublisher : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private MessagePublisher(IChannel channel) => _channel = channel;

    public bool IsOpen => _channel.IsOpen;

    public event AsyncEventHandler<ShutdownEventArgs> ShutdownAsync
    {
        add => _channel.ChannelShutdownAsync += value;
        remove => _channel.ChannelShutdownAsync -= value;
    }

    public static async Task<MessagePublisher> CreateAsync(IConnection connection, CancellationToken cancellationToken = default)
    {
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await QueueTopology.DeclareAsync(channel, cancellationToken);
        return new MessagePublisher(channel);
    }

    public async Task PublishAsync<T>(string queue, T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        var properties = new BasicProperties { Persistent = true, ContentType = "application/json" };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await _channel.BasicPublishAsync(exchange: "", routingKey: queue, mandatory: true,
                basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        _lock.Dispose();
    }
}
