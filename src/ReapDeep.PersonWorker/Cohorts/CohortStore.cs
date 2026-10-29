using MySqlConnector;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.PersonWorker.Cohorts;

public sealed class CohortStore(MySqlDataSource db)
{
    const int CommandTimeoutSeconds = 600;

    public async Task ExecuteAsync(SqlQuery query, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = Command(query, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> ScalarAsync(SqlQuery query, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = Command(query, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    static MySqlCommand Command(SqlQuery query, MySqlConnection conn)
    {
        var cmd = new MySqlCommand(query.Sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var (name, value) in query.Parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
