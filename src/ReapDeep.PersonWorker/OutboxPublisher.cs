using Confluent.Kafka;
using Npgsql;

namespace ReapDeep.PersonWorker;

public sealed class OutboxPublisher(NpgsqlDataSource db, IConfiguration cfg, ILogger<OutboxPublisher> log) : BackgroundService
{
    const int BatchSize = 500;
    static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(200);

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
                if (await PublishBatch(producer, ct) == 0) await Task.Delay(IdleDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to publish outbox, retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    async Task<int> PublishBatch(IProducer<string, string> producer, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ids = new List<long>(BatchSize);
        var deliveries = new List<Task<DeliveryResult<string, string>>>(BatchSize);
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, topic, key, payload::text FROM person_outbox ORDER BY id LIMIT @n FOR UPDATE SKIP LOCKED", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", BatchSize);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetInt64(0));
                deliveries.Add(producer.ProduceAsync(r.GetString(1), new Message<string, string> { Key = r.GetString(2), Value = r.GetString(3) }, ct));
            }
        }
        if (ids.Count == 0) return 0;

        await Task.WhenAll(deliveries);

        await using (var del = new NpgsqlCommand("DELETE FROM person_outbox WHERE id = ANY(@ids)", conn, tx))
        {
            del.Parameters.AddWithValue("ids", ids.ToArray());
            await del.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return ids.Count;
    }
}
