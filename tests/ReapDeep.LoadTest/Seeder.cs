using System.Diagnostics;
using MySqlConnector;
using Npgsql;

namespace ReapDeep.LoadTest;

static class Seeder
{
    const int VisitorsPerDay = 200_000;
    const int VisitorShiftPerDay = 20_000;
    const int IdentifiedSecondIdOffset = 400_000;

    public static async Task Run(string sr, string pg, long totalEvents, int days, int persons, bool reportOnly)
    {
        await using var conn = new MySqlConnection(sr);
        await conn.OpenAsync();
        await Exec(conn, "SET query_timeout = 3600");

        double? eventsSeconds = null, personsSeconds = null;
        if (!reportOnly)
        {
            var existing = Convert.ToInt64(await Scalar(conn, "SELECT count(*) FROM analytics.events WHERE project_id = 2"));
            if (existing > 0) throw new InvalidOperationException($"Project 2 already has {existing:N0} events; run cleanup first.");

            var perDay = totalEvents / days;
            var firstDay = DateTime.UtcNow.Date.AddDays(-(days - 1));
            var total = Stopwatch.StartNew();
            Console.WriteLine($"Seeding {totalEvents:N0} events over {days} days ({perDay:N0}/day) into project 2...");
            for (var d = 0; d < days; d++)
            {
                var sw = Stopwatch.StartNew();
                await Exec(conn, EventsSql(d, firstDay.AddDays(d), perDay));
                Console.WriteLine($"  day {d + 1}/{days} {firstDay.AddDays(d):yyyy-MM-dd}: {sw.Elapsed.TotalSeconds:0.0}s");
            }
            eventsSeconds = total.Elapsed.TotalSeconds;

            var sw2 = Stopwatch.StartNew();
            await Exec(conn, PersonsSql(persons));
            await Exec(conn, DistinctIdsSql(persons));
            await SeedPostgres(pg, persons);
            personsSeconds = sw2.Elapsed.TotalSeconds;
        }

        var projectRows = Convert.ToInt64(await Scalar(conn, "SELECT count(*) FROM analytics.events WHERE project_id = 2"));
        var allRows = Convert.ToInt64(await Scalar(conn, "SELECT count(*) FROM analytics.events"));
        var partitions = Convert.ToInt64(await Scalar(conn, "SELECT count(DISTINCT date_trunc('day', timestamp)) FROM analytics.events WHERE project_id = 2"));
        var mappedIds = Convert.ToInt64(await Scalar(conn, "SELECT count(*) FROM analytics.person_distinct_ids WHERE project_id = 2"));
        var personRows = Convert.ToInt64(await Scalar(conn, "SELECT count(*) FROM analytics.persons WHERE project_id = 2"));
        var size = await TableSize(conn);
        var bytes = ParseSize(size);

        Report.Heading("100M seed (StarRocks)");
        Report.Table(
            ["Project 2 events", "Days (partitions)", "Identified persons", "Distinct ids mapped", "Event seed time", "Person seed time", "events rows (all projects)", "events size on disk (ZSTD)", "Bytes per row", "Per daily partition"],
            [[
                Stats.N(projectRows), partitions.ToString(), Stats.N(personRows), Stats.N(mappedIds),
                eventsSeconds is { } es ? $"{Stats.F(es, "0")} s" : "-", personsSeconds is { } ps ? $"{Stats.F(ps, "0")} s" : "-",
                Stats.N(allRows), size, allRows == 0 ? "-" : Stats.F(bytes / allRows, "0.0"),
                partitions == 0 ? "-" : $"{Stats.F(bytes / partitions / 1_048_576, "0")} MB",
            ]]);
    }

