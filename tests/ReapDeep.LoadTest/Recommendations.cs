using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MySqlConnector;
using Npgsql;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.LoadTest;

static class Recommendations
{
    const int CatalogSize = 2000;
    const int Runs = 5;

    public static async Task Run(string sr, string pg, int timeoutSeconds)
    {
        await using var db = NpgsqlDataSource.Create(pg);
        await Exec(db, $"DELETE FROM items WHERE project_id = {Setup.BenchProjectId}");
        await Exec(db, $"DELETE FROM recommendation_runs WHERE project_id = {Setup.BenchProjectId}");
        await Exec(db, $"""
            INSERT INTO items (project_id, item_id, title, category, price, in_stock)
            SELECT {Setup.BenchProjectId}, 'i' || n, 'Item ' || n, 'c' || (n % 20), 10 + n % 90, n % 20 <> 0
            FROM generate_series(0, {CatalogSize - 1}) n
            """);
        Console.WriteLine($"Catalog of {CatalogSize} items written; waiting for the person worker to calculate the models...");

        var sw = Stopwatch.StartNew();
        List<string[]> runs;
        while (true)
        {
            runs = [];
            await using (var cmd = db.CreateCommand($"SELECT model, source_items, calculation_ms FROM recommendation_runs WHERE project_id = {Setup.BenchProjectId} ORDER BY model"))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) runs.Add([r.GetString(0), Stats.F(r.GetInt32(1), "N0"), $"{Stats.F(r.GetInt32(2), "N0")} ms"]);
            if (runs.Count == RecommendationSql.Models.Length || sw.Elapsed.TotalSeconds > timeoutSeconds) break;
            await Task.Delay(2000);
        }
        Report.Heading($"Recommendation models (project {Setup.BenchProjectId}, {CatalogSize} items, calculated by the person worker)");
        Report.Table(["Model", "Source items", "Calculation"], runs);
        if (runs.Count < RecommendationSql.Models.Length) throw new TimeoutException("Models were not calculated in time");

        await using var conn = new MySqlConnection(sr);
        await conn.OpenAsync();
        var hitRate = RecommendationSql.HitRate(Setup.BenchProjectId, DateTime.UtcNow, 10);
        var (hitMs, hit) = await Time(conn, hitRate.Sql, hitRate.Parameters);
        Report.Heading("Hit-rate evaluation query");
        Report.Line($"p50 {Stats.F(hitMs, "0")} ms over {Runs} runs; cases {hit[0]}, co_viewed hits {hit[1]}, popular hits {hit[3]} (random seed data, so both are near chance).");

        var inner = "MAX(CASE e.event WHEN 'purchase' THEN 5 ELSE 1 END) AS w FROM analytics.events e " +
                             $"WHERE e.project_id = {Setup.BenchProjectId} AND e.event IN ('product_viewed', 'purchase')";
        var column = $"SELECT COUNT(*) FROM (SELECT e.distinct_id, e.item_id, {inner} AND e.item_id IS NOT NULL GROUP BY 1, 2) t";
        var json = $"SELECT COUNT(*) FROM (SELECT e.distinct_id, get_json_string(e.properties, '$.\"item_id\"') AS item_id, {inner} " +
                   "AND get_json_string(e.properties, '$.\"item_id\"') IS NOT NULL GROUP BY 1, 2) t";
        var (columnMs, columnRows) = await Time(conn, column, []);
        var (jsonMs, jsonRows) = await Time(conn, json, []);
        Report.Heading("item_id as a column vs. a JSON property (person x item aggregation, 30 days)");
        Report.Table(["Source", "p50", "Rows"],
        [
            ["events.item_id column", $"{Stats.F(columnMs, "0")} ms", columnRows[0]],
            ["get_json_string(properties, '$.item_id')", $"{Stats.F(jsonMs, "0")} ms", jsonRows[0]],
        ]);
    }

    public static async Task Latency(string url, string key, int seconds, int concurrency)
    {
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
        var popular = await http.GetFromJsonAsync<JsonElement>($"/recommend?api_key={key}&model=popular&limit=50");
        var ids = popular.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item_id").GetString()!).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("The project has no recommendations to request");

        var latencies = new List<double>[concurrency];
        var errors = 0;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async w =>
        {
            var rng = new Random(w);
            var mine = latencies[w] = [];
            while (DateTime.UtcNow < end)
            {
                var path = rng.NextDouble() < 0.7
                    ? $"/recommend?api_key={key}&model=co_viewed&item_id={Uri.EscapeDataString(ids[rng.Next(ids.Length)])}&limit=10"
                    : $"/recommend?api_key={key}&model=popular&limit=10";
                var sw = Stopwatch.StartNew();
                using var res = await http.GetAsync(path);
                await res.Content.ReadAsByteArrayAsync();
                mine.Add(sw.Elapsed.TotalMilliseconds);
                if (!res.IsSuccessStatusCode) Interlocked.Increment(ref errors);
            }
        }));

        var all = latencies.SelectMany(l => l).Order().ToList();
        Report.Heading($"Recommender latency ({concurrency} concurrent clients, {seconds} s, from the compose network)");
        Report.Table(["Requests", "Throughput", "p50", "p95", "p99", "Max", "Errors"],
        [[
            Stats.F(all.Count, "N0"), $"{Stats.F(all.Count / (double)seconds, "N0")}/s",
            $"{Stats.F(Stats.Percentile(all, 0.5), "0.00")} ms", $"{Stats.F(Stats.Percentile(all, 0.95), "0.00")} ms",
            $"{Stats.F(Stats.Percentile(all, 0.99), "0.00")} ms", $"{Stats.F(all[^1], "0.00")} ms", errors.ToString(),
        ]]);
    }

    static async Task<(double P50, string[] FirstRow)> Time(MySqlConnection conn, string sql, IEnumerable<(string Name, object Value)> parameters)
    {
        var times = new List<double>();
        string[] first = [];
        for (var i = 0; i <= Runs; i++)
        {
            await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 600 };
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            var sw = Stopwatch.StartNew();
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync()) first = Enumerable.Range(0, r.FieldCount).Select(c => Convert.ToString(r.GetValue(c)) ?? "").ToArray();
            if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return (Stats.Percentile(times, 0.5), first);
    }

    static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
