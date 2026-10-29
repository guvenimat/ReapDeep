using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ReapDeep.LoadTest;

static class Demo
{
    public const long ProjectId = 4;
    public const string PublicKey = "pk_demo_shop";

    sealed record Device(string UserAgent, string Type, int Width, int Height);
    sealed record Source(string? UtmSource, string? UtmMedium, string? Referrer, string Landing, double Weight);
    sealed record Product(string Id, string Name, string Category, double Price);
    sealed record Ev(string Event, string DistinctId, DateTime Ts, Dictionary<string, object?> Props);

    static readonly (Device Device, double Weight)[] Devices =
    [
        (new("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Desktop", 1920, 1080), 0.33),
        (new("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15", "Desktop", 1512, 982), 0.14),
        (new("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", "Desktop", 1920, 1080), 0.08),
        (new("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", "Mobile", 390, 844), 0.27),
        (new("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36", "Mobile", 412, 915), 0.18),
    ];

    static readonly Source[] Sources =
    [
        new("google", "cpc", "https://www.google.com/", "/", 0.30),
        new(null, null, null, "/", 0.25),
        new("newsletter", "email", null, "/blog/spring-collection", 0.15),
        new("instagram", "social", "https://www.instagram.com/", "/products/linen-shirt", 0.18),
        new("producthunt", "referral", "https://www.producthunt.com/", "/pricing", 0.12),
    ];

    static readonly Product[] Products =
    [
        new("linen-shirt", "Linen shirt", "apparel", 49),
        new("canvas-sneakers", "Canvas sneakers", "shoes", 79),
        new("wool-scarf", "Wool scarf", "accessories", 29),
        new("denim-jacket", "Denim jacket", "apparel", 119),
        new("leather-bag", "Leather bag", "accessories", 159),
        new("running-shoes", "Running shoes", "shoes", 99),
    ];

    static readonly string[] BlogPosts = ["/blog/spring-collection", "/blog/care-guide", "/blog/sizing"];

    static int _signups;

    public static async Task Run(string captureUrl, string pg, int visitors, int days, int seed)
    {
        await using var db = NpgsqlDataSource.Create(pg);
        await using (var cmd = db.CreateCommand("""
            INSERT INTO projects (id, organization_id, name, public_key) VALUES (@id, 1, 'Demo Shop', @key) ON CONFLICT (id) DO NOTHING;
            SELECT setval('projects_id_seq', GREATEST((SELECT max(id) FROM projects), 1));
            """))
        {
            cmd.Parameters.AddWithValue("id", ProjectId);
            cmd.Parameters.AddWithValue("key", PublicKey);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = db.CreateCommand("SELECT count(*) FROM event_definitions WHERE project_id = @id"))
        {
            cmd.Parameters.AddWithValue("id", ProjectId);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                Console.WriteLine("Demo project already has data; nothing to do. Delete project 4 and its events to reseed.");
                return;
            }
        }

        Console.WriteLine("Waiting for the ingestion API to load the demo project key...");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        for (var i = 0; ; i++)
        {
            var probe = await Send(http, captureUrl, null, [new Ev("", "probe", DateTime.UtcNow, [])]);
            if (probe == 202) break;
            if (i > 30) throw new InvalidOperationException($"Ingestion API rejects the demo key (HTTP {probe})");
            await Task.Delay(2000);
        }

        var rng = new Random(seed);
        var now = DateTime.UtcNow;
        var start = now.Date.AddDays(-(days - 1));
        var sessions = new List<(Device Device, List<Ev> Events)>();
        var dayWeights = Enumerable.Range(0, days).Select(d =>
        {
            var day = start.AddDays(d);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.7 : 1.0;
            return (1 + 0.8 * d / days) * weekend;
        }).ToArray();
        var totalWeight = dayWeights.Sum();

        for (var v = 0; v < visitors; v++)
        {
            var firstDay = Pick(rng, dayWeights, totalWeight);
            var device = PickWeighted(rng, Devices);
            var source = PickWeighted(rng, Sources.Select(s => (s, s.Weight)).ToArray());
            var visitor = new Visitor($"anon-{Guid.CreateVersion7()}", device, source);

            AddSession(rng, sessions, visitor, start.AddDays(firstDay), now, first: true);
            for (var week = 1; week < 8; week++)
            {
                var p = visitor.UserId is not null ? 0.48 * Math.Pow(0.86, week - 1) : 0.16 * Math.Pow(0.72, week - 1);
                if (rng.NextDouble() >= p) continue;
                var day = start.AddDays(firstDay + week * 7 - 3 + rng.Next(7));
                if (day > now) break;
                AddSession(rng, sessions, visitor, day, now, first: false);
            }
        }

