using Dapper;
using Npgsql;

namespace ReapDeep.PersonWorker.Identity;

public sealed record PersonRef(Guid Id, bool IsIdentified);

public static class PersonLocks
{
    public const string Sql = "SELECT pg_advisory_xact_lock(hashtextextended(@ProjectId::text || ':' || @DistinctId, 0))";

    public static async Task AcquireAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId, IEnumerable<string?> distinctIds)
    {
        foreach (var d in distinctIds.OfType<string>().Distinct().Order(StringComparer.Ordinal))
            await conn.ExecuteAsync(Sql, new { ProjectId = projectId, DistinctId = d }, tx);
    }
}

public sealed class PersonTransaction(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId)
{
    readonly HashSet<Guid> _changedPersons = [];
    readonly HashSet<string> _changedDistinctIds = [];

    public long ProjectId => projectId;

    public Task LockAsync(params string?[] distinctIds) => PersonLocks.AcquireAsync(conn, tx, projectId, distinctIds);

    public Task<PersonRef?> FindAsync(string distinctId) => conn.QuerySingleOrDefaultAsync<PersonRef>("""
        SELECT p.id, p.is_identified FROM person_distinct_ids d JOIN persons p ON p.id = d.person_id
        WHERE d.project_id = @ProjectId AND d.distinct_id = @DistinctId
        """, new { ProjectId = projectId, DistinctId = distinctId }, tx);

    public async Task<Guid> CreateAsync(bool identified, string? set)
    {
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO persons (id, project_id, properties, is_identified, version)
            VALUES (@Id, @ProjectId, COALESCE(@Set::jsonb, '{}'), @Identified, 1)
            """, new { Id = id, ProjectId = projectId, Set = set, Identified = identified }, tx);
        _changedPersons.Add(id);
        return id;
    }

    public async Task LinkAsync(string distinctId, Guid personId)
    {
        var changed = await conn.ExecuteAsync("""
            INSERT INTO person_distinct_ids (project_id, distinct_id, person_id, version)
            VALUES (@ProjectId, @DistinctId, @PersonId, (extract(epoch FROM clock_timestamp()) * 1000000)::bigint)
            ON CONFLICT (project_id, distinct_id) DO UPDATE
              SET person_id = EXCLUDED.person_id, version = person_distinct_ids.version + 1, updated_at = now()
              WHERE person_distinct_ids.person_id <> EXCLUDED.person_id
            """, new { ProjectId = projectId, DistinctId = distinctId, PersonId = personId }, tx);
        if (changed > 0) _changedDistinctIds.Add(distinctId);
    }

    public async Task UpdateAsync(Guid personId, string? set, bool identify)
    {
        if (set is null && !identify) return;
        var changed = await conn.ExecuteAsync("""
            UPDATE persons SET properties = properties || COALESCE(@Set::jsonb, '{}'),
                               is_identified = is_identified OR @Identify,
                               version = version + 1, updated_at = now()
            WHERE id = @Id AND (properties || COALESCE(@Set::jsonb, '{}') <> properties OR (@Identify AND NOT is_identified))
            """, new { Id = personId, Set = set, Identify = identify }, tx);
        if (changed > 0) _changedPersons.Add(personId);
    }

    public async Task MergeAsync(Guid from, Guid into, string? set)
    {
        var moved = await conn.QueryAsync<string>("""
            UPDATE person_distinct_ids SET person_id = @Into, version = version + 1, updated_at = now()
            WHERE person_id = @From RETURNING distinct_id
            """, new { From = from, Into = into }, tx);
        _changedDistinctIds.UnionWith(moved);

        await conn.ExecuteAsync("""
            UPDATE persons SET properties = (SELECT properties FROM persons WHERE id = @From) || properties || COALESCE(@Set::jsonb, '{}'),
                               is_identified = true, version = version + 1, updated_at = now()
            WHERE id = @Into;
            UPDATE persons SET is_deleted = true, properties = '{}', version = version + 1, updated_at = now() WHERE id = @From;
            """, new { From = from, Into = into, Set = set }, tx);
        _changedPersons.Add(from);
        _changedPersons.Add(into);
    }

    public async Task WriteOutboxAsync()
    {
        if (_changedPersons.Count > 0)
            await conn.ExecuteAsync("""
                INSERT INTO person_outbox (topic, key, payload)
                SELECT 'person_updates', id::text, jsonb_build_object(
                    'project_id', project_id, 'person_id', id::text, 'properties', properties,
                    'is_identified', is_identified, 'is_deleted', is_deleted, 'version', version,
                    'created_at', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'),
                    'updated_at', to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
                FROM persons WHERE id = ANY(@Ids)
                """, new { Ids = _changedPersons.ToArray() }, tx);

        if (_changedDistinctIds.Count > 0)
            await conn.ExecuteAsync("""
                INSERT INTO person_outbox (topic, key, payload)
                SELECT 'person_distinct_id_updates', distinct_id, jsonb_build_object(
                    'project_id', project_id, 'distinct_id', distinct_id, 'person_id', person_id::text,
                    'version', version, 'is_deleted', false,
                    'updated_at', to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
                FROM person_distinct_ids WHERE project_id = @ProjectId AND distinct_id = ANY(@DistinctIds)
                """, new { ProjectId = projectId, DistinctIds = _changedDistinctIds.ToArray() }, tx);
    }
}
