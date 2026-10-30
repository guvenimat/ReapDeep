using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReapDeep.LoadTest;

static class Bench
{
    sealed record Scenario(string Id, string Name, string Target, string Path, string? Body);

    public static async Task Run(string api, string key, int runs)
    {
        using var http = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var today = DateTime.UtcNow.Date;
        var range = $"\"date_from\":\"{today.AddDays(-29):yyyy-MM-dd}\",\"date_to\":\"{today:yyyy-MM-dd}\"";
        var person = Seeder.PersonIdFor(42);
        var from30 = Uri.EscapeDataString(today.AddDays(-29).ToString("yyyy-MM-dd"));
        var cohorts = await CohortIds(http);
        var heavy = cohorts.GetValueOrDefault(Cohorts.Heavy);
        var noPurchase = cohorts.GetValueOrDefault(Cohorts.NoPurchase);
        var pro = cohorts.GetValueOrDefault(Cohorts.Pro);
        string InCohort(long id) => $$"""{"scope":"cohort","property":"{{id}}","op":"in"}""";

        var rfm = "\"purchase_event\":\"purchase\",\"amount_property\":\"cart_value\"";
        var weeks = $"\"date_from\":\"{today.AddDays(-27):yyyy-MM-dd}\",\"date_to\":\"{today:yyyy-MM-dd}\"";

        Scenario[] scenarios =
        [
            new("1", "Trends total, 30 days daily", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"total"}],"interval":"day",{{range}}}"""),
            new("2", "Trends unique users (colocate join)", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"unique_users"}],"interval":"day",{{range}}}"""),
            new("3", "Unique users + device_type breakdown + filter", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"unique_users"}],"interval":"day","breakdown":{"property":"$device_type","limit":5},"filters":[{"property":"utm_source","op":"eq","value":"google"}],{{range}}}"""),
            new("4", "Trends with person property filter", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"unique_users"}],"interval":"day","filters":[{"scope":"person","property":"plan","op":"eq","value":"pro"}],{{range}}}"""),
            new("5a", "Trends total, deduplicate=false", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"total"}],"interval":"day","deduplicate":false,{{range}}}"""),
            new("5b", "Trends total, deduplicate=true", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"total"}],"interval":"day","deduplicate":true,{{range}}}"""),
            new("6", "Funnel pageview > signup > purchase, 7d window", "< 3 s", "/api/v1/query/funnels",
                $$"""{"steps":[{"event":"$pageview"},{"event":"signup"},{"event":"purchase"}],"window_seconds":604800,{{range}}}"""),
            new("7", "Funnel + device_type breakdown", "< 3 s", "/api/v1/query/funnels",
                $$"""{"steps":[{"event":"$pageview"},{"event":"signup"},{"event":"purchase"}],"window_seconds":604800,"breakdown":{"property":"$device_type","limit":5},{{range}}}"""),
            new("6s", "Funnel 3 steps, sampling 10%", "< 3 s", "/api/v1/query/funnels",
                $$"""{"steps":[{"event":"$pageview"},{"event":"signup"},{"event":"purchase"}],"window_seconds":604800,"sampling":0.1,{{range}}}"""),
            new("7s", "Funnel + breakdown, sampling 25%", "< 3 s", "/api/v1/query/funnels",
                $$"""{"steps":[{"event":"$pageview"},{"event":"signup"},{"event":"purchase"}],"window_seconds":604800,"breakdown":{"property":"$device_type","limit":5},"sampling":0.25,{{range}}}"""),
            new("8a","Events of one person, 30 days (bucket pruning)", "-", $"/api/v1/events?person_id={person}&limit=200&date_from={from30}", null),
            new("8b", "Latest 50 events (7 days)", "-", "/api/v1/events?limit=50", null),
            new("9a", "Trends total + cohort filter (vs 1)", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"total"}],"interval":"day","filters":[{{InCohort(heavy)}}],{{range}}}"""),
            new("9b", "Unique users + cohort filter (vs 2)", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"unique_users"}],"interval":"day","filters":[{{InCohort(heavy)}}],{{range}}}"""),
            new("9c", "Unique users, breakdown by 2 cohorts", "< 1 s", "/api/v1/query/trends",
                $$"""{"series":[{"event":"$pageview","metric":"unique_users"}],"interval":"day","breakdown":{"scope":"cohort","cohort_ids":[{{heavy}},{{pro}}]},{{range}}}"""),
            new("9d", "Funnel 3 steps + cohort filter (vs 6)", "< 3 s", "/api/v1/query/funnels",
                $$"""{"steps":[{"event":"$pageview"},{"event":"signup"},{"event":"purchase"}],"window_seconds":604800,"filters":[{{InCohort(noPurchase)}}],{{range}}}"""),
            new("9e", "Funnel, breakdown by 2 cohorts (vs 7)", "< 3 s", "/api/v1/query/funnels",
                $$"""{"steps":[{"event":"$pageview"},{"event":"signup"},{"event":"purchase"}],"window_seconds":604800,"breakdown":{"scope":"cohort","cohort_ids":[{{heavy}},{{pro}}]},{{range}}}"""),
            new("9f", "Funnel 10% sampling + cohort filter (vs 6s)", "< 3 s", "/api/v1/query/funnels",
                $$"""{"steps":[{"event":"$pageview"},{"event":"signup"},{"event":"purchase"}],"window_seconds":604800,"sampling":0.1,"filters":[{{InCohort(heavy)}}],{{range}}}"""),
            new("10a", "RFM, 365-day window (all buyers)", "< 1.5 s", "/api/v1/query/rfm", $$"""{"window_days":365,{{rfm}}}"""),
            new("10b", "RFM + person property filter", "< 1.5 s", "/api/v1/query/rfm",
                $$"""{"window_days":365,{{rfm}},"filters":[{"scope":"person","property":"plan","op":"eq","value":"pro"}]}"""),
            new("11a", "Lifecycle weekly, any event, 90-day look-back", "< 2 s", "/api/v1/query/lifecycle",
                $$"""{"period":"week","lookback_days":90,{{weeks}}}"""),
            new("11b", "Lifecycle daily, purchase, 365-day look-back", "< 2 s", "/api/v1/query/lifecycle",
                $$"""{"event":"purchase","period":"day","lookback_days":365,{{range}}}"""),
            new("11c", "Lifecycle weekly, any event + cohort filter", "< 2 s", "/api/v1/query/lifecycle",
                $$"""{"period":"week","lookback_days":90,"filters":[{{InCohort(heavy)}}],{{weeks}}}"""),
        ];

        var rows = new List<string[]>();
        foreach (var s in scenarios)
        {
            if ((s.Id.StartsWith('9') || s.Id == "11c") && cohorts.Count < 3) continue;
            var (cold, coldServer, sample) = await Call(http, s);
            var client = new List<double>();
            var server = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                var (ms, srv, _) = await Call(http, s);
                client.Add(ms);
                server.Add(srv);
            }
            client.Sort();
            server.Sort();
            var p95 = Stats.Percentile(client, 0.95);
            var limit = s.Target switch { "< 1 s" => 1000, "< 1.5 s" => 1500, "< 2 s" => 2000, "< 3 s" => 3000, _ => double.MaxValue };
            Console.WriteLine($"  {s.Id,-3} {s.Name,-50} cold {cold,8:0} ms  p50 {Stats.Percentile(client, 0.5),7:0} ms  p95 {p95,7:0} ms   {sample}");
            rows.Add([
                s.Id, s.Name, $"{Stats.Ms(cold)} ms", $"{Stats.Ms(Stats.Percentile(client, 0.5))} ms", $"{Stats.Ms(p95)} ms",
                $"{Stats.Ms(Stats.Percentile(client, 0.99))} ms", $"{Stats.Ms(Stats.Percentile(server, 0.95))} ms", s.Target,
                limit == double.MaxValue ? "-" : p95 < limit ? "PASS" : "FAIL", sample,
            ]);
        }

        Report.Heading($"Query benchmark (project 2, 30 days, {runs} warm runs after 1 cold run)");
        Report.Table(["#", "Scenario", "Cold", "p50", "p95", "p99", "Server p95", "Target (p95)", "Result", "Result sample"], rows);
    }

    static async Task<Dictionary<string, long>> CohortIds(HttpClient http)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/v1/cohorts"));
        return doc.RootElement.GetProperty("cohorts").EnumerateArray()
            .Where(c => c.GetProperty("status").GetString() == "ready")
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetInt64());
    }

    static async Task<(double Ms, double ServerMs, string Sample)> Call(HttpClient http, Scenario s)
    {
        using var req = new HttpRequestMessage(s.Body is null ? HttpMethod.Get : HttpMethod.Post, s.Path);
        if (s.Body is not null) req.Content = new StringContent(s.Body, Encoding.UTF8, "application/json");
        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var ms = sw.Elapsed.TotalMilliseconds;
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Scenario {s.Id}: HTTP {(int)resp.StatusCode} {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var server = root.TryGetProperty("took_ms", out var t) ? t.GetDouble() : 0;
        return (ms, server, Summarize(root));
    }

    static string Summarize(JsonElement root)
    {
        if (root.TryGetProperty("events", out var events)) return $"{events.GetArrayLength()} events";
        if (root.TryGetProperty("customers", out var customers))
            return $"{Stats.N(customers.GetDouble())} customers, {Stats.N(root.GetProperty("segments").EnumerateArray().Count(x => x.GetProperty("persons").GetInt64() > 0))} segments";
        if (root.TryGetProperty("periods", out var periods))
        {
            var last = periods[periods.GetArrayLength() - 1];
            return $"{periods.GetArrayLength()} periods, last: new {Stats.N(last.GetProperty("new").GetDouble())}, returning {Stats.N(last.GetProperty("returning").GetDouble())}, dormant {Stats.N(last.GetProperty("dormant").GetDouble())}";
        }
        var results = root.GetProperty("results");
        if (results.GetArrayLength() == 0) return "no results";
        var first = results[0];
        if (first.TryGetProperty("steps", out var steps))
            return string.Join(" > ", steps.EnumerateArray().Select(x => Stats.N(x.GetProperty("count").GetDouble()))) + (results.GetArrayLength() > 1 ? $" ({results.GetArrayLength()} groups)" : "");
        return $"total {Stats.N(first.GetProperty("total").GetDouble())}" + (results.GetArrayLength() > 1 ? $" ({results.GetArrayLength()} groups)" : "");
    }
}
