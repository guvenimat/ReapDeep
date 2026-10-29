using Dapper;
using Npgsql;
using ReapDeep.QueryApi.Analytics.Sql;

namespace ReapDeep.QueryApi.Metadata;

public sealed class MetadataRepository(NpgsqlDataSource db)
{
    internal sealed record PropertyRecord(string Scope, string Name, string? PropertyType, DateTime LastSeenAt);

    public async Task<MetadataResponse> GetAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var events = await conn.QueryAsync<EventDefinition>(
            "SELECT name, last_seen_at FROM event_definitions WHERE project_id = @ProjectId ORDER BY last_seen_at DESC, name LIMIT 2000",
            new { ProjectId = projectId });
        var properties = (await conn.QueryAsync<PropertyRecord>("""
            SELECT scope, name, property_type, last_seen_at FROM property_definitions
            WHERE project_id = @ProjectId AND column_name IS NULL ORDER BY last_seen_at DESC, name LIMIT 5000
            """, new { ProjectId = projectId })).AsList();

        List<PropertyDefinition> Of(string scope) =>
            properties.Where(p => p.Scope == scope).Select(p => new PropertyDefinition(p.Name, p.PropertyType, p.LastSeenAt)).ToList();

        var columns = ColumnMapper.EventColumns.Select(c => c.StartsWith("utm_", StringComparison.Ordinal) ? c : "$" + c).Order().ToList();
        return new MetadataResponse(events.AsList(), columns, Of("event"), Of("person"));
    }
}
