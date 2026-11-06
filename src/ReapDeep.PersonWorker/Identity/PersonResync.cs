using System.Globalization;
using Npgsql;

namespace ReapDeep.PersonWorker.Identity;

public static class PersonResync
{
    const string QueuePersons = """
        INSERT INTO person_outbox (topic, key, payload)
        SELECT 'person_updates', id::text, jsonb_build_object(
            'project_id', project_id, 'person_id', id::text, 'properties', properties,
            'is_identified', is_identified, 'is_deleted', is_deleted, 'version', version,
            'created_at', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'),
            'updated_at', to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
        FROM persons WHERE $1::bigint IS NULL OR project_id = $1::bigint
        """;

    const string QueueDistinctIds = """
        INSERT INTO person_outbox (topic, key, payload)
        SELECT 'person_distinct_id_updates', distinct_id, jsonb_build_object(
            'project_id', project_id, 'distinct_id', distinct_id, 'person_id', person_id::text,
            'version', version, 'is_deleted', false,
            'updated_at', to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
        FROM person_distinct_ids WHERE $1::bigint IS NULL OR project_id = $1::bigint
        """;

    public static bool TryParseArgs(string[] args, out long? projectId)
    {
        projectId = null;
        if (args is not ["resync-persons", .. var rest]) return false;
        if (rest is ["--project", var id] && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var p)) projectId = p;
        else if (rest.Length > 0) throw new ArgumentException("Usage: resync-persons [--project <id>]");
        return true;
    }

    public static async Task<int> RunAsync(string? connectionString, long? projectId)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("ConnectionStrings__Postgres is not set");
            return 1;
        }
        await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).Build();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var persons = await QueueAsync(conn, tx, QueuePersons, projectId);
        var distinctIds = await QueueAsync(conn, tx, QueueDistinctIds, projectId);
        await tx.CommitAsync();
        var scope = projectId is { } id ? $"project {id}" : "all projects";
        Console.WriteLine($"Queued {persons} persons and {distinctIds} distinct ids of {scope} for StarRocks; the running person worker publishes them");
        return 0;
    }

    static async Task<int> QueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, long? projectId)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { Parameters = { new() { Value = projectId is { } id ? id : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint } } };
        return await cmd.ExecuteNonQueryAsync();
    }
}
