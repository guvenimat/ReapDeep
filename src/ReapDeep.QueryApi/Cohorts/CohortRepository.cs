using System.Data;
using Dapper;
using Npgsql;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Cohorts;

public sealed class CohortRepository(NpgsqlDataSource db)
{
    internal sealed record CohortRecord(
        long Id, string Name, string Description, string Kind, string Definition, string Status, long? ActiveVersion,
        long? MemberCount, int? StaticMembers, DateTime? CalculatedAt, int? CalculationMs, string? LastError,
        string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public CohortRow ToContract() => new(Id, Name, Description, Kind,
            Kind == CohortKinds.Dynamic ? JsonText.Parse(Definition) : null,
            Status, MemberCount, StaticMembers, CalculatedAt, CalculationMs, LastError, CreatedBy, CreatedAt, UpdatedAt);
    }

    internal sealed record CohortRefRecord(long Id, string Name, string Status, long? ActiveVersion);
    internal sealed record UsageRecord(long Id, string Name, string Kind);

    const string Select = """
        SELECT c.id, c.name, c.description, c.kind, c.definition::text AS definition, c.status, c.active_version, c.member_count,
               CASE WHEN c.kind = 'static' THEN (SELECT count(*)::int FROM cohort_static_members s WHERE s.cohort_id = c.id) END AS static_members,
               c.calculated_at, c.calculation_ms, c.last_error, u.email AS created_by, c.created_at, c.updated_at
        FROM cohorts c LEFT JOIN users u ON u.id = c.created_by
        WHERE c.project_id = @ProjectId AND c.deleted_at IS NULL
        """;

    public async Task<List<CohortRow>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CohortRecord>(Select + " ORDER BY c.updated_at DESC LIMIT 500", new { ProjectId = projectId });
        return rows.Select(r => r.ToContract()).ToList();
    }

    public async Task<(CohortRow Row, long? ActiveVersion)?> GetAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, projectId, id);
    }

    public async Task<CohortRow> CreateAsync(long projectId, string name, string description, string kind, string definition,
        IReadOnlyList<string> members, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO cohorts (project_id, name, description, kind, definition, created_by)
            VALUES (@ProjectId, @Name, @Description, @Kind, @Definition::jsonb, @CreatedBy) RETURNING id
            """, new { ProjectId = projectId, Name = name, Description = description, Kind = kind, Definition = definition, CreatedBy = createdBy }, tx);
        if (kind == CohortKinds.Static) await InsertMembersAsync(conn, tx, projectId, id, members);
        await tx.CommitAsync(ct);
        return (await GetAsync(conn, projectId, id))!.Value.Row;
    }

    public async Task<CohortRow?> UpdateAsync(long projectId, long id, string? name, string? description, string? definition, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE cohorts SET name = COALESCE(@Name::text, name), description = COALESCE(@Description::text, description),
                definition = COALESCE(@Definition::jsonb, definition), updated_at = now(),
                calculated_at = CASE WHEN @Definition::text IS NULL THEN calculated_at END
            WHERE id = @Id AND project_id = @ProjectId AND deleted_at IS NULL
            """, new { Name = name, Description = description, Definition = definition, Id = id, ProjectId = projectId });
        return updated == 0 ? null : (await GetAsync(conn, projectId, id))?.Row;
    }

    public async Task<bool> ReplaceMembersAsync(long projectId, long id, IReadOnlyList<string> members, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE cohorts SET updated_at = now(), calculated_at = NULL
            WHERE id = @Id AND project_id = @ProjectId AND kind = 'static' AND deleted_at IS NULL
            """, new { Id = id, ProjectId = projectId }, tx);
        if (updated == 0) return false;
        await conn.ExecuteAsync("DELETE FROM cohort_static_members WHERE cohort_id = @Id", new { Id = id }, tx);
        await InsertMembersAsync(conn, tx, projectId, id, members);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RecalculateAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "UPDATE cohorts SET updated_at = now(), calculated_at = NULL WHERE id = @Id AND project_id = @ProjectId AND deleted_at IS NULL",
            new { Id = id, ProjectId = projectId }) > 0;
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "UPDATE cohorts SET deleted_at = now() WHERE id = @Id AND project_id = @ProjectId AND deleted_at IS NULL",
            new { Id = id, ProjectId = projectId }) > 0;
    }

    public async Task<List<CohortUsage>> UsageAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<UsageRecord>("""
            SELECT id, name, kind FROM insights
            WHERE project_id = @ProjectId
              AND jsonb_path_exists(query, '$.** ? (@.scope == "cohort" && (@.property == $sid || @.cohort_ids[*] == $nid))',
                                    jsonb_build_object('sid', @Id::text, 'nid', @Id))
            ORDER BY name
            """, new { ProjectId = projectId, Id = id });
        return rows.Select(r => new CohortUsage(r.Id, r.Name, r.Kind)).ToList();
    }

    public async Task<List<CohortRef>> ReferencesAsync(long projectId, long[] ids, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CohortRefRecord>(
            "SELECT id, name, status, active_version FROM cohorts WHERE project_id = @ProjectId AND id = ANY(@Ids) AND deleted_at IS NULL",
            new { ProjectId = projectId, Ids = ids });
        return rows.Select(r => r.ActiveVersion is { } v
            ? new CohortRef(r.Id, r.Name, v)
            : throw new CohortNotReadyException(r.Id, r.Name, r.Status)).ToList();
    }

    public async Task<List<string>> ResolveDistinctIdsAsync(long projectId, string[] ids, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<string>("""
            SELECT d.distinct_id FROM unnest(@Ids) AS x(id)
            JOIN persons p ON p.project_id = @ProjectId AND p.id::text = x.id
            JOIN person_distinct_ids d ON d.person_id = p.id
            UNION
            SELECT x.id FROM unnest(@Ids) AS x(id)
            WHERE NOT EXISTS (SELECT 1 FROM persons p WHERE p.project_id = @ProjectId AND p.id::text = x.id)
            """, new { ProjectId = projectId, Ids = ids });
        return rows.AsList();
    }

    static Task InsertMembersAsync(IDbConnection conn, IDbTransaction tx, long projectId, long id, IReadOnlyList<string> members) =>
        conn.ExecuteAsync("""
            INSERT INTO cohort_static_members (cohort_id, project_id, distinct_id)
            SELECT @Id, @ProjectId, m FROM unnest(@Members) AS m ON CONFLICT DO NOTHING
            """, new { Id = id, ProjectId = projectId, Members = members.ToArray() }, tx);

    static async Task<(CohortRow Row, long? ActiveVersion)?> GetAsync(IDbConnection conn, long projectId, long id) =>
        await conn.QuerySingleOrDefaultAsync<CohortRecord>(Select + " AND c.id = @Id", new { ProjectId = projectId, Id = id }) is { } r
            ? (r.ToContract(), r.ActiveVersion)
            : null;
}

public sealed class CohortNotReadyException(long id, string name, string status)
    : Exception(status == "error" ? $"Cohort '{name}' ({id}) failed to calculate" : $"Cohort '{name}' ({id}) is still being calculated");

public sealed class CohortNotFoundException(long id) : Exception($"Cohort {id} not found");
