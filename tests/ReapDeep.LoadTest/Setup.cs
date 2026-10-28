using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using Npgsql;

namespace ReapDeep.LoadTest;

static class Setup
{
    public const long BenchProjectId = 2;
    public const long LoadTestProjectId = 3;
    public const string BenchPublicKey = "pk_benchmark";
    public const string LoadTestPublicKey = "pk_load_test";

    public static async Task Run(string pg, string benchKey)
    {
        await using var db = NpgsqlDataSource.Create(pg);
        await Exec(db, """
            INSERT INTO projects (id, organization_id, name, public_key) VALUES
                (2, 1, 'Benchmark Project', 'pk_benchmark'),
                (3, 1, 'Load Test Project', 'pk_load_test')
            ON CONFLICT (id) DO NOTHING
            """);
        await Exec(db, "SELECT setval('projects_id_seq', GREATEST((SELECT max(id) FROM projects), 1))");
        await Exec(db, "DELETE FROM api_keys WHERE project_id = 2");
        await using (var cmd = db.CreateCommand(
            "INSERT INTO api_keys (project_id, name, key_prefix, key_hash) VALUES (2, 'Benchmark read key', @prefix, @hash)"))
        {
            cmd.Parameters.AddWithValue("prefix", benchKey[..Math.Min(6, benchKey.Length)]);
            cmd.Parameters.AddWithValue("hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(benchKey))));
            await cmd.ExecuteNonQueryAsync();
        }
        Console.WriteLine("Projects 2 (benchmark) and 3 (load test) are ready.");
    }

    public static async Task Cleanup(string sr, string pg)
    {
        await using (var conn = new MySqlConnection(sr))
        {
            await conn.OpenAsync();
            foreach (var sql in new[]
            {
                "DELETE FROM analytics.events WHERE project_id IN (2, 3)",
                "DELETE FROM analytics.persons WHERE project_id IN (2, 3)",
                "DELETE FROM analytics.person_distinct_ids WHERE project_id IN (2, 3)",
            })
            {
                await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 600 };
                await cmd.ExecuteNonQueryAsync();
                Console.WriteLine($"StarRocks: {sql}");
            }
        }

        await using var db = NpgsqlDataSource.Create(pg);
        foreach (var sql in new[]
        {
            "DELETE FROM person_distinct_ids WHERE project_id IN (2, 3)",
            "DELETE FROM persons WHERE project_id IN (2, 3)",
            "DELETE FROM person_outbox WHERE (payload->>'project_id')::bigint IN (2, 3)",
            "DELETE FROM api_keys WHERE project_id IN (2, 3)",
            "DELETE FROM projects WHERE id IN (2, 3)",
        })
        {
            await Exec(db, sql);
            Console.WriteLine($"PostgreSQL: {sql}");
        }
    }

    static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync();
    }
}
