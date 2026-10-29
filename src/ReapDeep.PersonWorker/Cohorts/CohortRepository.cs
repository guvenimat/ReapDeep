using Dapper;
using Npgsql;

namespace ReapDeep.PersonWorker.Cohorts;

public sealed class CohortRepository(NpgsqlDataSource db)
{
    const int LockClass = 0x434F48;

    public sealed record DueCohortRecord(long Id, long ProjectId, string Kind, string Definition, long? ActiveVersion, DateTime StartedAt);
    public sealed record RetiredVersionRecord(long CohortId, long ProjectId, long Version);
    public sealed record DeletedCohortRecord(long Id, long ProjectId);

    public async Task<List<DueCohortRecord>> DueAsync(TimeSpan maxInterval, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DueCohortRecord>("""
            SELECT id, project_id, kind, definition::text AS definition, active_version, now() AS started_at FROM cohorts
            WHERE deleted_at IS NULL
              AND (calculated_at IS NULL OR calculated_at < now() - make_interval(secs => LEAST(refresh_interval_seconds, @MaxSeconds)))
            ORDER BY calculated_at NULLS FIRST, id
            LIMIT @Limit
            """, new { MaxSeconds = (int)maxInterval.TotalSeconds, Limit = limit })).AsList();
    }

    public async Task<NpgsqlConnection?> TryLockAsync(long cohortId, CancellationToken ct)
    {
        var conn = await db.OpenConnectionAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@Class, @Id)", new { Class = LockClass, Id = (int)cohortId }))
            return conn;
        await conn.DisposeAsync();
        return null;
    }

    public static Task UnlockAsync(NpgsqlConnection conn, long cohortId) =>
        conn.ExecuteAsync("SELECT pg_advisory_unlock(@Class, @Id)", new { Class = LockClass, Id = (int)cohortId });

    public async Task MarkCalculatingAsync(long cohortId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE cohorts SET status = 'calculating' WHERE id = @Id AND active_version IS NULL", new { Id = cohortId });
    }

    public async Task<List<string>> StaticMembersAsync(long cohortId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<string>(
            "SELECT distinct_id FROM cohort_static_members WHERE cohort_id = @Id ORDER BY distinct_id", new { Id = cohortId })).AsList();
    }

    public async Task<bool> CompleteAsync(DueCohortRecord cohort, long version, long memberCount, int calculationMs, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            WITH old AS (SELECT id, project_id, active_version FROM cohorts WHERE id = @Id AND deleted_at IS NULL FOR UPDATE),
            retired AS (
                INSERT INTO cohort_retired_versions (cohort_id, project_id, version)
                SELECT id, project_id, active_version FROM old WHERE active_version IS NOT NULL
                ON CONFLICT DO NOTHING
            )
            UPDATE cohorts c SET active_version = @Version, member_count = @Count, calculation_ms = @Ms,
                status = 'ready', last_error = NULL,
                calculated_at = CASE WHEN c.updated_at > @StartedAt THEN NULL ELSE now() END
            FROM old WHERE c.id = old.id
            """, new { cohort.Id, Version = version, Count = memberCount, Ms = calculationMs, cohort.StartedAt }, tx);
        await tx.CommitAsync(ct);
        return updated > 0;
    }

    public async Task FailAsync(long cohortId, string error, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE cohorts SET last_error = @Error, calculated_at = now(),
                status = CASE WHEN active_version IS NULL THEN 'error' ELSE status END
            WHERE id = @Id
            """, new { Id = cohortId, Error = error.Length > 500 ? error[..500] : error });
    }

    public async Task<List<RetiredVersionRecord>> ExpiredVersionsAsync(TimeSpan grace, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<RetiredVersionRecord>("""
            SELECT cohort_id, project_id, version FROM cohort_retired_versions
            WHERE retired_at < now() - make_interval(secs => @GraceSeconds)
            ORDER BY retired_at LIMIT 100
            """, new { GraceSeconds = (int)grace.TotalSeconds })).AsList();
    }

    public async Task ForgetVersionAsync(RetiredVersionRecord v, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM cohort_retired_versions WHERE cohort_id = @CohortId AND version = @Version", new { v.CohortId, v.Version });
    }

    public async Task<List<DeletedCohortRecord>> DeletedAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DeletedCohortRecord>(
            "SELECT id, project_id FROM cohorts WHERE deleted_at IS NOT NULL ORDER BY id LIMIT 100")).AsList();
    }

    public async Task RemoveAsync(long cohortId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM cohorts WHERE id = @Id AND deleted_at IS NOT NULL", new { Id = cohortId });
    }
}
