using System.Text;
using Confluent.Kafka;
using Npgsql;
using ReapDeep.ServiceDefaults;

namespace ReapDeep.PersonWorker.Identity;

public sealed class EventConsumer(IConfiguration cfg, IdentityResolver resolver, DeadLetterProducer deadLetters, ILogger<EventConsumer> log) : BackgroundService
{
    const int MaxRetryDelaySeconds = 30;
    const long LagIntervalMs = 10_000;

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
        var lagDueAt = 0L;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Heartbeat.Beat();
                if (Environment.TickCount64 >= lagDueAt)
                {
                    lagDueAt = Environment.TickCount64 + LagIntervalMs;
                    IdentityMetrics.SetConsumerLag(IdentityMetrics.ConsumerLag(consumer));
                }
                if (consumer.Consume(TimeSpan.FromSeconds(1)) is not { } result) continue;
                Handle(result, ct);
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

    public enum Failure { Retry, DeadLetter, Stop }

    public static Failure Classify(Exception ex, bool cancellationRequested) => ex switch
    {
        _ when cancellationRequested => Failure.Stop,
        NpgsqlException { IsTransient: true } or TimeoutException or OperationCanceledException => Failure.Retry,
        _ => Failure.DeadLetter,
    };

    void Handle(ConsumeResult<Ignore, byte[]> result, CancellationToken ct)
    {
        var message = result.Message.Value;
        if (!IdentityEvent.MayBeRelevant(message)) return;
        if (IdentityEvent.Parse(message) is not { } e)
        {
            log.LogError("Unparseable event moved to the dead letter topic: {Value}", Encoding.UTF8.GetString(message));
            deadLetters.Send(result, "unparseable", "The event could not be parsed", ct);
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
            catch (Exception ex)
            {
                switch (Classify(ex, ct.IsCancellationRequested))
                {
                    case Failure.Stop:
                        ct.ThrowIfCancellationRequested();
                        throw;
                    case Failure.Retry:
                        var delay = TimeSpan.FromSeconds(Math.Min(MaxRetryDelaySeconds, Math.Pow(2, attempt)));
                        log.LogError(ex, "Failed to resolve identity ({Event} {DistinctId}), retrying in {Delay}s", e.Event, e.DistinctId, delay.TotalSeconds);
                        ct.WaitHandle.WaitOne(delay);
                        ct.ThrowIfCancellationRequested();
                        break;
                    default:
                        log.LogError(ex, "Failed to resolve identity, event moved to the dead letter topic: {Event} {DistinctId} anon={AnonId}", e.Event, e.DistinctId, e.AnonId);
                        deadLetters.Send(result, "identity", ex.Message, ct);
                        return;
                }
            }
        }
    }
}
