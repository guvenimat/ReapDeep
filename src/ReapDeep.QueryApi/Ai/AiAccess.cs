using Dapper;
using Npgsql;
using ReapDeep.Analytics;

namespace ReapDeep.QueryApi.Ai;

public static class AiFeatures
{
    public const string ExperimentExplain = "experiment_explain";
    public const string InsightExplain = "insight_explain";
    public const string Query = "query";
    public const string Digest = "digest";

    public static readonly string[] All = [ExperimentExplain, InsightExplain, Query, Digest];

    public static List<string> Validate(List<string>? features)
    {
        var list = (features ?? []).Select(f => f?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
        var unknown = list.Where(f => !All.Contains(f)).ToList();
        Ensure.That(unknown.Count == 0, $"Unknown AI features: {string.Join(", ", unknown)} ({string.Join(" | ", All)})");
        return [.. All.Where(list.Contains)];
    }
}

public sealed record AiAccessState(long OrganizationId, string OrganizationName, bool PlatformEnabled, List<string> PlatformFeatures, bool CustomerEnabled,
    List<string> CustomerFeatures)
{
    public List<string> Effective => PlatformEnabled && CustomerEnabled
        ? [.. AiFeatures.All.Where(f => PlatformFeatures.Contains(f) && CustomerFeatures.Contains(f))]
        : [];

    public List<string> Allowed => PlatformEnabled ? [.. AiFeatures.All.Where(PlatformFeatures.Contains)] : [];
}

public sealed record AiAccessResponse(bool PlatformEnabled, List<string> Allowed, bool Enabled, List<string> Features, List<string> Effective, string? Model);
public sealed record AiAccessBody(bool? Enabled, List<string>? Features);
public sealed record PlatformOrganization(long Id, string Name, int Projects, int Members, bool AiEnabled, List<string> AiFeatures, bool CustomerEnabled,
    List<string> CustomerFeatures, List<string> Effective);
public sealed record PlatformOrganizationsResponse(List<PlatformOrganization> Organizations, string? Model);

public sealed class AiAccessRepository(NpgsqlDataSource db)
{
    public sealed record AccessRecord(long OrganizationId, string OrganizationName, bool PlatformEnabled, string PlatformFeatures, bool CustomerEnabled,
        string CustomerFeatures, int Projects, int Members)
    {
        public AiAccessState ToState() => new(OrganizationId, OrganizationName, PlatformEnabled, Split(PlatformFeatures), CustomerEnabled, Split(CustomerFeatures));
    }

    const string Select = """
        SELECT o.id AS organization_id, o.name AS organization_name,
               COALESCE(a.platform_enabled, false) AS platform_enabled,
               array_to_string(COALESCE(a.platform_features, '{}'::text[]), ',') AS platform_features,
               COALESCE(a.customer_enabled, true) AS customer_enabled,
               array_to_string(COALESCE(a.customer_features, '{experiment_explain,insight_explain,query,digest}'::text[]), ',') AS customer_features,
               (SELECT count(*)::int FROM projects p WHERE p.organization_id = o.id) AS projects,
               (SELECT count(*)::int FROM organization_members m WHERE m.organization_id = o.id) AS members
        FROM organizations o LEFT JOIN organization_ai a ON a.organization_id = o.id
        """;

    public async Task<AiAccessState?> ForProjectAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AccessRecord>(Select + " WHERE o.id = (SELECT organization_id FROM projects WHERE id = @Id)", new { Id = projectId });
        return row?.ToState();
    }

    public async Task<List<AccessRecord>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AccessRecord>(Select + " ORDER BY lower(o.name), o.id")).AsList();
    }

    public async Task<bool> SetPlatformAsync(long organizationId, bool enabled, string[] features, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("""
            INSERT INTO organization_ai (organization_id, platform_enabled, platform_features)
            SELECT id, @Enabled, @Features FROM organizations WHERE id = @Id
            ON CONFLICT (organization_id) DO UPDATE SET platform_enabled = EXCLUDED.platform_enabled,
                platform_features = EXCLUDED.platform_features, updated_at = now()
            """, new { Id = organizationId, Enabled = enabled, Features = features }) > 0;
    }

    public async Task SetCustomerAsync(long organizationId, bool enabled, string[] features, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO organization_ai (organization_id, customer_enabled, customer_features) VALUES (@Id, @Enabled, @Features)
            ON CONFLICT (organization_id) DO UPDATE SET customer_enabled = EXCLUDED.customer_enabled,
                customer_features = EXCLUDED.customer_features, updated_at = now()
            """, new { Id = organizationId, Enabled = enabled, Features = features });
    }

    public sealed record AuditRecord(long Id, string ActorEmail, long? OrganizationId, string OrganizationName, string Action, string BeforeState,
        string AfterState, DateTime CreatedAt);

    public async Task AuditAsync(string actor, long organizationId, string organizationName, string action, string before, string after, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO platform_audit (actor_email, organization_id, organization_name, action, before_state, after_state)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb)
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = organizationId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = organizationName });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = action });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = before });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = after });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<PlatformAuditEntry>> AuditLogAsync(int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuditRecord>("""
            SELECT id, actor_email, organization_id, organization_name, action, before_state::text AS before_state, after_state::text AS after_state, created_at
            FROM platform_audit ORDER BY created_at DESC, id DESC LIMIT @Limit
            """, new { Limit = limit });
        return [.. rows.Select(r => new PlatformAuditEntry(r.Id, r.ActorEmail, r.OrganizationId, r.OrganizationName, r.Action,
            System.Text.Json.JsonDocument.Parse(r.BeforeState).RootElement.Clone(), System.Text.Json.JsonDocument.Parse(r.AfterState).RootElement.Clone(), r.CreatedAt))];
    }

    static List<string> Split(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
