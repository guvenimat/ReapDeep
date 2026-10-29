using MySqlConnector;
using Npgsql;

namespace ReapDeep.PersonWorker;

public sealed class DeletionJob(NpgsqlDataSource db, IConfiguration cfg, ILogger<DeletionJob> log) : BackgroundService
{
    const int BatchSize = 1000;
    const int DeleteChunk = 500;

    readonly TimeSpan _interval = Seconds(cfg, "Deletion:IntervalSeconds", 86_400);
    readonly TimeSpan _minAge = Seconds(cfg, "Deletion:MinAgeSeconds", 600);
    readonly TimeSpan _tombstoneDelay = Seconds(cfg, "Deletion:TombstoneSeconds", 3_600);

    static TimeSpan Seconds(IConfiguration cfg, string key, int fallback) =>
        TimeSpan.FromSeconds(int.TryParse(cfg[key], out var s) && s > 0 ? s : fallback);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var starRocks = cfg.GetConnectionString("StarRocks");
        if (string.IsNullOrEmpty(starRocks))
        {
            log.LogWarning("ConnectionStrings:StarRocks is not set; deletion requests will not be processed");
            return;
        }
        await using var sr = new MySqlDataSource(starRocks);
        log.LogInformation("Deletion job: every {Interval}, requests older than {MinAge}", _interval, _minAge);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (await ProcessBatch(sr, ct) == BatchSize) { }
                await PurgeTombstones(sr, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Deletion job failed; retrying in the next run");
            }
            await Task.Delay(_interval, ct);
        }
    }

    sealed record Request(long Id, long ProjectId, string TargetType, string TargetId);

    async Task<int> ProcessBatch(MySqlDataSource sr, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var requests = new List<Request>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, project_id, target_type, target_id FROM deletion_requests
            WHERE processed_at IS NULL AND requested_at < now() - @age
            ORDER BY id LIMIT @n FOR UPDATE SKIP LOCKED
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("age", _minAge);
            cmd.Parameters.AddWithValue("n", BatchSize);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) requests.Add(new Request(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
        }
        if (requests.Count == 0) return 0;

        var eventDistinctIds = 0;
        var distinctIdCount = 0;
        var personCount = 0;
        foreach (var project in requests.GroupBy(r => r.ProjectId))
        {
            var p = new ProjectDeletion(conn, tx, project.Key, ct);
            foreach (var req in project) await p.Resolve(req.TargetType, req.TargetId);
            await p.Lock();
            await p.Resolve();

            foreach (var chunk in p.DistinctIds.Order(StringComparer.Ordinal).Chunk(DeleteChunk))
                await DeleteEvents(sr, project.Key, chunk, ct);
            eventDistinctIds += p.DistinctIds.Count;

            var (ids, persons) = await p.Apply();
            distinctIdCount += ids;
            personCount += persons;
        }

        await using (var cmd = new NpgsqlCommand("UPDATE deletion_requests SET processed_at = now() WHERE id = ANY(@ids)", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", requests.Select(r => r.Id).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        log.LogInformation("Processed {Requests} deletion requests: events of {EventDistinctIds} distinct_ids, {DistinctIds} distinct_id mappings and {Persons} persons deleted",
            requests.Count, eventDistinctIds, distinctIdCount, personCount);
        return requests.Count;
    }

    static async Task DeleteEvents(MySqlDataSource sr, long projectId, string[] distinctIds, CancellationToken ct)
    {
        await using var conn = await sr.OpenConnectionAsync(ct);
        var names = distinctIds.Select((_, i) => "@d" + i).ToArray();
        await using var cmd = new MySqlCommand(
            $"DELETE FROM analytics.events WHERE project_id = @p AND distinct_id IN ({string.Join(", ", names)})", conn) { CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("p", projectId);
        for (var i = 0; i < distinctIds.Length; i++) cmd.Parameters.AddWithValue(names[i], distinctIds[i]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    async Task PurgeTombstones(MySqlDataSource sr, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - _tombstoneDelay;
        await using var conn = await sr.OpenConnectionAsync(ct);
        foreach (var table in new[] { "person_distinct_ids", "persons" })
        {
            await using var cmd = new MySqlCommand(
                $"DELETE FROM analytics.{table} WHERE is_deleted = 1 AND updated_at < @cutoff", conn) { CommandTimeout = 600 };
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}

sealed class ProjectDeletion(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId, CancellationToken ct)
{
    readonly List<(string Type, string Id)> _targets = [];

    public HashSet<string> DistinctIds { get; } = new(StringComparer.Ordinal);
    HashSet<Guid> _persons = [];

    public async Task Resolve(string targetType, string targetId)
    {
        _targets.Add((targetType, targetId));
        await ResolveOne(targetType, targetId);
    }

    public async Task Resolve()
    {
        DistinctIds.Clear();
        _persons = [];
        foreach (var (type, id) in _targets) await ResolveOne(type, id);
    }

    async Task ResolveOne(string targetType, string targetId)
    {
        if (targetType == "person" && Guid.TryParse(targetId, out var personId))
        {
            await using var cmd = Cmd("SELECT id FROM persons WHERE project_id = @p AND id = @id", ("id", personId));
            if (await cmd.ExecuteScalarAsync(ct) is not null)
            {
                _persons.Add(personId);
                await using var ids = Cmd("SELECT distinct_id FROM person_distinct_ids WHERE project_id = @p AND person_id = @id", ("id", personId));
                await using var r = await ids.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) DistinctIds.Add(r.GetString(0));
                return;
            }
        }
        DistinctIds.Add(targetId);
    }

    public async Task Lock()
    {
        foreach (var d in DistinctIds.Order(StringComparer.Ordinal))
        {
            await using var cmd = Cmd("SELECT pg_advisory_xact_lock(hashtextextended(@p::text || ':' || @d, 0))", ("d", d));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<(int DistinctIds, int Persons)> Apply()
    {
        var dids = DistinctIds.ToArray();
        var affected = new HashSet<Guid>(_persons);
        var removedIds = 0;
        await using (var cmd = Cmd("""
            WITH gone AS (
                DELETE FROM person_distinct_ids WHERE project_id = @p AND distinct_id = ANY(@dids)
                RETURNING project_id, distinct_id, person_id, version
            ), outbox AS (
                INSERT INTO person_outbox (topic, key, payload)
                SELECT 'person_distinct_id_updates', distinct_id, jsonb_build_object(
                    'project_id', project_id, 'distinct_id', distinct_id, 'person_id', person_id::text,
                    'version', version + 1, 'is_deleted', true,
                    'updated_at', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
                FROM gone
            )
            SELECT person_id FROM gone
            """, ("dids", dids)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                affected.Add(r.GetGuid(0));
                removedIds++;
            }
        }

        await using var del = Cmd("""
            WITH gone AS (
                DELETE FROM persons p
                WHERE p.project_id = @p AND p.id = ANY(@ids)
                  AND (p.id = ANY(@requested) OR NOT EXISTS (SELECT 1 FROM person_distinct_ids d WHERE d.person_id = p.id))
                RETURNING p.*
            )
            INSERT INTO person_outbox (topic, key, payload)
            SELECT 'person_updates', id::text, jsonb_build_object(
                'project_id', project_id, 'person_id', id::text, 'properties', '{}'::jsonb,
                'is_identified', is_identified, 'is_deleted', true, 'version', version + 1,
                'created_at', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'),
                'updated_at', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
            FROM gone
            """, ("ids", affected.ToArray()), ("requested", _persons.ToArray()));
        var removedPersons = await del.ExecuteNonQueryAsync(ct);
        return (removedIds, removedPersons);
    }

    NpgsqlCommand Cmd(string sql, params (string Name, object Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("p", projectId);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
