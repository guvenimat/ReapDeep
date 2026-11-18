using MySqlConnector;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.QueryApi.Infrastructure;

public sealed record ExplainResponse(List<string> Sql, List<string> Plans);

public sealed class StarRocksClient(MySqlDataSource db, IConfiguration cfg)
{
    public const int CommandTimeoutSeconds = 30;
    public const int MaxParallelPerRequest = 4;

    readonly bool _allowExplain = cfg["QueryApi:AllowExplain"] == "true";

    public async Task<List<T>> QueryAsync<T>(SqlQuery query, Func<MySqlDataReader, T> map, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var cmd = new MySqlCommand(query.Sql, conn) { CommandTimeout = CommandTimeoutSeconds };
            foreach (var (name, value) in query.Parameters) cmd.Parameters.AddWithValue(name, value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<T>();
            while (await reader.ReadAsync(ct)) rows.Add(map(reader));
            return rows;
        }
        catch (MySqlException e) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The StarRocks query was canceled", e, ct);
        }
    }

    public async Task<List<T>> QueryAsync<T>(SqlQuery query, Func<MySqlDataReader, T> map, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await QueryAsync(query, map, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public static SemaphoreSlim RequestGate() => new(MaxParallelPerRequest, MaxParallelPerRequest);

    public async Task<ExplainResponse> ExplainAsync(IReadOnlyList<SqlQuery> queries, CancellationToken ct)
    {
        Ensure.That(_allowExplain, "explain is disabled in this environment");
        using var gate = RequestGate();
        var plans = await Task.WhenAll(queries.Select(q =>
            QueryAsync(q with { Sql = "EXPLAIN " + q.Sql }, r => r.GetString(0), gate, ct)));
        return new ExplainResponse(queries.Select(q => q.Sql).ToList(), plans.Select(p => string.Join('\n', p)).ToList());
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync(ct);
    }
}
