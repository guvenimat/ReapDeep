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
        api.MapGet("/events", Events);
    }

    static async Task<IResult> Trends(TrendRequest req, ProjectContext project, TrendService trends, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await trends.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await trends.RunAsync(project, req, ct), AppJsonContext.Default.TrendResponse);

    static async Task<IResult> Funnels(FunnelRequest req, ProjectContext project, FunnelService funnels, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await funnels.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await funnels.RunAsync(project, req, ct), AppJsonContext.Default.FunnelResponse);

    static async Task<IResult> FunnelPersons(FunnelPersonsRequest req, ProjectContext project, FunnelService funnels, CancellationToken ct) =>
        ApiResults.Ok(await funnels.PersonsAsync(project, req, ct), AppJsonContext.Default.FunnelPersonsResponse);

    static async Task<IResult> Retention(RetentionRequest req, ProjectContext project, RetentionService retention, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await retention.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await retention.RunAsync(project, req, ct), AppJsonContext.Default.RetentionResponse);

    static async Task<IResult> Paths(PathsRequest req, ProjectContext project, PathService paths, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await paths.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await paths.RunAsync(project, req, ct), AppJsonContext.Default.PathsResponse);

    static async Task<IResult> Rfm(RfmRequest req, ProjectContext project, RfmService rfm, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await rfm.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await rfm.RunAsync(project, req, ct), AppJsonContext.Default.RfmResponse);

    static async Task<IResult> RfmMigration(RfmMigrationRequest req, ProjectContext project, RfmService rfm, CancellationToken ct) =>
        ApiResults.Ok(await rfm.MigrationAsync(project, req, ct), AppJsonContext.Default.RfmMigrationResponse);

    static async Task<IResult> PersonRfm(string personId, ProjectContext project, RfmService rfm,
        [FromQuery(Name = "window_days")] int? windowDays, [FromQuery(Name = "purchase_event")] string? purchaseEvent, CancellationToken ct) =>
        ApiResults.Ok(await rfm.PersonAsync(project, personId, windowDays, purchaseEvent, ct), AppJsonContext.Default.RfmPersonResponse);

    static async Task<IResult> Lifecycle(LifecycleRequest req, ProjectContext project, LifecycleService lifecycle, CancellationToken ct) => req.Explain
        ? ApiResults.Ok(await lifecycle.ExplainAsync(project, req, ct), AppJsonContext.Default.ExplainResponse)
        : ApiResults.Ok(await lifecycle.RunAsync(project, req, ct), AppJsonContext.Default.LifecycleResponse);

    static async Task<IResult> Events(ProjectContext project, EventService events,
        [FromQuery(Name = "event")] string? eventName, [FromQuery(Name = "distinct_id")] string? distinctId,
        [FromQuery(Name = "person_id")] string? personId, [FromQuery(Name = "date_from")] string? dateFrom,
        [FromQuery(Name = "date_to")] string? dateTo, int? limit, CancellationToken ct) =>
        ApiResults.Ok(await events.ListAsync(project, new EventsQuery(eventName, distinctId, personId, dateFrom, dateTo, limit), ct),
            AppJsonContext.Default.EventsResponse);
}
