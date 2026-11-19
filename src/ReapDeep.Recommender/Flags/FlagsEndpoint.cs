using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ReapDeep.Analytics.Flags;
using ReapDeep.Recommender.Infrastructure;
using ReapDeep.Recommender.Projects;
using ReapDeep.Recommender.Recommendations;

namespace ReapDeep.Recommender.Flags;

public sealed record FlagsResponse(Dictionary<string, JsonElement> Flags);

public static class FlagsEndpoint
{
    const int MaxIdentityLength = 200;
    const int MaxPropertiesLength = 4000;
    const int MaxProperties = 50;
    static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();
    static readonly JsonElement False = JsonDocument.Parse("false").RootElement.Clone();

    const string BearerPrefix = "Bearer ";

    public static void MapFlags(this WebApplication app)
    {
        app.MapGet("/flags", Flags).RequireRateLimiting(HostingExtensions.PublicPolicy);
        app.MapGet("/flags/definitions", Definitions);
    }

    static IResult Definitions(HttpContext ctx, ProjectCache projects, FlagCache flags)
    {
        if (!projects.IsLoaded || !flags.IsLoaded) return Error(StatusCodes.Status503ServiceUnavailable, "Starting up, try again shortly");
        if (ctx.Request.Headers.Origin.Count > 0)
            return Error(StatusCodes.Status403Forbidden, "Server keys must stay on the server; this request came from a browser");
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) || !projects.TryGetProjectIdBySecret(header[BearerPrefix.Length..].Trim(), out var projectId))
            return Error(StatusCodes.Status401Unauthorized, "A server key is required: Authorization: Bearer sk_...");
        ctx.Response.Headers.CacheControl = "private, no-store";
        return Results.Json(new FlagDefinitionsResponse([.. flags.Definitions(projectId)]), AppJsonContext.Default.FlagDefinitionsResponse);
    }

    static IResult Flags(HttpContext ctx, ProjectCache projects, FlagCache flags, [FromQuery(Name = "api_key")] string? apiKey, string? identity,
        string? properties)
    {
        if (!projects.IsLoaded || !flags.IsLoaded) return Error(StatusCodes.Status503ServiceUnavailable, "Starting up, try again shortly");
        if (string.IsNullOrEmpty(apiKey) || !projects.TryGetProjectId(apiKey, out var projectId)) return Error(StatusCodes.Status401Unauthorized, "Invalid api_key");
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > MaxIdentityLength)
            return Error(StatusCodes.Status400BadRequest, $"identity is required (at most {MaxIdentityLength} characters)");
        if (ParseProperties(properties) is not { } props)
            return Error(StatusCodes.Status400BadRequest, $"properties must be a JSON object of at most {MaxPropertiesLength} characters with at most {MaxProperties} string, number or boolean values; other values are ignored");

        ctx.Response.Headers.CacheControl = "private, no-store";
        return Results.Json(new FlagsResponse(Evaluate(flags.Get(projectId), identity, props, flags.Cohorts)), AppJsonContext.Default.FlagsResponse);
    }

    public static Dictionary<string, JsonElement> Evaluate(IReadOnlyList<CompiledFlag> flags, string identity, IReadOnlyDictionary<string, string> properties,
        IFlagCohorts? cohorts = null)
    {
        var result = new Dictionary<string, JsonElement>(flags.Count, StringComparer.Ordinal);
        foreach (var flag in flags)
        {
            var r = flag.Evaluate(identity, properties, cohorts);
            result[flag.Key] = !r.Enabled ? False : r.Variant is null ? True : JsonSerializer.SerializeToElement(r.Variant, AppJsonContext.Default.String);
        }
        return result;
    }

    public static Dictionary<string, string>? ParseProperties(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        if (json.Length > MaxPropertiesLength) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    result[p.Name] = FlagRules.Text(p.Value);
            }
            return result.Count <= MaxProperties ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static IResult Error(int status, string message) => Results.Json(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, statusCode: status);
}
