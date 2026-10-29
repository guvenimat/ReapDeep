using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Persons;

public static class PersonEndpoints
{
    const int DefaultLimit = 50;
    const int MaxLimit = 200;

    public static void MapPersons(this RouteGroupBuilder api)
    {
        api.MapGet("/persons", List);
        api.MapGet("/persons/{personId}", Get);
    }

    static async Task<IResult> List(ProjectContext project, PersonRepository persons, string? search, int? limit, string? cursor, bool? identified,
        CancellationToken ct)
    {
        var n = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        Guid? before = null;
        if (cursor is not null)
        {
            Ensure.That(Guid.TryParse(cursor, out var c), "Invalid cursor");
            before = c;
        }
        var rows = await persons.ListAsync(project.ProjectId, search, before, identified, n, ct);
        return ApiResults.Ok(new PersonsResponse(rows, rows.Count == n ? rows[^1].Id : null), AppJsonContext.Default.PersonsResponse);
    }

    static async Task<IResult> Get(ProjectContext project, PersonRepository persons, string personId, CancellationToken ct) =>
        Guid.TryParse(personId, out var id) && await persons.GetAsync(project.ProjectId, id, ct) is { } person
            ? ApiResults.Ok(person, AppJsonContext.Default.PersonRow)
            : ApiResults.NotFound("Person not found");
}
