using Dapper;
using Npgsql;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Notifications;

public static class NotificationKinds
{
    public const string AlertFired = "alert_fired";
    public const string AlertResolved = "alert_resolved";
    public const string AlertError = "alert_error";
    public const string Note = "note";
    public const string ExperimentResult = "experiment_result";

    public static readonly string[] All = [AlertFired, AlertResolved, AlertError, Note, ExperimentResult];
}

public sealed record Notification(long Id, string Kind, string Title, string Body, string Link, DateTime CreatedAt, bool Unread);
public sealed record NotificationsResponse(List<Notification> Notifications, int Unread);
public sealed record NotificationPreference(string Kind, bool Enabled);
public sealed record NotificationPreferencesResponse(List<NotificationPreference> Preferences);
public sealed record NotificationPreferenceBody(string? Kind, bool? Enabled);

public sealed class NotificationRepository(NpgsqlDataSource db, ILogger<NotificationRepository> log)
{
    public const int KeptPerProject = 200;
    const int MaxTitle = 200;
    const int MaxBody = 500;

    public sealed record NotificationRecord(long Id, string Kind, string Title, string Body, string Link, DateTime CreatedAt, bool Unread);

    public async Task AddAsync(long projectId, string kind, string title, string body, string link, CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using (var insert = new NpgsqlCommand("INSERT INTO notifications (project_id, kind, title, body, link) VALUES ($1, $2, $3, $4, $5)", conn))
            {
                insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Cut(title, MaxTitle) });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Cut(body, MaxBody) });
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = link });
                await insert.ExecuteNonQueryAsync(ct);
            }
            await using var trim = new NpgsqlCommand("""
                DELETE FROM notifications WHERE project_id = $1
                  AND id < (SELECT min(id) FROM (SELECT id FROM notifications WHERE project_id = $1 ORDER BY id DESC LIMIT $2) k)
                """, conn);
            trim.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
            trim.Parameters.Add(new NpgsqlParameter<int> { TypedValue = KeptPerProject });
            await trim.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Project {Project}: notification {Kind} could not be stored", projectId, kind);
        }
    }

    public async Task<NotificationsResponse> ListAsync(long projectId, long userId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<NotificationRecord>("""
            SELECT n.id, n.kind, n.title, n.body, n.link, n.created_at, n.id > COALESCE(r.last_read_id, 0) AS unread
            FROM notifications n LEFT JOIN notification_reads r ON r.project_id = n.project_id AND r.user_id = @UserId
            WHERE n.project_id = @ProjectId
              AND NOT EXISTS (SELECT 1 FROM notification_mutes m
                              WHERE m.user_id = @UserId AND m.project_id = n.project_id AND m.kind = n.kind)
            ORDER BY n.id DESC LIMIT @Limit
            """, new { ProjectId = projectId, UserId = userId, Limit = limit })).AsList();
        var unread = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM notifications n
            WHERE n.project_id = @ProjectId AND n.id > COALESCE((SELECT last_read_id FROM notification_reads WHERE project_id = @ProjectId AND user_id = @UserId), 0)
              AND NOT EXISTS (SELECT 1 FROM notification_mutes m
                              WHERE m.user_id = @UserId AND m.project_id = n.project_id AND m.kind = n.kind)
            """, new { ProjectId = projectId, UserId = userId });
        return new NotificationsResponse([.. rows.Select(r => new Notification(r.Id, r.Kind, r.Title, r.Body, r.Link, r.CreatedAt, r.Unread))], unread);
    }

    public async Task<NotificationPreferencesResponse> PreferencesAsync(long projectId, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var muted = (await conn.QueryAsync<string>("""
            SELECT kind FROM notification_mutes WHERE project_id = @ProjectId AND user_id = @UserId
            """, new { ProjectId = projectId, UserId = userId })).ToHashSet(StringComparer.Ordinal);
        return new NotificationPreferencesResponse([.. NotificationKinds.All.Select(k => new NotificationPreference(k, !muted.Contains(k)))]);
    }

    public async Task SetPreferenceAsync(long projectId, long userId, string kind, bool enabled, CancellationToken ct)
    {
        Ensure.That(NotificationKinds.All.Contains(kind), $"Unknown notification kind: {kind}");
        await using var conn = await db.OpenConnectionAsync(ct);
        if (enabled)
        {
            await using var delete = new NpgsqlCommand(
                "DELETE FROM notification_mutes WHERE user_id = $1 AND project_id = $2 AND kind = $3", conn);
            delete.Parameters.Add(new NpgsqlParameter<long> { TypedValue = userId });
            delete.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
            delete.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
            await delete.ExecuteNonQueryAsync(ct);
            return;
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO notification_mutes (user_id, project_id, kind) VALUES ($1, $2, $3)
            ON CONFLICT (user_id, project_id, kind) DO NOTHING
            """, conn);
        insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = userId });
        insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        await insert.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkReadAsync(long projectId, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO notification_reads (user_id, project_id, last_read_id)
            SELECT $1, $2, COALESCE(max(id), 0) FROM notifications WHERE project_id = $2
            ON CONFLICT (user_id, project_id) DO UPDATE SET last_read_id = GREATEST(notification_reads.last_read_id, EXCLUDED.last_read_id)
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = userId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

public static class NotificationEndpoints
{
    public static void MapNotifications(this RouteGroupBuilder api)
    {
        api.MapGet("/notifications", List).RequireUser();
        api.MapPost("/notifications/read", Read).RequireUser();
        api.MapGet("/notifications/preferences", Preferences).RequireUser();
        api.MapPut("/notifications/preferences", SetPreference).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, NotificationRepository notifications, int? limit, CancellationToken ct) =>
        ApiResults.Ok(await notifications.ListAsync(project.ProjectId, project.UserId!.Value, Math.Clamp(limit ?? 30, 1, 100), ct),
            AppJsonContext.Default.NotificationsResponse);

    static async Task<IResult> Read(ProjectContext project, NotificationRepository notifications, CancellationToken ct)
    {
        await notifications.MarkReadAsync(project.ProjectId, project.UserId!.Value, ct);
        return Results.NoContent();
    }

    static async Task<IResult> Preferences(ProjectContext project, NotificationRepository notifications, CancellationToken ct) =>
        ApiResults.Ok(await notifications.PreferencesAsync(project.ProjectId, project.UserId!.Value, ct),
            AppJsonContext.Default.NotificationPreferencesResponse);

    static async Task<IResult> SetPreference(NotificationPreferenceBody req, ProjectContext project, NotificationRepository notifications, CancellationToken ct)
    {
        await notifications.SetPreferenceAsync(project.ProjectId, project.UserId!.Value, Ensure.NotBlank(req.Kind, "kind"),
            req.Enabled ?? throw new ValidationException("enabled is required"), ct);
        return Results.NoContent();
    }
}
