using Microsoft.AspNetCore.Diagnostics;
using MySqlConnector;
using ReapDeep.Analytics;

namespace ReapDeep.QueryApi.Infrastructure;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        var (status, message) = exception switch
        {
            ValidationException e => (StatusCodes.Status400BadRequest, e.Message),
            MySqlException e when IsResourceLimit(e) =>
                (StatusCodes.Status503ServiceUnavailable, "Query exceeded the database resource limits; narrow the date range or filters"),
            MySqlException => (StatusCodes.Status502BadGateway, "Query failed in the analytics database"),
            _ => (0, ""),
        };
        if (status == 0) return false;

        if (exception is MySqlException) log.LogError(exception, "StarRocks query failed");
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, cancellationToken: ct);
        return true;
    }

    static bool IsResourceLimit(MySqlException e) =>
        e.Message.Contains("exceed", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
}
