using MySqlConnector;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.PersonWorker.Infrastructure;

public sealed class StarRocksStore(MySqlDataSource db)
{
    const int CommandTimeoutSeconds = 600;

    public Task ExecuteAsync(SqlQuery query, CancellationToken ct) => Run(query, async cmd => await cmd.ExecuteNonQueryAsync(ct), ct);

    public Task<long> ScalarAsync(SqlQuery query, CancellationToken ct) => Run(query, async cmd => Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)), ct);

    public Task<List<T>> QueryAsync<T>(SqlQuery query, Func<MySqlDataReader, T> map, CancellationToken ct) => Run(query, async cmd =>
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }, ct);

    public async IAsyncEnumerable<T> StreamAsync<T>(SqlQuery query, Func<MySqlDataReader, T> map,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = Command(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return map(reader);
    }

    async Task<T> Run<T>(SqlQuery query, Func<MySqlCommand, Task<T>> action, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var cmd = Command(query, conn);
            return await action(cmd);
        }
        catch (MySqlException e) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The StarRocks query was canceled", e, ct);
        }
    }

    static MySqlCommand Command(SqlQuery query, MySqlConnection conn)
    {
        var cmd = new MySqlCommand(query.Sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var (name, value) in query.Parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
