using System.Diagnostics.Metrics;
using Confluent.Kafka;

namespace ReapDeep.PersonWorker.Identity;

public static class IdentityMetrics
{
    static readonly Meter Meter = new("ReapDeep.PersonWorker");
    static long _consumerLag = -1;
    static long _outboxPending = -1;
    static double _outboxOldestSeconds;

    static IdentityMetrics()
    {
        Meter.CreateObservableGauge("reapdeep.identity.consumer_lag", () => Observe(Volatile.Read(ref _consumerLag)), "{message}",
            "Messages on the events topic the person worker has not processed yet");
        Meter.CreateObservableGauge("reapdeep.outbox.pending", () => Observe(Volatile.Read(ref _outboxPending)), "{row}",
            "Person and distinct id updates waiting in the outbox for Kafka");
        Meter.CreateObservableGauge("reapdeep.outbox.oldest_age", () => Volatile.Read(ref _outboxPending) > 0 ? Volatile.Read(ref _outboxOldestSeconds) : 0,
            "s", "Age of the oldest update waiting in the outbox");
    }

    static IEnumerable<Measurement<long>> Observe(long value) => value < 0 ? [] : [new Measurement<long>(value)];

    public static void SetConsumerLag(long lag) => Volatile.Write(ref _consumerLag, lag);

    public static void SetOutbox(long pending, double oldestSeconds)
    {
        Volatile.Write(ref _outboxOldestSeconds, oldestSeconds);
        Volatile.Write(ref _outboxPending, pending);
    }

    public static long Lag(IEnumerable<(long High, long Position)> partitions) =>
        partitions.Where(p => p.High >= 0 && p.Position >= 0).Sum(p => Math.Max(0, p.High - p.Position));

    public static long ConsumerLag<TKey, TValue>(IConsumer<TKey, TValue> consumer) =>
        Lag(consumer.Assignment.Select(tp => (consumer.GetWatermarkOffsets(tp).High.Value, consumer.Position(tp).Value)));
}
