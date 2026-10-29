using System.Text;
using Confluent.Kafka;
using Npgsql;

namespace ReapDeep.PersonWorker.Identity;

public sealed class EventConsumer(IConfiguration cfg, IdentityResolver resolver, ILogger<EventConsumer> log) : BackgroundService
{
    const int MaxRetryDelaySeconds = 30;

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
                var result = consumer.Consume(ct);
                Handle(result.Message.Value, ct);
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    void Handle(byte[] message, CancellationToken ct)
    {
        if (!IdentityEvent.MayBeRelevant(message)) return;
        if (IdentityEvent.Parse(message) is not { } e)
        {
            log.LogError("Skipped unparseable event: {Value}", Encoding.UTF8.GetString(message));
            return;
        }
        if (!e.HasWork) return;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                resolver.ProcessAsync(e, ct).GetAwaiter().GetResult();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException { IsTransient: true } or TimeoutException && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(MaxRetryDelaySeconds, Math.Pow(2, attempt)));
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
}
