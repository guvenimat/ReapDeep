using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Alerts;

public sealed class AlertRepository(NpgsqlDataSource db)
{
    internal sealed record AlertRecord(
        long Id, long ProjectId, string TimeZone, string Name, long InsightId, string InsightName, string Condition, double Threshold,
        string WindowUnit, string WebhookUrl, string Secret, bool Enabled, string State, double? LastValue, double? PreviousValue,
        string? LastError, DateTime? LastCheckedAt, DateTime NextCheckAt, string? CreatedBy, DateTime CreatedAt)
    {
        public AlertRow ToContract(bool withSecret) => new(Id, Name, InsightId, InsightName, Condition, Threshold, WindowUnit, WebhookUrl,
            withSecret ? Secret : null, Enabled, State, LastValue, PreviousValue, LastError, LastCheckedAt, NextCheckAt, CreatedBy, CreatedAt);
    }

    internal sealed record AlertEventRecord(long Id, string Kind, double? Value, double? PreviousValue, string Message, int? ResponseStatus,
        string? DeliveryError, DateTime CreatedAt)
    {
        public AlertEventRow ToContract() => new(Id, Kind, Value, PreviousValue, Message, ResponseStatus, DeliveryError, CreatedAt);
    }

    const string Select = """
        SELECT a.id, a.project_id, p.timezone AS time_zone, a.name, a.insight_id, i.name AS insight_name, a.condition, a.threshold,
               a.window_unit, a.webhook_url, a.secret, a.enabled, a.state, a.last_value, a.previous_value, a.last_error,
               a.last_checked_at, a.next_check_at, u.email AS created_by, a.created_at
        FROM alerts a
        JOIN projects p ON p.id = a.project_id
        JOIN insights i ON i.id = a.insight_id
        LEFT JOIN users u ON u.id = a.created_by
        """;

    internal async Task<List<AlertRecord>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AlertRecord>(Select + " WHERE a.project_id = @ProjectId ORDER BY a.created_at DESC", new { ProjectId = projectId })).AsList();
    }

    internal async Task<AlertRecord?> GetAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AlertRecord>(Select + " WHERE a.project_id = @ProjectId AND a.id = @Id", new { ProjectId = projectId, Id = id });
    }

    public async Task<long> CreateAsync(long projectId, string name, long insightId, string condition, double threshold, string window,
        string webhookUrl, string secret, bool enabled, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO alerts (project_id, name, insight_id, condition, threshold, window_unit, webhook_url, secret, enabled, created_by)
            VALUES (@ProjectId, @Name, @InsightId, @Condition, @Threshold, @Window, @WebhookUrl, @Secret, @Enabled, @CreatedBy) RETURNING id
            """, new { ProjectId = projectId, Name = name, InsightId = insightId, Condition = condition, Threshold = threshold, Window = window,
            WebhookUrl = webhookUrl, Secret = secret, Enabled = enabled, CreatedBy = createdBy });
    }

    public async Task<bool> UpdateAsync(long projectId, long id, string name, long insightId, string condition, double threshold, string window,
        string webhookUrl, bool enabled, bool rulesChanged, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE alerts SET name = @Name, insight_id = @InsightId, condition = @Condition, threshold = @Threshold, window_unit = @Window,
                   webhook_url = @WebhookUrl, enabled = @Enabled, updated_at = now(),
                   next_check_at = CASE WHEN @RulesChanged THEN now() ELSE next_check_at END
            WHERE id = @Id AND project_id = @ProjectId
            """, new { Name = name, InsightId = insightId, Condition = condition, Threshold = threshold, Window = window, WebhookUrl = webhookUrl,
            Enabled = enabled, RulesChanged = rulesChanged, Id = id, ProjectId = projectId }) > 0;
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM alerts WHERE id = @Id AND project_id = @ProjectId", new { Id = id, ProjectId = projectId }) > 0;
    }

    public async Task<List<AlertEventRow>> EventsAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AlertEventRecord>("""
            SELECT id, kind, value, previous_value, message, response_status, delivery_error, created_at
            FROM alert_events WHERE alert_id = @Id AND project_id = @ProjectId ORDER BY created_at DESC, id DESC LIMIT 50
            """, new { Id = id, ProjectId = projectId });
        return rows.Select(r => r.ToContract()).ToList();
    }

    internal async Task<List<AlertRecord>> ClaimDueAsync(int limit, DateTime leaseUntil, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var ids = (await conn.QueryAsync<long>("""
            UPDATE alerts SET next_check_at = @LeaseUntil
            WHERE id IN (SELECT id FROM alerts WHERE enabled AND next_check_at <= now() ORDER BY next_check_at LIMIT @Limit FOR UPDATE SKIP LOCKED)
            RETURNING id
            """, new { Limit = limit, LeaseUntil = leaseUntil })).ToArray();
        if (ids.Length == 0) return [];
        return (await conn.QueryAsync<AlertRecord>(Select + " WHERE a.id = ANY(@Ids)", new { Ids = ids })).AsList();
    }

    public async Task SaveResultAsync(long id, string state, double? value, double? previous, string? error, DateTime nextCheckAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE alerts SET state = @State, last_value = @Value, previous_value = @Previous, last_error = @Error,
                   last_checked_at = now(), next_check_at = @NextCheckAt
            WHERE id = @Id
            """, new { State = state, Value = value, Previous = previous, Error = error, NextCheckAt = nextCheckAt, Id = id });
    }

    public async Task<AlertEventRow> AddEventAsync(long alertId, long projectId, string kind, double? value, double? previous, string message,
        int? responseStatus, string? deliveryError, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<AlertEventRecord>("""
            INSERT INTO alert_events (alert_id, project_id, kind, value, previous_value, message, response_status, delivery_error)
            VALUES (@AlertId, @ProjectId, @Kind, @Value, @Previous, @Message, @ResponseStatus, @DeliveryError)
            RETURNING id, kind, value, previous_value, message, response_status, delivery_error, created_at
            """, new { AlertId = alertId, ProjectId = projectId, Kind = kind, Value = value, Previous = previous, Message = message,
            ResponseStatus = responseStatus, DeliveryError = deliveryError });
        return row.ToContract();
    }
}
