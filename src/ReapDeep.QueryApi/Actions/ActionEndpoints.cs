using System.Text.Json;
using Dapper;
using Npgsql;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Actions;

public sealed record ActionBody(string? Name, string? Description, List<string>? Events);
public sealed record ActionRow(long Id, string Name, string Description, List<string> Events, string? CreatedBy, DateTime UpdatedAt);
public sealed record ActionsResponse(List<ActionRow> Actions);

public static class ActionEndpoints
{
    public const int MaxEvents = 20;
    const int MaxActions = 200;

    public static void MapActions(this RouteGroupBuilder api)
    {
        api.MapGet("/actions", List);
        api.MapPost("/actions", Create).RequireUser();
        api.MapPut("/actions/{id:long}", Update).RequireUser();
        api.MapDelete("/actions/{id:long}", Delete).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, ActionRepository actions, CancellationToken ct) =>
        ApiResults.Ok(new ActionsResponse(await actions.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.ActionsResponse);

    static async Task<IResult> Create(ActionBody req, ProjectContext project, ActionRepository actions, CancellationToken ct)
    {
        var (name, description, events) = Validate(req);
        Ensure.That(await actions.CountAsync(project.ProjectId, ct) < MaxActions, $"A project can have at most {MaxActions} actions");
        return await actions.CreateAsync(project.ProjectId, name, description, events, project.UserId!.Value, ct) is { } row
            ? ApiResults.Created(row, AppJsonContext.Default.ActionRow)
            : ApiResults.Conflict($"An action named '{name}' already exists");
    }

    static async Task<IResult> Update(long id, ActionBody req, ProjectContext project, ActionRepository actions, CancellationToken ct)
    {
        var (name, description, events) = Validate(req);
        var current = await actions.NameAsync(project.ProjectId, id, ct);
        if (current is null) return ApiResults.NotFound("Action not found");
        if (!string.Equals(current, name, StringComparison.Ordinal) && await actions.CohortsUsingAsync(project.ProjectId, current, ct) is { Count: > 0 } users)
            return ApiResults.Conflict($"The action is used by the cohort{(users.Count > 1 ? "s" : "")} {string.Join(", ", users.Select(n => $"'{n}'"))}; it cannot be renamed while a cohort uses it");
        return await actions.UpdateAsync(project.ProjectId, id, name, description, events, ct) switch
        {
            { Found: false } => ApiResults.NotFound("Action not found"),
            { Row: { } row } => ApiResults.Ok(row, AppJsonContext.Default.ActionRow),
            _ => ApiResults.Conflict($"An action named '{name}' already exists"),
        };
    }

    static async Task<IResult> Delete(long id, ProjectContext project, ActionRepository actions, CancellationToken ct)
    {
        var name = await actions.NameAsync(project.ProjectId, id, ct);
        if (name is null) return ApiResults.NotFound("Action not found");
        if (await actions.CohortsUsingAsync(project.ProjectId, name, ct) is { Count: > 0 } names)
            return ApiResults.Conflict($"The action is used by the cohort{(names.Count > 1 ? "s" : "")} {string.Join(", ", names.Select(n => $"'{n}'"))}; remove it from the cohort first");
        return await actions.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.NotFound("Action not found");
    }

    static (string Name, string Description, string[] Events) Validate(ActionBody req)
    {
        var name = Ensure.NotBlank(req.Name, "name", 100);
        var description = req.Description?.Trim() ?? "";
        Ensure.That(description.Length <= 500, "description can be at most 500 characters");
        var events = (req.Events ?? []).Select(e => Ensure.NotBlank(e, "events", 200)).Distinct(StringComparer.Ordinal).ToArray();
        Ensure.That(events.Length is >= 1 and <= MaxEvents, $"events must contain 1-{MaxEvents} event names");
        return (name, description, events);
    }
}

public sealed class ActionRepository(NpgsqlDataSource db)
{
    internal sealed record ActionRecord(long Id, string Name, string Description, string Events, string? CreatedBy, DateTime UpdatedAt)
    {
        public ActionRow ToContract() => new(Id, Name, Description, ParseEvents(Events), CreatedBy, UpdatedAt);
    }

    internal sealed record ResolvedRecord(string Name, string Events);

    public readonly record struct UpdateResult(bool Found, ActionRow? Row);

    const string Select = """
        SELECT a.id, a.name, a.description, to_jsonb(a.events)::text AS events, u.email AS created_by, a.updated_at
        FROM actions a LEFT JOIN users u ON u.id = a.created_by
        """;