        var events = sessions.Sum(s => s.Events.Count);
        Console.WriteLine($"Sending {events:N0} events in {sessions.Count:N0} sessions from {visitors:N0} visitors ({_signups:N0} sign-ups) over {days} days...");
        var sw = Stopwatch.StartNew();
        var sent = 0;
        await Parallel.ForEachAsync(sessions, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (s, ct) =>
        {
            foreach (var chunk in s.Events.Chunk(100))
            {
                var status = await Send(http, captureUrl, s.Device.UserAgent, chunk);
                if (status != 202) throw new InvalidOperationException($"capture returned HTTP {status}");
            }
            if (Interlocked.Add(ref sent, s.Events.Count) % 5000 < s.Events.Count) Console.WriteLine($"  {sent:N0} / {events:N0}");
        });
        Console.WriteLine($"Sent in {sw.Elapsed.TotalSeconds:0.0} s.");

        await CreateDashboard(db);
        Console.WriteLine("Created the 'Demo Shop overview' dashboard. Select the 'Demo Shop' project and the last 30 days in the dashboard.");
    }

    sealed class Visitor(string anonId, Device device, Source source)
    {
        public string AnonId { get; } = anonId;
        public Device Device { get; } = device;
        public Source Source { get; } = source;
        public string? UserId { get; set; }
        public string DistinctId => UserId ?? AnonId;
    }

    static void AddSession(Random rng, List<(Device, List<Ev>)> sessions, Visitor v, DateTime day, DateTime now, bool first)
    {
        var hour = 8 + (int)(Math.Pow(rng.NextDouble(), 0.8) * 15);
        var t = day.AddHours(hour).AddMinutes(rng.Next(60)).AddSeconds(rng.Next(60));
        var sessionId = Guid.CreateVersion7().ToString();
        var list = new List<Ev>();

        Dictionary<string, object?> Props(string path, Dictionary<string, object?>? extra = null)
        {
            var p = new Dictionary<string, object?>
            {
                ["$current_url"] = "https://demo-shop.example" + path,
                ["$host"] = "demo-shop.example",
                ["$pathname"] = path,
                ["$session_id"] = sessionId,
                ["$screen_width"] = v.Device.Width,
                ["$screen_height"] = v.Device.Height,
                ["$lib"] = "web",
                ["$lib_version"] = "0.1.0",
            };
            if (first && v.Source.UtmSource is not null)
            {
                p["utm_source"] = v.Source.UtmSource;
                p["utm_medium"] = v.Source.UtmMedium;
            }
            if (first && v.Source.Referrer is not null) p["$referrer"] = v.Source.Referrer;
            if (extra is not null) foreach (var (k, val) in extra) p[k] = val;
            return p;
        }
        void Add(string ev, string path, Dictionary<string, object?>? extra = null)
        {
            if (t > now) return;
            list.Add(new Ev(ev, v.DistinctId, t, Props(path, extra)));
            t = t.AddSeconds(8 + rng.Next(80));
        }
        void Identify(string path)
        {
            if (v.UserId is not null) return;
            var n = ++_signups;
            v.UserId = $"user-{n:D5}";
            var plan = rng.NextDouble() < 0.22 ? "pro" : "free";
            if (t > now) return;
            list.Add(new Ev("$identify", v.UserId, t, Props(path, new()
            {
                ["$anon_distinct_id"] = v.AnonId,
                ["$set"] = new Dictionary<string, object?>
                {
                    ["email"] = $"{v.UserId}@example.com",
                    ["name"] = $"Customer {n}",
                    ["plan"] = plan,
                    ["signup_source"] = v.Source.UtmSource ?? "direct",
                },
            })));
            t = t.AddSeconds(5 + rng.Next(20));
        }

        var page = first ? v.Source.Landing : rng.NextDouble() < 0.5 ? "/" : "/account";
        var intent = v.UserId is not null ? 1.4 : 1.0;
        for (var step = 0; step < 9 && page != "exit"; step++)
        {
            if (page.StartsWith("/products/", StringComparison.Ordinal))
            {
                var product = Products.First(p => page.EndsWith(p.Id, StringComparison.Ordinal));
                Add("$pageview", page);
                var item = new Dictionary<string, object?> { ["product_id"] = product.Id, ["product_name"] = product.Name, ["category"] = product.Category, ["price"] = product.Price };
                Add("product_viewed", page, item);
                if (rng.NextDouble() < 0.32 * intent)
                {
                    Add("add_to_cart", page, item);
                    if (rng.NextDouble() < 0.62 * intent)
                    {
                        Add("$pageview", "/checkout");
                        Identify("/checkout");
                        Add("checkout_started", "/checkout", new() { ["cart_value"] = product.Price, ["items"] = 1 });
                        if (rng.NextDouble() < 0.71)
                        {
                            Add("purchase", "/checkout", new() { ["amount"] = product.Price, ["currency"] = "EUR", ["product_id"] = product.Id, ["category"] = product.Category });
                            break;
                        }
                    }
                }
                page = rng.NextDouble() < 0.45 ? "/products" : rng.NextDouble() < 0.5 ? RandomProduct(rng) : "exit";
                continue;
            }

            Add("$pageview", page);
            if (page == "/pricing" && rng.NextDouble() < 0.3) Identify("/pricing");
            if (page == "/" && first && rng.NextDouble() < 0.08) Identify("/");
            page = page switch
            {
                "/" => Next(rng, ("/products", 0.45), ("/pricing", 0.15), (BlogPosts[rng.Next(BlogPosts.Length)], 0.1), ("exit", 0.3)),
                "/products" => Next(rng, (RandomProduct(rng), 0.7), ("/pricing", 0.05), ("exit", 0.25)),
                "/pricing" => Next(rng, ("/products", 0.4), ("/", 0.15), ("exit", 0.45)),
                "/account" => Next(rng, ("/products", 0.5), ("exit", 0.5)),
                _ => Next(rng, (RandomProduct(rng), 0.35), ("/", 0.2), ("exit", 0.45)),
            };
        }
        if (list.Count > 0) sessions.Add((v.Device, list));
    }

    static string RandomProduct(Random rng) => "/products/" + Products[rng.Next(Products.Length)].Id;

    static string Next(Random rng, params (string Page, double Weight)[] options) => PickWeighted(rng, options);

    static T PickWeighted<T>(Random rng, (T Item, double Weight)[] items)
    {
        var r = rng.NextDouble() * items.Sum(i => i.Weight);
        foreach (var (item, w) in items)
        {
            if ((r -= w) <= 0) return item;
        }
        return items[^1].Item;
    }

    static int Pick(Random rng, double[] weights, double total)
    {
        var r = rng.NextDouble() * total;
        for (var i = 0; i < weights.Length; i++)
            if ((r -= weights[i]) <= 0) return i;
        return weights.Length - 1;
    }

    static async Task<int> Send(HttpClient http, string url, string? userAgent, IEnumerable<Ev> events)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("api_key", PublicKey);
            w.WriteStartArray("batch");
            foreach (var e in events)
            {
                w.WriteStartObject();
                w.WriteString("event", e.Event);
                w.WriteString("distinct_id", e.DistinctId);
                w.WriteString("timestamp", e.Ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                w.WritePropertyName("properties");
                JsonSerializer.Serialize(w, e.Props);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(buffer.ToArray()) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        if (userAgent is not null) req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var res = await http.SendAsync(req);
        return (int)res.StatusCode;
    }

    static async Task CreateDashboard(NpgsqlDataSource db)
    {
        (string Name, string Kind, string Query)[] insights =
        [
            ("Daily visitors", "trends", """{"series":[{"event":"$pageview","metric":"unique_users"}],"filters":[],"deduplicate":false,"chart":"line"}"""),
            ("Purchase funnel", "funnels", """{"steps":["product_viewed","add_to_cart","checkout_started","purchase"],"window_seconds":86400,"filters":[],"sampling":1}"""),
            ("Sign-ups by source", "trends", """{"series":[{"event":"$identify","metric":"unique_users"}],"interval":"week","filters":[],"breakdown":{"scope":"event","property":"utm_source"},"deduplicate":false,"chart":"bar"}"""),
            ("Weekly retention after sign-up", "retention", """{"start_event":"$identify","return_event":"$pageview","period":"week","periods":6,"filters":[]}"""),
            ("Purchases by device", "trends", """{"series":[{"event":"purchase","metric":"total"}],"interval":"week","filters":[],"breakdown":{"scope":"event","property":"$device_type"},"deduplicate":false,"chart":"bar"}"""),
            ("Journeys after the first page", "paths", """{"start_event":"$pageview","depth":4,"window_seconds":3600,"pageview_paths":true,"filters":[]}"""),
        ];
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        long? owner;
        await using (var cmd = new NpgsqlCommand("SELECT user_id FROM organization_members WHERE organization_id = 1 AND role = 'owner' ORDER BY user_id LIMIT 1", conn, tx))
            owner = await cmd.ExecuteScalarAsync() as long?;

        var ids = new List<long>();
        foreach (var (name, kind, query) in insights)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO insights (project_id, name, kind, query, created_by) VALUES (@p, @n, @k, @q::jsonb, @u) RETURNING id", conn, tx);
            cmd.Parameters.AddWithValue("p", ProjectId);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("q", query);
            cmd.Parameters.AddWithValue("u", (object?)owner ?? DBNull.Value);
            ids.Add((long)(await cmd.ExecuteScalarAsync())!);
        }
        await using (var cmd = new NpgsqlCommand("""
            WITH d AS (
                INSERT INTO dashboards (project_id, name, description, created_by)
                VALUES (@p, 'Demo Shop overview', 'Traffic, conversion and retention of the demo store', @u) RETURNING id
            )
            INSERT INTO dashboard_items (dashboard_id, insight_id, position)
            SELECT d.id, u.id, u.ord FROM d, unnest(@ids) WITH ORDINALITY AS u(id, ord)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("p", ProjectId);
            cmd.Parameters.AddWithValue("u", (object?)owner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ids", ids.ToArray());
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
