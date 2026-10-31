using System.Diagnostics.Metrics;
using System.Text;
using Confluent.Kafka;

namespace ReapDeep.PersonWorker.Identity;

public sealed class DeadLetterProducer : IDisposable
{
    public const string StageHeader = "reapdeep-stage";
    public const string ErrorHeader = "reapdeep-error";
    public const string SourceHeader = "reapdeep-source";
    const int MaxErrorLength = 1000;

    static readonly Meter Meter = new("ReapDeep.PersonWorker");
    static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("reapdeep.events.dead_lettered", "{event}", "Events moved to the dead letter topic, by stage");

    readonly IProducer<Null, byte[]> _producer;
    readonly string _topic;
    readonly ILogger<DeadLetterProducer> _log;

    public DeadLetterProducer(IConfiguration cfg, ILogger<DeadLetterProducer> log)
    {
        _log = log;
        _topic = cfg["Kafka:DeadLetterTopic"] ?? "events_dead_letter";
        _producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig
        {
            BootstrapServers = cfg["Kafka:BootstrapServers"],
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10_000,
        })
        .SetErrorHandler((_, e) => log.LogWarning("Kafka: {Reason}", e.Reason))
        .Build();
    }

    public static Headers HeadersFor(string stage, string error, TopicPartitionOffset source) => new()
    {
        { StageHeader, Encoding.UTF8.GetBytes(stage) },
        { ErrorHeader, Encoding.UTF8.GetBytes(error.Length <= MaxErrorLength ? error : error[..MaxErrorLength]) },
        { SourceHeader, Encoding.UTF8.GetBytes($"{source.Topic}/{source.Partition.Value}/{source.Offset.Value}") },
    };

    public void Send(ConsumeResult<Ignore, byte[]> result, string stage, string error, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _producer.ProduceAsync(_topic, new Message<Null, byte[]> { Value = result.Message.Value, Headers = HeadersFor(stage, error, result.TopicPartitionOffset) }, ct)
                    .GetAwaiter().GetResult();
                DeadLettered.Add(1, new KeyValuePair<string, object?>("stage", stage));
                return;
            }
            catch (Exception ex) when (ex is ProduceException<Null, byte[]> or KafkaException && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                _log.LogError(ex, "Could not write to the dead letter topic, retrying in {Delay}s", delay.TotalSeconds);
                ct.WaitHandle.WaitOne(delay);
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
