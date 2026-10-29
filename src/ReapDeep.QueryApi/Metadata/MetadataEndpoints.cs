using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Metadata;

public static class MetadataEndpoints
{
    public static void MapMetadata(this RouteGroupBuilder api) => api.MapGet("/metadata", Get);

    static async Task<IResult> Get(ProjectContext project, MetadataRepository metadata, CancellationToken ct) =>
        ApiResults.Ok(await metadata.GetAsync(project.ProjectId, ct), AppJsonContext.Default.MetadataResponse);
}
