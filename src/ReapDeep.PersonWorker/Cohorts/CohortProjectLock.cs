using Dapper;
using Npgsql;

namespace ReapDeep.PersonWorker.Cohorts;

public static class CohortProjectLock
{
    const int LockClass = 0x434F50;

    public static Task EnterRefreshAsync(NpgsqlConnection conn, long projectId) =>
        conn.ExecuteAsync("SELECT pg_advisory_lock_shared(@Class, @Project)", new { Class = LockClass, Project = (int)projectId });

    public static Task ExitRefreshAsync(NpgsqlConnection conn, long projectId) =>
        conn.ExecuteAsync("SELECT pg_advisory_unlock_shared(@Class, @Project)", new { Class = LockClass, Project = (int)projectId });

    public static Task ExcludeRefreshesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId) =>
        conn.ExecuteAsync("SELECT pg_advisory_xact_lock(@Class, @Project)", new { Class = LockClass, Project = (int)projectId }, tx);
}
