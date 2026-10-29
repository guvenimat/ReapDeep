using Confluent.Kafka;
using Dapper;
using Npgsql;

namespace ReapDeep.PersonWorker.Outbox;

public sealed class OutboxPublisher(NpgsqlDataSource db, IConfiguration cfg, ILogger<OutboxPublisher> log) : BackgroundService
{
    const int BatchSize = 500;
    static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(2);

    internal sealed record OutboxRecord(long Id, string Topic, string Key, string Payload);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = cfg["Kafka:BootstrapServers"],
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = 5,
            MessageTimeoutMs = 10_000,
        })
        .SetErrorHandler((_, e) => log.LogWarning("Kafka: {Reason}", e.Reason))
        .Build();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await PublishBatchAsync(producer, ct) == 0) await Task.Delay(IdleDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to publish outbox, retrying");
                await Task.Delay(ErrorDelay, ct);
            }
        }
    }

    async Task<int> PublishBatchAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = (await conn.QueryAsync<OutboxRecord>(
            "SELECT id, topic, key, payload::text AS payload FROM person_outbox ORDER BY id LIMIT @Limit FOR UPDATE SKIP LOCKED",
            new { Limit = BatchSize }, tx)).AsList();
        if (rows.Count == 0) return 0;

        await Task.WhenAll(rows.Select(r => producer.ProduceAsync(r.Topic, new Message<string, string> { Key = r.Key, Value = r.Payload }, ct)));
        await conn.ExecuteAsync("DELETE FROM person_outbox WHERE id = ANY(@Ids)", new { Ids = rows.Select(r => r.Id).ToArray() }, tx);
        await tx.CommitAsync(ct);
        return rows.Count;
    }
}
