using Confluent.Kafka;

namespace ReapDeep.Ingestion.Kafka;

public sealed class EventProducer : IDisposable
{
    static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    readonly IProducer<Null, byte[]> _producer;
    readonly string _topic;

    public EventProducer(IConfiguration cfg, ILogger<EventProducer> log)
    {
        _topic = cfg["Kafka:Topic"] ?? "events";
        _producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig
        {
            BootstrapServers = cfg["Kafka:BootstrapServers"],
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = 5,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = 10_000,
        })
        .SetErrorHandler((_, e) => log.LogWarning("Kafka: {Reason}", e.Reason))
        .Build();
    }

    public Task ProduceAsync(byte[] value, CancellationToken ct) =>
        _producer.ProduceAsync(_topic, new Message<Null, byte[]> { Value = value }, ct);

    public void Flush() => _producer.Flush(FlushTimeout);

    public void Dispose() => _producer.Dispose();

    public static bool IsDeliveryFailure(Exception ex) => ex is ProduceException<Null, byte[]> or KafkaException;
}
