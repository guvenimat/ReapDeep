using ReapDeep.Analytics;
using ReapDeep.Analytics.Experiments;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Flags;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Experiments;

public static class ExperimentEndpoints
{
    public static void MapExperiments(this RouteGroupBuilder api)
    {
        api.MapGet("/experiments", List);
        api.MapGet("/experiments/{id:long}", Get);
        api.MapGet("/experiments/{id:long}/results", GetResults);
        api.MapPost("/experiments", Create).RequireManager();
        api.MapPut("/experiments/{id:long}", Update).RequireManager();
        api.MapPost("/experiments/{id:long}/start", Start).RequireManager();
        api.MapPost("/experiments/{id:long}/stop", Stop).RequireManager();
        api.MapDelete("/experiments/{id:long}", Delete).RequireManager();
    }

    static async Task<IResult> List(ProjectContext project, ExperimentRepository experiments, CancellationToken ct) =>
        ApiResults.Ok(new ExperimentsResponse(await experiments.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.ExperimentsResponse);

    static async Task<IResult> Get(long id, ProjectContext project, ExperimentRepository experiments, CancellationToken ct) =>
        await experiments.GetAsync(project.ProjectId, id, ct) is { } x
            ? ApiResults.Ok(x, AppJsonContext.Default.Experiment)
            : ApiResults.NotFound("Experiment not found");

    static async Task<IResult> GetResults(long id, ProjectContext project, ExperimentRepository experiments, ExperimentService service, AnalyticsCache cache,
        HttpContext http, CancellationToken ct)
    {
        if (await experiments.GetAsync(project.ProjectId, id, ct) is not { } x) return ApiResults.NotFound("Experiment not found");
        var key = ExperimentService.Key(x, DateTime.UtcNow);
        return ApiResults.Ok(await cache.GetOrRunAsync(http, project, "experiments", key, AppJsonContext.Default.ExperimentResultsKey, null, null,
            AppJsonContext.Default.ExperimentResults, () => service.RunAsync(project, x, ct), (r, ms) => r with { TookMs = ms }, ct),
            AppJsonContext.Default.ExperimentResults);
    }

    static async Task<IResult> Create(ExperimentBody req, ProjectContext project, ExperimentRepository experiments, FlagRepository flags, CancellationToken ct)
    {
        Ensure.That(req.FlagId is not null, "flag_id is required");
        var values = await ValuesAsync(req, null, project, flags, ct);
        if (values is null) return ApiResults.BadRequest($"Flag {req.FlagId} not found in this project");
        Ensure.That(await experiments.CountAsync(project.ProjectId, ct) < ExperimentRules.MaxExperimentsPerProject,
            $"A project can have at most {ExperimentRules.MaxExperimentsPerProject} experiments");
        return await experiments.CreateAsync(project.ProjectId, values, project.UserId!.Value, ct) is { } created
            ? ApiResults.Created(created, AppJsonContext.Default.Experiment)
            : ApiResults.NotFound("Experiment not found");
    }

    static async Task<IResult> Update(long id, ExperimentBody req, ProjectContext project, ExperimentRepository experiments, FlagRepository flags, CancellationToken ct)
    {
        if (await experiments.GetAsync(project.ProjectId, id, ct) is not { } current) return ApiResults.NotFound("Experiment not found");
        if (req.FlagId is { } flagId && flagId != current.FlagId && current.Status != ExperimentStatus.Draft)
            return ApiResults.Conflict("The flag of an experiment can only be changed while it is a draft");
        var values = await ValuesAsync(req, current, project, flags, ct);
        if (values is null) return ApiResults.BadRequest($"Flag {req.FlagId} not found in this project");
        return await experiments.UpdateAsync(project.ProjectId, id, values, project.UserId!.Value, ct) is { } updated
            ? ApiResults.Ok(updated, AppJsonContext.Default.Experiment)
            : ApiResults.Conflict("The experiment changed while saving; reload it and try again");
    }

    static async Task<IResult> Start(long id, ProjectContext project, ExperimentRepository experiments, CancellationToken ct)
    {
        if (await experiments.GetAsync(project.ProjectId, id, ct) is not { } current) return ApiResults.NotFound("Experiment not found");
        if (current.Status != ExperimentStatus.Draft) return ApiResults.Conflict($"Only a draft experiment can be started (this one is {current.Status})");
        ExperimentRules.Control(current.ControlVariant, current.Variants);
        return await experiments.StartAsync(project.ProjectId, id, project.UserId!.Value, ct) is { } started
            ? ApiResults.Ok(started, AppJsonContext.Default.Experiment)
            : ApiResults.Conflict("The experiment could not be started: its end date has passed or it is no longer a draft");
    }

    static async Task<IResult> Stop(long id, ProjectContext project, ExperimentRepository experiments, CancellationToken ct)
    {
        if (await experiments.GetAsync(project.ProjectId, id, ct) is not { } current) return ApiResults.NotFound("Experiment not found");
        if (current.Status != ExperimentStatus.Running) return ApiResults.Conflict($"Only a running experiment can be stopped (this one is {current.Status})");
        return await experiments.StopAsync(project.ProjectId, id, project.UserId!.Value, ct) is { } stopped
            ? ApiResults.Ok(stopped, AppJsonContext.Default.Experiment)
            : ApiResults.Conflict("The experiment is no longer running");
    }

    static async Task<IResult> Delete(long id, ProjectContext project, ExperimentRepository experiments, CancellationToken ct)
    {
        if (await experiments.GetAsync(project.ProjectId, id, ct) is not { } current) return ApiResults.NotFound("Experiment not found");
        if (current.Status == ExperimentStatus.Running) return ApiResults.Conflict("Stop the experiment before deleting it");
        return await experiments.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.Conflict("Stop the experiment before deleting it");
    }

    static async Task<ExperimentRepository.ExperimentValues?> ValuesAsync(ExperimentBody req, Experiment? current, ProjectContext project, FlagRepository flags,
        CancellationToken ct)
    {
        var flagId = req.FlagId ?? current!.FlagId;
        if (await flags.GetAsync(project.ProjectId, flagId, ct) is not { } flag) return null;

        var name = Ensure.NotBlank(req.Name ?? current?.Name, "name", ExperimentRules.MaxNameLength);
        var description = (req.Description ?? current?.Description ?? "").Trim();
        Ensure.That(description.Length <= ExperimentRules.MaxDescriptionLength, $"description can be at most {ExperimentRules.MaxDescriptionLength} characters");
        var sameFlag = current is not null && flagId == current.FlagId;
        var control = ExperimentRules.Control(req.ControlVariant ?? (sameFlag ? current!.ControlVariant : null), flag.Variants);
        var metric = ExperimentRules.Metric(req.Metric ?? current?.Metric);
        var start = Date(req.StartDate, current?.StartDate, project.TimeZone, isEnd: false, "start_date");
        var end = Date(req.EndDate, current?.EndDate, project.TimeZone, isEnd: true, "end_date");
        Ensure.That(start is not null || current is null || current.Status == ExperimentStatus.Draft, "start_date cannot be removed once the experiment has started");
        Ensure.That(start is null || end is null || end > start, "end_date must be after start_date");
        if (start is not null && end is not null)
            Ensure.That(end.Value - start.Value <= TimeSpan.FromDays(ExperimentRules.MaxRangeDays), $"An experiment can run for at most {ExperimentRules.MaxRangeDays} days");
        return new ExperimentRepository.ExperimentValues(name, description, flagId, control, ExperimentRules.MetricJson(metric), start, end);
    }

    static DateTime? Date(string? requested, DateTime? current, TimeZoneInfo tz, bool isEnd, string name) => requested switch
    {
        null => current,
        "" => null,
        _ => TimeRange.ParseBoundary(requested.Trim(), tz, isEnd, name),
    };
}
