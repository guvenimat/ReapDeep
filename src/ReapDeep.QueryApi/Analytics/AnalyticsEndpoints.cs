using Microsoft.AspNetCore.Mvc;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public static class AnalyticsEndpoints
{
    public static void MapAnalytics(this RouteGroupBuilder api)
    {
        api.MapPost("/query/trends", Trends);
        api.MapPost("/query/funnels", Funnels);
        api.MapPost("/query/retention", Retention);
        api.MapPost("/query/paths", Paths);
        api.MapGet("/events", Events);
    }

    static async Task<IResult> Trends(TrendRequest req, ProjectContext project, TrendService trends, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await trends.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await trends.RunAsync(project, req, ct), AppJsonContext.Default.TrendResponse);

    static async Task<IResult> Funnels(FunnelRequest req, ProjectContext project, FunnelService funnels, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await funnels.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await funnels.RunAsync(project, req, ct), AppJsonContext.Default.FunnelResponse);

    static async Task<IResult> Retention(RetentionRequest req, ProjectContext project, RetentionService retention, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await retention.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await retention.RunAsync(project, req, ct), AppJsonContext.Default.RetentionResponse);

    static async Task<IResult> Paths(PathsRequest req, ProjectContext project, PathService paths, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await paths.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await paths.RunAsync(project, req, ct), AppJsonContext.Default.PathsResponse);

    static async Task<IResult> Events(ProjectContext project, EventService events,
        [FromQuery(Name = "event")] string? eventName, [FromQuery(Name = "distinct_id")] string? distinctId,
        [FromQuery(Name = "person_id")] string? personId, [FromQuery(Name = "date_from")] string? dateFrom,
        [FromQuery(Name = "date_to")] string? dateTo, int? limit, CancellationToken ct) =>
        ApiResults.Ok(await events.ListAsync(project, new EventsQuery(eventName, distinctId, personId, dateFrom, dateTo, limit), ct),
            AppJsonContext.Default.EventsResponse);
}
