using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using Npgsql;
using ReapDeep.Migrations;

var postgres = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? throw new InvalidOperationException("ConnectionStrings__Postgres is not set");
var starRocks = Environment.GetEnvironmentVariable("ConnectionStrings__StarRocksAdmin");
var migrations = MigrationCatalog.Load(typeof(MigrationCatalog).Assembly);

await using var pg = new NpgsqlSlimDataSourceBuilder(postgres).Build();
await using var conn = await Retry("PostgreSQL", () => pg.OpenConnectionAsync().AsTask());
await Exec(conn, "SELECT pg_advisory_lock(7130)");
await Exec(conn, """
    CREATE TABLE IF NOT EXISTS schema_migrations (
        target      TEXT        NOT NULL,
        version     TEXT        NOT NULL,
        name        TEXT        NOT NULL,
        applied_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
        PRIMARY KEY (target, version)
    )
    """);

await Baseline(conn, MigrationCatalog.Postgres, "SELECT to_regclass('public.projects') IS NOT NULL");
foreach (var m in MigrationCatalog.Pending(migrations, await Applied(conn, MigrationCatalog.Postgres), MigrationCatalog.Postgres))
{
    await using var tx = await conn.BeginTransactionAsync();
    await using (var cmd = new NpgsqlCommand(m.Sql, conn, tx)) await cmd.ExecuteNonQueryAsync();
    await Record(conn, tx, m);
    await tx.CommitAsync();
    Console.WriteLine($"postgres: applied {m.Version}_{m.Name}");
}
await SeedReadKey(conn, Environment.GetEnvironmentVariable("READ_API_KEY"));

if (!string.IsNullOrWhiteSpace(starRocks))
{
    await using var sr = await Retry("StarRocks", async () =>
    {
        var c = new MySqlConnection(starRocks);
        await c.OpenAsync();
        return c;
    });
    var exists = await Scalar(sr, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'analytics' AND table_name = 'events'") > 0;
    if (exists && (await Applied(conn, MigrationCatalog.StarRocks)).Count == 0)
        foreach (var m in migrations.Where(m => m.Target == MigrationCatalog.StarRocks && m.Version is "0001" or "0002"))
            await Record(conn, null, m, "starrocks: existing schema, marked");
    foreach (var m in MigrationCatalog.Pending(migrations, await Applied(conn, MigrationCatalog.StarRocks), MigrationCatalog.StarRocks))
    {
        foreach (var statement in MigrationCatalog.Statements(m.Sql))
            await using (var cmd = new MySqlCommand(statement, sr)) await cmd.ExecuteNonQueryAsync();
        await Record(conn, null, m);
        Console.WriteLine($"starrocks: applied {m.Version}_{m.Name}");
    }
    foreach (var statement in StarRocksUsers.Statements(Environment.GetEnvironmentVariable))
        await using (var cmd = new MySqlCommand(statement, sr)) await cmd.ExecuteNonQueryAsync();
    Console.WriteLine("starrocks: users and grants are up to date");
}

await Exec(conn, "SELECT pg_advisory_unlock(7130)");
Console.WriteLine("Migrations are up to date");
return 0;

static async Task Exec(NpgsqlConnection conn, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<long> Scalar(MySqlConnection conn, string sql)
{
    await using var cmd = new MySqlCommand(sql, conn);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

static async Task<HashSet<string>> Applied(NpgsqlConnection conn, string target)
{
    var versions = new HashSet<string>(StringComparer.Ordinal);
    await using var cmd = new NpgsqlCommand("SELECT version FROM schema_migrations WHERE target = $1", conn) { Parameters = { new() { Value = target } } };
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) versions.Add(reader.GetString(0));
    return versions;
}

static async Task Record(NpgsqlConnection conn, NpgsqlTransaction? tx, Migration m, string? note = null)
{
    await using var cmd = new NpgsqlCommand("INSERT INTO schema_migrations (target, version, name) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING", conn, tx)
    {
        Parameters = { new() { Value = m.Target }, new() { Value = m.Version }, new() { Value = m.Name } },
    };
    await cmd.ExecuteNonQueryAsync();
    if (note is not null) Console.WriteLine($"{note} {m.Version}_{m.Name} as applied");
}

static async Task Baseline(NpgsqlConnection conn, string target, string existsSql)
{
    await using var count = new NpgsqlCommand("SELECT count(*) FROM schema_migrations WHERE target = $1", conn) { Parameters = { new() { Value = target } } };
    if (Convert.ToInt64(await count.ExecuteScalarAsync()) > 0) return;
    await using var exists = new NpgsqlCommand(existsSql, conn);
    if (await exists.ExecuteScalarAsync() is not true) return;
    await Record(conn, null, new Migration(target, "0001", "metadata", ""), $"{target}: existing schema, marked");
}

static async Task SeedReadKey(NpgsqlConnection conn, string? key)
{
    if (string.IsNullOrWhiteSpace(key)) return;
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    await using var cmd = new NpgsqlCommand("""
        INSERT INTO api_keys (project_id, name, key_prefix, key_hash)
        SELECT 1, 'Local read key', left($1, 6), $2
        WHERE EXISTS (SELECT 1 FROM projects WHERE id = 1) AND NOT EXISTS (SELECT 1 FROM api_keys WHERE key_hash = $2)
        """, conn) { Parameters = { new() { Value = key }, new() { Value = hash } } };
    if (await cmd.ExecuteNonQueryAsync() > 0) Console.WriteLine("postgres: added the local read API key");
}

static async Task<T> Retry<T>(string what, Func<Task<T>> open)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            return await open();
        }
        catch (Exception ex) when (ex is NpgsqlException or MySqlException && attempt < 60)
        {
            Console.WriteLine($"{what} is not ready ({ex.Message}), retrying");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
