using System.Text.Json;
using Dapper;
using Npgsql;

namespace ReapDeep.Recommender.Recommendations;

public sealed class PersonHistoryStore(NpgsqlDataSource db)
{
    public async Task<List<HistoryItem>> GetAsync(long projectId, string distinctId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var json = await conn.QuerySingleOrDefaultAsync<string>("""
            SELECT h.items::text FROM person_item_history h
            WHERE h.project_id = @ProjectId AND h.person_key = COALESCE(
                (SELECT d.person_id::text FROM person_distinct_ids d WHERE d.project_id = @ProjectId AND d.distinct_id = @DistinctId),
                @DistinctId)
            """, new { ProjectId = projectId, DistinctId = distinctId });
        return json is null ? [] : Parse(json);
    }

    public static List<HistoryItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new HistoryItem(e.GetProperty("item_id").GetString()!, e.GetProperty("weight").GetInt32(), e.GetProperty("at").GetInt64()))
            .ToList();
    }
}