    public async Task<List<ActionRow>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ActionRecord>(Select + " WHERE a.project_id = @ProjectId ORDER BY lower(a.name)", new { ProjectId = projectId });
        return rows.Select(r => r.ToContract()).ToList();
    }

    public async Task<long> CountAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM actions WHERE project_id = @ProjectId", new { ProjectId = projectId });
    }

    public async Task<ActionRow?> CreateAsync(long projectId, string name, string description, string[] events, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long?>("""
            INSERT INTO actions (project_id, name, description, events, created_by)
            VALUES (@ProjectId, @Name, @Description, @Events, @CreatedBy)
            ON CONFLICT (project_id, name) DO NOTHING RETURNING id
            """, new { ProjectId = projectId, Name = name, Description = description, Events = events, CreatedBy = createdBy });
        return id is null ? null : await GetAsync(conn, id.Value);
    }

    public async Task<UpdateResult> UpdateAsync(long projectId, long id, string name, string description, string[] events, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        if (await conn.ExecuteScalarAsync<long?>("SELECT id FROM actions WHERE project_id = @ProjectId AND id = @Id", new { ProjectId = projectId, Id = id }) is null)
            return new UpdateResult(false, null);
        if (await conn.ExecuteScalarAsync<long?>("SELECT id FROM actions WHERE project_id = @ProjectId AND name = @Name AND id <> @Id",
                new { ProjectId = projectId, Name = name, Id = id }) is not null)
            return new UpdateResult(true, null);
        await conn.ExecuteAsync("""
            UPDATE actions SET name = @Name, description = @Description, events = @Events, updated_at = now()
            WHERE project_id = @ProjectId AND id = @Id
            """, new { ProjectId = projectId, Id = id, Name = name, Description = description, Events = events });
        return new UpdateResult(true, await GetAsync(conn, id));
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM actions WHERE project_id = @ProjectId AND id = @Id", new { ProjectId = projectId, Id = id }) > 0;
    }

    public async Task<string?> NameAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT name FROM actions WHERE project_id = $1 AND id = $2", conn);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = id });
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<List<string>> CohortsUsingAsync(long projectId, string actionName, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT c.name FROM cohorts c
            WHERE c.project_id = $1 AND c.deleted_at IS NULL AND c.kind = 'dynamic'
              AND EXISTS (
                SELECT 1 FROM jsonb_array_elements(COALESCE(c.definition->'groups', '[]'::jsonb)) g,
                              jsonb_array_elements(COALESCE(g->'conditions', '[]'::jsonb)) cond
                WHERE cond->>'action' = $2)
            ORDER BY c.name
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actionName });
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        return names;
    }

    public async Task<List<Series>> ApplyAsync(long projectId, List<Series> series, CancellationToken ct)
    {
        var found = await ResolveAsync(projectId, series.Select(s => s.Action), ct);
        return series.Select(s => s.Action is { Length: > 0 } a ? s with { Event = a, Events = Lookup(found, a) } : s with { Events = null }).ToList();
    }

    public async Task<List<FunnelStep>> ApplyAsync(long projectId, List<FunnelStep> steps, CancellationToken ct)
    {
        var found = await ResolveAsync(projectId, steps.Select(s => s.Action), ct);
        return steps.Select(s => s.Action is { Length: > 0 } a ? s with { Event = a, Events = Lookup(found, a) } : s with { Events = null }).ToList();
    }

    static List<string> Lookup(Dictionary<string, List<string>> found, string name) =>
        found.TryGetValue(name, out var events) ? events : throw new ValidationException($"Unknown action: '{name}'");

    public async Task<Dictionary<string, List<string>>> ResolveAsync(long projectId, IEnumerable<string?> names, CancellationToken ct)
    {
        var wanted = names.OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0) return [];
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ResolvedRecord>(
            "SELECT name, to_jsonb(events)::text AS events FROM actions WHERE project_id = @ProjectId AND name = ANY(@Names)",
            new { ProjectId = projectId, Names = wanted });
        return rows.ToDictionary(r => r.Name, r => ParseEvents(r.Events), StringComparer.Ordinal);
    }

    static async Task<ActionRow> GetAsync(NpgsqlConnection conn, long id) =>
        (await conn.QuerySingleAsync<ActionRecord>(Select + " WHERE a.id = @Id", new { Id = id })).ToContract();

    static List<string> ParseEvents(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList();
    }
}
