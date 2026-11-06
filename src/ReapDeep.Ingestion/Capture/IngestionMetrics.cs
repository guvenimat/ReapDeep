using System.Diagnostics.Metrics;

namespace ReapDeep.Ingestion.Capture;

public static class IngestionMetrics
{
    static readonly Meter Meter = new("ReapDeep");
    public static readonly Counter<long> Accepted = Meter.CreateCounter<long>("reapdeep.ingestion.events.accepted", "{event}", "Events written to Kafka");
    public static readonly Counter<long> Rejected = Meter.CreateCounter<long>("reapdeep.ingestion.events.rejected", "{event}", "Events dropped as invalid");
    public static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("reapdeep.ingestion.events.duplicates", "{event}", "Retried events dropped as duplicates");
    public static readonly Counter<long> Throttled = Meter.CreateCounter<long>("reapdeep.ingestion.events.throttled", "{event}", "Events refused with 429 because the project was over its ingestion quota");
    public static readonly Counter<long> KafkaFailures = Meter.CreateCounter<long>("reapdeep.ingestion.kafka.failures", "{batch}", "Batches answered with 503 because Kafka did not acknowledge them");
}
