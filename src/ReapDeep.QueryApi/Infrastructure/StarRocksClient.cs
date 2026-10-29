using MySqlConnector;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.QueryApi.Infrastructure;

public sealed record ExplainResponse(List<string> Sql, List<string> Plans);

public sealed class StarRocksClient(MySqlDataSource db, IConfiguration cfg)
{
    const int CommandTimeoutSeconds = 30;

    readonly bool _allowExplain = cfg["QueryApi:AllowExplain"] == "true";

    public async Task<List<T>> QueryAsync<T>(SqlQuery query, Func<MySqlDataReader, T> map, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand(query.Sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var (name, value) in query.Parameters) cmd.Parameters.AddWithValue(name, value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }

    public async Task<ExplainResponse> ExplainAsync(IReadOnlyList<SqlQuery> queries, CancellationToken ct)
    {
        Ensure.That(_allowExplain, "explain is disabled in this environment");
        var plans = await Task.WhenAll(queries.Select(q =>
            QueryAsync(q with { Sql = "EXPLAIN " + q.Sql }, r => r.GetString(0), ct)));
        return new ExplainResponse(queries.Select(q => q.Sql).ToList(), plans.Select(p => string.Join('\n', p)).ToList());
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync(ct);
    }
}
