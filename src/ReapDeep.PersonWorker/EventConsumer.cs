using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace ReapDeep.PersonWorker;

public sealed class EventConsumer(IConfiguration cfg, IdentityResolver resolver, ILogger<EventConsumer> log) : BackgroundService
{
    static readonly byte[] IdentifyToken = "\"$identify\""u8.ToArray();
    static readonly byte[] SetToken = "\"$set\""u8.ToArray();

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    void Run(CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = cfg["Kafka:BootstrapServers"],
            GroupId = cfg["Kafka:GroupId"] ?? "person-worker",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
        })
        .SetErrorHandler((_, e) => log.LogWarning("Kafka: {Reason}", e.Reason))
        .Build();

        consumer.Subscribe(cfg["Kafka:EventsTopic"] ?? "events");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var cr = consumer.Consume(ct);
                Handle(cr.Message.Value, ct);
                consumer.StoreOffset(cr);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            consumer.Close();
        }
    }

    void Handle(byte[] value, CancellationToken ct)
    {
        var span = value.AsSpan();
        if (span.IndexOf(IdentifyToken) < 0 && span.IndexOf(SetToken) < 0) return;

        if (!TryParse(value, out var e))
        {
            log.LogError("Skipped unparseable event: {Value}", Encoding.UTF8.GetString(value));
            return;
        }
        if (e.Event != "$identify" && e.Set is null) return;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                resolver.ProcessAsync(e.ProjectId, e.Event, e.DistinctId, e.AnonId, e.Set, ct).GetAwaiter().GetResult();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException { IsTransient: true } or TimeoutException && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                log.LogError(ex, "Failed to resolve identity ({Event} {DistinctId}), retrying in {Delay}s", e.Event, e.DistinctId, delay.TotalSeconds);
                ct.WaitHandle.WaitOne(delay);
                ct.ThrowIfCancellationRequested();
            }
            catch (NpgsqlException ex)
            {
                log.LogError(ex, "Failed to resolve identity, event skipped: {Event} {DistinctId} anon={AnonId}", e.Event, e.DistinctId, e.AnonId);
                return;
            }
        }
    }

    readonly record struct Parsed(long ProjectId, string Event, string DistinctId, string? AnonId, string? Set);

    static bool TryParse(byte[] value, out Parsed parsed)
    {
        parsed = default;
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            string? anon = null, set = null;
            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("$anon_distinct_id", out var a) && a.ValueKind == JsonValueKind.String) anon = a.GetString();
                if (props.TryGetProperty("$set", out var s) && s.ValueKind == JsonValueKind.Object) set = s.GetRawText();
            }
            var eventName = root.GetProperty("event").GetString();
            var distinctId = root.GetProperty("distinct_id").GetString();
            if (string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(distinctId)) return false;

            parsed = new Parsed(root.GetProperty("project_id").GetInt64(), eventName, distinctId, string.IsNullOrEmpty(anon) ? null : anon, set);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