    static string EventsSql(int d, DateTime day, long count) => $"""
        INSERT INTO analytics.events (project_id, event, timestamp, distinct_id, event_id, session_id, ip,
            current_url, host, pathname, referrer, referring_domain, utm_source, utm_medium, utm_campaign,
            browser, browser_version, os, os_version, device_type, screen_width, screen_height, lib, lib_version, properties, inserted_at)
        SELECT 2,
            CASE WHEN r < 50 THEN '$pageview' WHEN r < 75 THEN '$autocapture_click' WHEN r < 90 THEN 'product_viewed' WHEN r < 95 THEN 'signup' ELSE 'purchase' END,
            ts, did, concat('b{d}-', i), concat(did, '-{d}-', h4 % 3), concat('10.', h5 % 256, '.', h4 % 256, '.', h6 % 256),
            concat('https://shop.example.com', path), 'shop.example.com', path,
            CASE WHEN h5 % 10 < 5 THEN '$direct' WHEN h5 % 10 < 8 THEN 'https://www.google.com/' ELSE 'https://t.co/x' END,
            CASE WHEN h5 % 10 < 5 THEN '$direct' WHEN h5 % 10 < 8 THEN 'www.google.com' ELSE 't.co' END,
            CASE WHEN h6 % 10 < 2 THEN 'google' WHEN h6 % 10 < 3 THEN 'newsletter' ELSE NULL END,
            CASE WHEN h6 % 10 < 2 THEN 'cpc' WHEN h6 % 10 < 3 THEN 'email' ELSE NULL END,
            CASE WHEN h6 % 10 < 3 THEN 'autumn_sale' ELSE NULL END,
            CASE WHEN dev = 0 THEN element_at(['Chrome', 'Chrome', 'Edge', 'Firefox', 'Safari'], h7 % 5 + 1)
                 ELSE element_at(['Chrome', 'Safari', 'Safari', 'Samsung Internet'], h7 % 4 + 1) END,
            '130.0',
            CASE WHEN dev = 0 THEN element_at(['Windows', 'Windows', 'macOS', 'Linux'], h7 % 4 + 1)
                 ELSE element_at(['Android', 'iOS'], h7 % 2 + 1) END,
            CASE WHEN dev = 0 THEN '10' ELSE '14' END,
            CASE WHEN dev = 0 THEN 'Desktop' WHEN dev = 1 THEN 'Mobile' ELSE 'Tablet' END,
            CASE WHEN dev = 0 THEN 1920 ELSE 390 END, CASE WHEN dev = 0 THEN 1080 ELSE 844 END,
            'web', '0.1.0',
            json_object('plan', element_at(['free', 'pro', 'enterprise'], h4 % 3 + 1), 'ab_test', CASE WHEN h5 % 2 = 0 THEN 'control' ELSE 'variant_b' END, 'cart_value', (h6 % 50000) / 100),
            ts
        FROM (
            SELECT i, r, h4, h5, h6, h7,
                seconds_add('{day:yyyy-MM-dd} 00:00:00', h2 % 86400) AS ts,
                concat('v', (h3 % {VisitorsPerDay} + {d * VisitorShiftPerDay})) AS did,
                element_at(['/', '/products', '/products/42', '/products/117', '/cart', '/checkout', '/pricing', '/blog/launch', '/account', '/search'], h4 % 10 + 1) AS path,
                CASE WHEN h7 % 100 < 55 THEN 0 WHEN h7 % 100 < 95 THEN 1 ELSE 2 END AS dev
            FROM (
                SELECT generate_series AS i,
                    abs(murmur_hash3_32(cast(generate_series AS VARCHAR), 'r{d}')) % 100 AS r,
                    abs(murmur_hash3_32(cast(generate_series AS VARCHAR), 't{d}')) AS h2,
                    abs(murmur_hash3_32(cast(generate_series AS VARCHAR), 'v{d}')) AS h3,
                    abs(murmur_hash3_32(cast(generate_series AS VARCHAR), 'a{d}')) AS h4,
                    abs(murmur_hash3_32(cast(generate_series AS VARCHAR), 'b{d}')) AS h5,
                    abs(murmur_hash3_32(cast(generate_series AS VARCHAR), 'c{d}')) AS h6,
                    abs(murmur_hash3_32(cast(generate_series AS VARCHAR), 'd{d}')) AS h7
                FROM TABLE(generate_series(0, {count - 1}))
            ) g
        ) x
        """;

    static string PersonUuid(string p) => $"concat('00000000-0000-7000-8000-', lower(lpad(hex({p}), 12, '0')))";

    static string PersonsSql(int persons) => $"""
        INSERT INTO analytics.persons (project_id, person_id, properties, is_identified, created_at, version, is_deleted, updated_at)
        SELECT 2, {PersonUuid("generate_series")},
            json_object('email', concat('user', generate_series, '@example.com'), 'plan', element_at(['free', 'pro', 'enterprise'], generate_series % 3 + 1)),
            true, '2026-01-01 00:00:00', 1, false, '2026-01-01 00:00:00'
        FROM TABLE(generate_series(0, {persons - 1}))
        """;

    static string DistinctIdsSql(int persons) => $"""
        INSERT INTO analytics.person_distinct_ids (project_id, distinct_id, person_id, version, is_deleted, updated_at)
        SELECT 2, concat('v', generate_series), {PersonUuid("generate_series")}, 1, false, '2026-01-01 00:00:00'
        FROM TABLE(generate_series(0, {persons - 1}))
        UNION ALL
        SELECT 2, concat('v', generate_series + {IdentifiedSecondIdOffset}), {PersonUuid("generate_series")}, 1, false, '2026-01-01 00:00:00'
        FROM TABLE(generate_series(0, {persons - 1}))
        """;

    static async Task SeedPostgres(string pg, int persons)
    {
        await using var db = NpgsqlDataSource.Create(pg);
        foreach (var sql in new[]
        {
            $"""
            INSERT INTO persons (id, project_id, properties, is_identified, version, created_at, updated_at)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(p), 12, '0'))::uuid, 2,
                   jsonb_build_object('email', 'user' || p || '@example.com', 'plan', (ARRAY['free', 'pro', 'enterprise'])[p % 3 + 1]),
                   true, 1, '2026-01-01', '2026-01-01'
            FROM generate_series(0, {persons - 1}) p
            ON CONFLICT DO NOTHING
            """,
            $"""
            INSERT INTO person_distinct_ids (project_id, distinct_id, person_id, version)
            SELECT 2, 'v' || (p + o), ('00000000-0000-7000-8000-' || lpad(to_hex(p), 12, '0'))::uuid, 1
            FROM generate_series(0, {persons - 1}) p CROSS JOIN (VALUES (0), ({IdentifiedSecondIdOffset})) AS offsets(o)
            ON CONFLICT DO NOTHING
            """,
        })
        {
            await using var cmd = db.CreateCommand(sql);
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static string PersonIdFor(int p) => $"00000000-0000-7000-8000-{p:x12}";

    static async Task<string> TableSize(MySqlConnection conn)
    {
        await using var cmd = new MySqlCommand("SHOW DATA FROM analytics.events", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetValue(reader.GetOrdinal("Size"))?.ToString() ?? "-" : "-";
    }

    static double ParseSize(string size)
    {
        var parts = size.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return v * parts[1].ToUpperInvariant() switch { "KB" => 1024, "MB" => 1024 * 1024, "GB" => 1024d * 1024 * 1024, "TB" => 1024d * 1024 * 1024 * 1024, _ => 1 };
    }

    static async Task Exec(MySqlConnection conn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 3600 };
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<object?> Scalar(MySqlConnection conn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 600 };
        return await cmd.ExecuteScalarAsync();
    }
}
