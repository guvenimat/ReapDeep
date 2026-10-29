using System.Security.Cryptography;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Organization;

public static class ApiKeyEndpoints
{
    const string KeyPrefix = "rk_";
    const int VisiblePrefixLength = 7;

    public static void MapApiKeys(this RouteGroupBuilder api)
    {
        api.MapGet("/api-keys", List).RequireManager();
        api.MapPost("/api-keys", Create).RequireManager();
        api.MapDelete("/api-keys/{id:long}", Revoke).RequireManager();
    }

    static async Task<IResult> List(ProjectContext project, ApiKeyRepository keys, CancellationToken ct) =>
        ApiResults.Ok(new ApiKeysResponse(await keys.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.ApiKeysResponse);

    static async Task<IResult> Create(CreateApiKeyRequest req, ProjectContext project, ApiKeyRepository keys, CancellationToken ct)
    {
        var name = Ensure.NotBlank(req.Name, "name", 100);
        var key = KeyPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var row = await keys.CreateAsync(project.ProjectId, name, key[..VisiblePrefixLength], ApiKeyAuthenticator.HashKey(key), project.UserId!.Value, ct);
        return ApiResults.Created(new CreatedApiKey(row, key), AppJsonContext.Default.CreatedApiKey);
    }

    static async Task<IResult> Revoke(long id, ProjectContext project, ApiKeyRepository keys, ApiKeyAuthenticator authenticator, CancellationToken ct)
    {
        if (await keys.RevokeAsync(project.ProjectId, id, ct) is not { } hash) return ApiResults.NotFound("API key not found");
        authenticator.Invalidate(hash);
        return Results.NoContent();
    }
}
