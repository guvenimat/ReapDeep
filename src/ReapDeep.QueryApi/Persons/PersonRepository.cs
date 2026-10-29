using Dapper;
using Npgsql;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Persons;

public sealed class PersonRepository(NpgsqlDataSource db)
{
    internal sealed record PersonRecord(Guid Id, bool IsIdentified, DateTime CreatedAt, string Properties, string[] DistinctIds)
    {
        public PersonRow ToContract() => new(Id.ToString(), IsIdentified, CreatedAt, DistinctIds, JsonText.Parse(Properties));
    }

    const string Select = """
        SELECT p.id, p.is_identified, p.created_at, p.properties::text AS properties,
               COALESCE((SELECT array_agg(d.distinct_id ORDER BY d.distinct_id) FROM person_distinct_ids d WHERE d.person_id = p.id), '{}') AS distinct_ids
        FROM persons p
        WHERE p.project_id = @ProjectId AND NOT p.is_deleted
        """;

    public async Task<List<PersonRow>> ListAsync(long projectId, string? search, Guid? before, bool? identified, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PersonRecord>(Select + """

              AND (@Before::uuid IS NULL OR p.id < @Before::uuid)
              AND (@Identified::boolean IS NULL OR p.is_identified = @Identified::boolean)
              AND (@Pattern::text IS NULL OR p.properties::text ILIKE @Pattern::text
                   OR EXISTS (SELECT 1 FROM person_distinct_ids d WHERE d.person_id = p.id AND d.distinct_id ILIKE @Pattern::text))
            ORDER BY p.id DESC LIMIT @Limit
            """, new
        {
            ProjectId = projectId,
            Before = before,
            Identified = identified,
            Pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + EscapeLike(search) + "%",
            Limit = limit,
        });
        return rows.Select(r => r.ToContract()).ToList();
    }

    public async Task<PersonRow?> GetAsync(long projectId, Guid personId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PersonRecord>(Select + " AND p.id = @PersonId", new { ProjectId = projectId, PersonId = personId });
        return row?.ToContract();
    }

    public async Task<List<string>> DistinctIdsAsync(long projectId, Guid personId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var ids = await conn.QueryAsync<string>(
            "SELECT distinct_id FROM person_distinct_ids WHERE project_id = @ProjectId AND person_id = @PersonId LIMIT @Limit",
            new { ProjectId = projectId, PersonId = personId, Limit = limit });
        return ids.AsList();
    }

    static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
