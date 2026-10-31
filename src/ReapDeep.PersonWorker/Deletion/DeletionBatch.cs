using Dapper;
using Npgsql;
using ReapDeep.PersonWorker.Identity;

namespace ReapDeep.PersonWorker.Deletion;

public sealed record DeletionRequest(long Id, long ProjectId, string TargetType, string TargetId);

public sealed record DeletionResult(int DistinctIds, int Persons, int StaticCohortMembers);

public sealed class DeletionBatch(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId, IReadOnlyList<DeletionRequest> requests)
{
    internal sealed record RemovedMappingRecord(Guid PersonId);

    public HashSet<string> DistinctIds { get; } = new(StringComparer.Ordinal);
    readonly HashSet<Guid> _persons = [];

    public async Task ResolveAndLockAsync()
    {
        await ResolveAsync();
        await PersonLocks.AcquireAsync(conn, tx, projectId, DistinctIds);
        await ResolveAsync();
    }

    public async Task<DeletionResult> ApplyAsync()
    {
        var removed = (await conn.QueryAsync<RemovedMappingRecord>("""
            WITH gone AS (
                DELETE FROM person_distinct_ids WHERE project_id = @ProjectId AND distinct_id = ANY(@DistinctIds)
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
            """, new { ProjectId = projectId, DistinctIds = DistinctIds.ToArray() }, tx)).AsList();

        var affected = new HashSet<Guid>(_persons);
        affected.UnionWith(removed.Select(r => r.PersonId));

        var persons = await conn.ExecuteAsync("""
            WITH gone AS (
                DELETE FROM persons p
                WHERE p.project_id = @ProjectId AND p.id = ANY(@Affected)
                  AND (p.id = ANY(@Requested) OR NOT EXISTS (SELECT 1 FROM person_distinct_ids d WHERE d.person_id = p.id))
                RETURNING p.*
            )
            INSERT INTO person_outbox (topic, key, payload)
            SELECT 'person_updates', id::text, jsonb_build_object(
                'project_id', project_id, 'person_id', id::text, 'properties', '{}'::jsonb,
                'is_identified', is_identified, 'is_deleted', true, 'version', version + 1,
                'created_at', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'),
                'updated_at', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
            FROM gone
            """, new { ProjectId = projectId, Affected = affected.ToArray(), Requested = _persons.ToArray() }, tx);

        var staticMembers = await conn.ExecuteAsync(
            "DELETE FROM cohort_static_members WHERE project_id = @ProjectId AND distinct_id = ANY(@DistinctIds)",
            new { ProjectId = projectId, DistinctIds = DistinctIds.ToArray() }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM person_item_history WHERE project_id = @ProjectId AND person_key = ANY(@Keys)",
            new { ProjectId = projectId, Keys = DistinctIds.Concat(affected.Select(p => p.ToString())).ToArray() }, tx);

        return new DeletionResult(removed.Count, persons, staticMembers);
    }

    async Task ResolveAsync()
    {
        DistinctIds.Clear();
        _persons.Clear();
        foreach (var request in requests)
        {
            if (request.TargetType == "person" && Guid.TryParse(request.TargetId, out var personId) && await PersonExistsAsync(personId))
            {
                _persons.Add(personId);
                DistinctIds.UnionWith(await conn.QueryAsync<string>(
                    "SELECT distinct_id FROM person_distinct_ids WHERE project_id = @ProjectId AND person_id = @PersonId",
                    new { ProjectId = projectId, PersonId = personId }, tx));
            }
            else
            {
                DistinctIds.Add(request.TargetId);
            }
        }
    }

    Task<bool> PersonExistsAsync(Guid personId) => conn.ExecuteScalarAsync<bool>(
        "SELECT EXISTS (SELECT 1 FROM persons WHERE project_id = @ProjectId AND id = @PersonId)",
        new { ProjectId = projectId, PersonId = personId }, tx);
}
