using Microsoft.AspNetCore.Diagnostics;
using MySqlConnector;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Ai;
using ReapDeep.QueryApi.Cohorts;

namespace ReapDeep.QueryApi.Infrastructure;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    const string BusyRetryAfterSeconds = "5";

    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        var failure = exception is MySqlException db ? StarRocksErrors.Classify(db) : (StarRocksFailure?)null;
        var (status, message) = exception switch
        {
            ValidationException e => (StatusCodes.Status400BadRequest, e.Message),
            CohortNotFoundException e => (StatusCodes.Status404NotFound, e.Message),
            CohortNotReadyException e => (StatusCodes.Status409Conflict, e.Message),
            AiDisabledException e => (StatusCodes.Status403Forbidden, e.Message),
            AiQuotaException e => (StatusCodes.Status429TooManyRequests, e.Message),
            AiUnavailableException e => (StatusCodes.Status503ServiceUnavailable, e.Message),
            MySqlException => failure switch
            {
                StarRocksFailure.ResourceLimit => (StatusCodes.Status503ServiceUnavailable, "Query exceeded the database resource limits; narrow the date range or filters"),
                StarRocksFailure.Busy => (StatusCodes.Status503ServiceUnavailable, "The analytics database is busy; try again in a few seconds"),
                StarRocksFailure.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The analytics database is not reachable right now"),
                _ => (StatusCodes.Status502BadGateway, "Query failed in the analytics database"),
            },
            _ => (0, ""),
        };
        if (status == 0) return false;

        if (failure is not null) log.LogError(exception, "StarRocks query failed");
        ctx.Response.StatusCode = status;
        if (failure == StarRocksFailure.Busy) ctx.Response.Headers.RetryAfter = BusyRetryAfterSeconds;
        await ctx.Response.WriteAsJsonAsync(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, cancellationToken: ct);
        return true;
    }
}
