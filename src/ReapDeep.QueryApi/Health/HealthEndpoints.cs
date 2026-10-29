using Dapper;
using Npgsql;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Health;

public sealed record HealthResponse(string Postgres, string Starrocks);

public static class HealthEndpoints
{
    const string Ok = "ok";

    public static void MapHealth(this IEndpointRouteBuilder app) => app.MapGet("/healthz", Check);

    static async Task<IResult> Check(NpgsqlDataSource pg, StarRocksClient starRocks, CancellationToken ct)
    {
        var postgres = Probe(async () =>
        {
            await using var conn = await pg.OpenConnectionAsync(ct);
            await conn.ExecuteScalarAsync<int>("SELECT 1");
        });
        var starrocks = Probe(() => starRocks.PingAsync(ct));
        var result = new HealthResponse(await postgres, await starrocks);
        return Results.Json(result, AppJsonContext.Default.HealthResponse,
            statusCode: result is { Postgres: Ok, Starrocks: Ok } ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    static async Task<string> Probe(Func<Task> probe)
    {
        try
        {
            await probe();
            return Ok;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
