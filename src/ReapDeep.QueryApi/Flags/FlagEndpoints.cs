using System.Security.Cryptography;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Flags;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Flags;

public static class FlagEndpoints
{
    const int MaxNameLength = 200;
    const int MaxDescriptionLength = 1000;

    public static void MapFlags(this RouteGroupBuilder api)
    {
        api.MapGet("/flags", List);
        api.MapGet("/flags/{id:long}", Get);
        api.MapPost("/flags", Create).RequireManager();
        api.MapPut("/flags/{id:long}", Update).RequireManager();
        api.MapDelete("/flags/{id:long}", Delete).RequireManager();
    }

    static async Task<IResult> List(ProjectContext project, FlagRepository flags, CancellationToken ct) =>
        ApiResults.Ok(new FeatureFlagsResponse(await flags.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.FeatureFlagsResponse);

    static async Task<IResult> Get(long id, ProjectContext project, FlagRepository flags, CancellationToken ct) =>
        await flags.GetAsync(project.ProjectId, id, ct) is { } flag
            ? ApiResults.Ok(flag, AppJsonContext.Default.FeatureFlag)
            : ApiResults.NotFound("Flag not found");

    static async Task<IResult> Create(FlagBody req, ProjectContext project, FlagRepository flags, CancellationToken ct)
    {
        var key = FlagRules.Key(req.Key);
        var values = Values(req, key, null);
        Ensure.That(await flags.CountAsync(project.ProjectId, ct) < FlagRules.MaxFlagsPerProject,
            $"A project can have at most {FlagRules.MaxFlagsPerProject} flags");
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        return await flags.CreateAsync(project.ProjectId, key, values, salt, project.UserId!.Value, ct) is { } created
            ? ApiResults.Created(created, AppJsonContext.Default.FeatureFlag)
            : ApiResults.Conflict($"A flag with the key '{key}' already exists in this project");
    }

    static async Task<IResult> Update(long id, FlagBody req, ProjectContext project, FlagRepository flags, CancellationToken ct)
    {
        if (await flags.GetAsync(project.ProjectId, id, ct) is not { } current) return ApiResults.NotFound("Flag not found");
        Ensure.That(req.Key is null || req.Key.Trim() == current.Key, "key cannot be changed; create a new flag instead");
        var values = Values(req, current.Key, current);
        return await flags.UpdateAsync(project.ProjectId, id, values, project.UserId!.Value, ct) is { } updated
            ? ApiResults.Ok(updated, AppJsonContext.Default.FeatureFlag)
            : ApiResults.NotFound("Flag not found");
    }

    static async Task<IResult> Delete(long id, ProjectContext project, FlagRepository flags, CancellationToken ct) =>
        await flags.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.NotFound("Flag not found");

    static FlagRepository.FlagValues Values(FlagBody req, string key, FeatureFlag? current)
    {
        var name = (req.Name ?? current?.Name)?.Trim() is { Length: > 0 } n ? n : key;
        Ensure.That(name.Length <= MaxNameLength, $"name can be at most {MaxNameLength} characters");
        var description = (req.Description ?? current?.Description ?? "").Trim();
        Ensure.That(description.Length <= MaxDescriptionLength, $"description can be at most {MaxDescriptionLength} characters");
        var rollout = FlagRules.Rollout(req.RolloutPercentage ?? current?.RolloutPercentage);
        var variants = FlagRules.Variants(req.Variants ?? current?.Variants);
        var conditions = FlagRules.Conditions(req.Conditions ?? current?.Conditions);
        var enabled = req.Enabled ?? current?.Enabled ?? false;
        return new FlagRepository.FlagValues(name, description, enabled, rollout, FlagRules.VariantsJson(variants), FlagRules.ConditionsJson(conditions));
    }
}
