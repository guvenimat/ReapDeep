using System.Globalization;
using Dapper;
using Npgsql;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Annotations;

public sealed record AnnotationBody(string? Day, string? Content);
public sealed record AnnotationRow(long Id, string Day, string Content, string? CreatedBy, DateTime CreatedAt);
public sealed record AnnotationsResponse(List<AnnotationRow> Annotations);

public static class AnnotationEndpoints
{
    public const int MaxContentLength = 300;
    public const int MaxRangeDays = 800;
    const int DefaultRangeDays = 365;

    public static void MapAnnotations(this RouteGroupBuilder api)
    {
        api.MapGet("/annotations", List);
        api.MapPost("/annotations", Create).RequireUser();
        api.MapDelete("/annotations/{id:long}", Delete).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, AnnotationRepository annotations, string? from, string? to, CancellationToken ct)
    {
        var end = to is null ? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1) : Day(to, "to");
        var start = from is null ? end.AddDays(-DefaultRangeDays) : Day(from, "from");
        Ensure.That(start <= end, "from must not be after to");
        Ensure.That(end.DayNumber - start.DayNumber <= MaxRangeDays, $"The range can be at most {MaxRangeDays} days");
        return ApiResults.Ok(new AnnotationsResponse(await annotations.ListAsync(project.ProjectId, Text(start), Text(end), ct)),
            AppJsonContext.Default.AnnotationsResponse);
    }

    static async Task<IResult> Create(AnnotationBody req, ProjectContext project, AnnotationRepository annotations, CancellationToken ct)
    {
        var day = Day(req.Day, "day");
        var content = Ensure.NotBlank(req.Content, "content", MaxContentLength);
        var row = await annotations.CreateAsync(project.ProjectId, Text(day), content, project.UserId!.Value, ct);
        return ApiResults.Created(row, AppJsonContext.Default.AnnotationRow);
    }

    static async Task<IResult> Delete(long id, ProjectContext project, AnnotationRepository annotations, CancellationToken ct) =>
        await annotations.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.NotFound("Annotation not found");

    public static DateOnly Day(string? value, string name)
    {
        Ensure.That(DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day),
            $"{name} must be a date (YYYY-MM-DD)");
        Ensure.That(day.Year is >= 2000 and <= 2100, $"{name} must be between 2000 and 2100");
        return day;
    }

    static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed class AnnotationRepository(NpgsqlDataSource db)
{
    internal sealed record AnnotationRecord(long Id, string Day, string Content, string? CreatedBy, DateTime CreatedAt)
    {
        public AnnotationRow ToContract() => new(Id, Day, Content, CreatedBy, CreatedAt);
    }

    const string Select = """
        SELECT a.id, a.day::text AS day, a.content, u.email AS created_by, a.created_at
        FROM annotations a LEFT JOIN users u ON u.id = a.created_by
        """;

    public async Task<List<AnnotationRow>> ListAsync(long projectId, string from, string to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AnnotationRecord>(Select + """
             WHERE a.project_id = @ProjectId AND a.day >= @From::date AND a.day <= @To::date ORDER BY a.day, a.id LIMIT 500
            """, new { ProjectId = projectId, From = from, To = to });
        return rows.Select(r => r.ToContract()).ToList();
    }

    public async Task<AnnotationRow> CreateAsync(long projectId, string day, string content, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO annotations (project_id, day, content, created_by) VALUES (@ProjectId, @Day::date, @Content, @CreatedBy) RETURNING id
            """, new { ProjectId = projectId, Day = day, Content = content, CreatedBy = createdBy });
        return (await conn.QuerySingleAsync<AnnotationRecord>(Select + " WHERE a.id = @Id", new { Id = id })).ToContract();
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM annotations WHERE project_id = @ProjectId AND id = @Id", new { ProjectId = projectId, Id = id }) > 0;
    }
}
