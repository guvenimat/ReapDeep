using Microsoft.AspNetCore.Diagnostics;
using MySqlConnector;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Cohorts;

namespace ReapDeep.QueryApi.Infrastructure;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        var (status, message) = exception switch
        {
            ValidationException e => (StatusCodes.Status400BadRequest, e.Message),
            CohortNotFoundException e => (StatusCodes.Status404NotFound, e.Message),
            CohortNotReadyException e => (StatusCodes.Status409Conflict, e.Message),
            MySqlException e => StarRocksErrors.Classify(e) switch
            {
                StarRocksFailure.ResourceLimit => (StatusCodes.Status503ServiceUnavailable, "Query exceeded the database resource limits; narrow the date range or filters"),
                StarRocksFailure.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The analytics database is not reachable right now"),
                _ => (StatusCodes.Status502BadGateway, "Query failed in the analytics database"),
            },
            _ => (0, ""),
        };
        if (status == 0) return false;

        if (exception is MySqlException) log.LogError(exception, "StarRocks query failed");
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, cancellationToken: ct);
        return true;
    }
}
