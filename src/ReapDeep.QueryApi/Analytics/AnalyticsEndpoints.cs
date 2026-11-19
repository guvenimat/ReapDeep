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
        api.MapPost("/query/funnels/persons", FunnelPersons);
        api.MapPost("/query/retention", Retention);
        api.MapPost("/query/paths", Paths);
        api.MapPost("/query/rfm", Rfm);
        api.MapPost("/query/rfm/migration", RfmMigration);
        api.MapGet("/persons/{personId}/rfm", PersonRfm);
        api.MapPost("/query/lifecycle", Lifecycle);
        api.MapPost("/query/stickiness", Stickiness);
        api.MapPost("/query/funnels/correlation", FunnelCorrelation);
        api.MapPost("/query/funnels/correlation/property", FunnelPropertyCorrelation);
        api.MapPost("/query/web", Web);
        api.MapGet("/events", Events);
    }

    static async Task<IResult> Trends(TrendRequest req, ProjectContext project, TrendService trends, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await trends.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "trends", req, AppJsonContext.Default.TrendRequest, req.Filters, req.Breakdown,
            AppJsonContext.Default.TrendResponse, () => trends.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.TrendResponse);

    static async Task<IResult> Funnels(FunnelRequest req, ProjectContext project, FunnelService funnels, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await funnels.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "funnels", req, AppJsonContext.Default.FunnelRequest, req.Filters, req.Breakdown,
            AppJsonContext.Default.FunnelResponse, () => funnels.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.FunnelResponse);

    static async Task<IResult> FunnelPersons(FunnelPersonsRequest req, ProjectContext project, FunnelService funnels, CancellationToken ct) =>
        ApiResults.Ok(await funnels.PersonsAsync(project, req, ct), AppJsonContext.Default.FunnelPersonsResponse);

    static async Task<IResult> Retention(RetentionRequest req, ProjectContext project, RetentionService retention, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await retention.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "retention", req, AppJsonContext.Default.RetentionRequest, req.Filters, null,
            AppJsonContext.Default.RetentionResponse, () => retention.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.RetentionResponse);

    static async Task<IResult> Paths(PathsRequest req, ProjectContext project, PathService paths, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await paths.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "paths", req, AppJsonContext.Default.PathsRequest, req.Filters, null,
            AppJsonContext.Default.PathsResponse, () => paths.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.PathsResponse);

    static async Task<IResult> Rfm(RfmRequest req, ProjectContext project, RfmService rfm, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await rfm.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "rfm", req, AppJsonContext.Default.RfmRequest, req.Filters, null,
            AppJsonContext.Default.RfmResponse, () => rfm.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.RfmResponse);

    static async Task<IResult> RfmMigration(RfmMigrationRequest req, ProjectContext project, RfmService rfm, CancellationToken ct) =>
        ApiResults.Ok(await rfm.MigrationAsync(project, req, ct), AppJsonContext.Default.RfmMigrationResponse);

    static async Task<IResult> PersonRfm(string personId, ProjectContext project, RfmService rfm,
        [FromQuery(Name = "window_days")] int? windowDays, [FromQuery(Name = "purchase_event")] string? purchaseEvent, CancellationToken ct) =>
        ApiResults.Ok(await rfm.PersonAsync(project, personId, windowDays, purchaseEvent, ct), AppJsonContext.Default.RfmPersonResponse);

    static async Task<IResult> FunnelCorrelation(FunnelRequest req, ProjectContext project, FunnelService funnels, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) =>
        ApiResults.Ok(await cache.GetOrRunAsync(http, project, "funnel-correlation", req, AppJsonContext.Default.FunnelRequest, req.Filters, null,
            AppJsonContext.Default.FunnelCorrelationResponse, () => funnels.CorrelationAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.FunnelCorrelationResponse);

    static async Task<IResult> FunnelPropertyCorrelation(FunnelPropertyRequest req, ProjectContext project, FunnelService funnels, AnalyticsCache cache,
        HttpContext http, CancellationToken ct) =>
        ApiResults.Ok(await cache.GetOrRunAsync(http, project, "funnel-property-correlation", req, AppJsonContext.Default.FunnelPropertyRequest, req.Filters, null,
            AppJsonContext.Default.FunnelPropertyResponse, () => funnels.PropertyCorrelationAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.FunnelPropertyResponse);

    static async Task<IResult> Stickiness(StickinessRequest req, ProjectContext project, StickinessService stickiness, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await stickiness.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "stickiness", req, AppJsonContext.Default.StickinessRequest, req.Filters, null,
            AppJsonContext.Default.StickinessResponse, () => stickiness.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.StickinessResponse);

    static async Task<IResult> Lifecycle(LifecycleRequest req, ProjectContext project, LifecycleService lifecycle, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await lifecycle.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "lifecycle", req, AppJsonContext.Default.LifecycleRequest, req.Filters, null,
            AppJsonContext.Default.LifecycleResponse, () => lifecycle.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.LifecycleResponse);

    static async Task<IResult> Web(WebAnalyticsRequest req, ProjectContext project, WebAnalyticsService web, AnalyticsCache cache, HttpContext http,
        CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await web.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await cache.GetOrRunAsync(http, project, "web", req, AppJsonContext.Default.WebAnalyticsRequest, req.Filters, null,
            AppJsonContext.Default.WebAnalyticsResponse, () => web.RunAsync(project, req, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.WebAnalyticsResponse);

    static async Task<IResult> Events(ProjectContext project, EventService events,
        [FromQuery(Name = "event")] string? eventName, [FromQuery(Name = "distinct_id")] string? distinctId,
        [FromQuery(Name = "person_id")] string? personId, [FromQuery(Name = "date_from")] string? dateFrom,
        [FromQuery(Name = "date_to")] string? dateTo, int? limit, CancellationToken ct) =>
        ApiResults.Ok(await events.ListAsync(project, new EventsQuery(eventName, distinctId, personId, dateFrom, dateTo, limit), ct),
            AppJsonContext.Default.EventsResponse);
}
