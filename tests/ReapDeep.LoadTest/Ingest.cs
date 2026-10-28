using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MySqlConnector;

namespace ReapDeep.LoadTest;

sealed record IngestRun(string Name, string Url, string Key, int Rate, int Seconds, int Batch, string RunId, string StarRocks, bool Marker);

static class Ingest
{
    static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0",
    ];
    static readonly string[] Paths = ["/", "/products", "/products/42", "/products/117", "/cart", "/checkout", "/pricing", "/blog/launch", "/account", "/search"];
    static readonly string[] Plans = ["free", "pro", "enterprise"];
    static readonly string[] Referrers = ["$direct", "https://www.google.com/", "https://news.ycombinator.com/", "https://t.co/abc"];

    public static async Task Run(IngestRun o)
    {
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1024,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        await WaitForProject(http, o);
        var errorRowsBefore = await RoutineLoadErrorRows(o.StarRocks);

        var requests = Math.Max(1, (int)((long)o.Rate * o.Seconds / o.Batch));
        var latencies = new double[requests];
        var statuses = new int[requests];
        long accepted = 0, bytes = 0, gzipBytes = 0, gzipInput = 0, gzipTicks = 0, maxLagTicks = 0;
        var errors = new ConcurrentDictionary<string, int>();
        var inFlight = new SemaphoreSlim(4096);
        var tasks = new Task[requests];
        double ticksPerRequest = (double)Stopwatch.Frequency * o.Batch / o.Rate;
        Task<double?>? freshness = null;

        Console.WriteLine($"[{o.Name}] {o.Rate} events/s, batch {o.Batch}, {o.Seconds}s -> {requests} requests");
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < requests; i++)
        {
            var due = start + (long)(i * ticksPerRequest);
            await WaitUntil(due);
            maxLagTicks = Math.Max(maxLagTicks, Stopwatch.GetTimestamp() - due);

            if (o.Marker && freshness is null && i >= requests / 2) freshness = MeasureFreshness(http, o);

            var payload = BuildBatch(o, i);
            bytes += payload.Length;
            if (i % 100 == 0)
            {
                var t0 = Stopwatch.GetTimestamp();
                gzipBytes += GzipLength(payload);
                gzipTicks += Stopwatch.GetTimestamp() - t0;
                gzipInput += payload.Length;
            }

            await inFlight.WaitAsync();
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                var sw = Stopwatch.GetTimestamp();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, o.Url) { Content = new ByteArrayContent(payload) };
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                    req.Headers.TryAddWithoutValidation("User-Agent", UserAgents[index % UserAgents.Length]);
                    using var resp = await http.SendAsync(req);
                    var body = await resp.Content.ReadAsByteArrayAsync();
                    latencies[index] = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
                    statuses[index] = (int)resp.StatusCode;
                    if (resp.StatusCode == HttpStatusCode.Accepted)
                    {
                        using var doc = JsonDocument.Parse(body);
                        Interlocked.Add(ref accepted, doc.RootElement.GetProperty("accepted").GetInt32());
                    }
                    else errors.AddOrUpdate($"HTTP {(int)resp.StatusCode}", 1, (_, n) => n + 1);
                }
                catch (Exception ex)
                {
                    latencies[index] = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
                    statuses[index] = -1;
                    errors.AddOrUpdate(ex.GetType().Name, 1, (_, n) => n + 1);
                }
                finally
                {
                    inFlight.Release();
                }
            });
        }
        await Task.WhenAll(tasks);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var markerSeconds = freshness is null ? null : await freshness;
        if (markerSeconds is not null) accepted++;

        var sorted = latencies.Order().ToArray();
        var ok = statuses.Count(s => s == 202);
        var sentEvents = (long)requests * o.Batch;
        Console.WriteLine($"[{o.Name}] sent in {elapsed:0.0}s, verifying {accepted} accepted events in StarRocks...");
        var (stored, settleSeconds) = await WaitForStored(o, accepted);
        var errorRows = await RoutineLoadErrorRows(o.StarRocks) - errorRowsBefore;

        var result = new IngestResult(o.Name, o.Rate, o.Batch, o.Seconds, requests, sentEvents, accepted, ok,
            sentEvents / elapsed, Stats.Percentile(sorted, 0.5), Stats.Percentile(sorted, 0.95), Stats.Percentile(sorted, 0.99), sorted[^1],
            bytes / elapsed / 1_048_576, gzipInput == 0 ? 1 : (double)gzipInput / gzipBytes,
            gzipInput == 0 ? 0 : Stopwatch.GetElapsedTime(0, gzipTicks).TotalMicroseconds / (gzipInput / 1024.0),
            markerSeconds, Stopwatch.GetElapsedTime(0, maxLagTicks).TotalMilliseconds,
            errors.Count == 0 ? "none" : string.Join(", ", errors.Select(e => $"{e.Key} x{e.Value}")),
            stored, settleSeconds, errorRows);

        Write(result);
    }

    static async Task<(long Stored, double Seconds)> WaitForStored(IngestRun o, long expected)
    {
        await using var conn = new MySqlConnection(o.StarRocks);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT count(*) FROM analytics.events WHERE project_id = @p AND event_id LIKE @prefix", conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("p", Setup.LoadTestProjectId);
        cmd.Parameters.AddWithValue("prefix", o.RunId + "-%");
        var sw = Stopwatch.StartNew();
        long last = -1;
        var lastChange = sw.Elapsed;
        while (true)
        {
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            if (count >= expected) return (count, sw.Elapsed.TotalSeconds);
            if (count != last) { last = count; lastChange = sw.Elapsed; }
            if (sw.Elapsed - lastChange > TimeSpan.FromSeconds(45)) return (count, sw.Elapsed.TotalSeconds);
            await Task.Delay(1000);
        }
    }

    static async Task<long> RoutineLoadErrorRows(string sr)
    {
        await using var conn = new MySqlConnection(sr);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("SHOW ROUTINE LOAD FOR analytics.events_from_kafka", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return 0;
        using var stats = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("Statistic")));
        return stats.RootElement.GetProperty("errorRows").GetInt64();
    }

    static async Task WaitForProject(HttpClient http, IngestRun o)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            var body = $$"""{"api_key":"{{o.Key}}","batch":[{"event":"$load_probe","distinct_id":"probe","event_id":"probe-{{o.RunId}}-{{DateTime.UtcNow.Ticks}}"}]}""";
            using var resp = await http.PostAsync(o.Url, new StringContent(body));
            if (resp.StatusCode == HttpStatusCode.Accepted) return;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"Ingestion does not accept {o.Key}: HTTP {(int)resp.StatusCode}");
            await Task.Delay(2000);
        }
    }

    static async Task WaitUntil(long due)
    {
        while (true)
        {
            var remaining = due - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            if (remaining > Stopwatch.Frequency / 50) await Task.Delay(TimeSpan.FromTicks(remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency) - TimeSpan.FromMilliseconds(16));
            else Thread.SpinWait(50);
        }
    }

    static async Task<double?> MeasureFreshness(HttpClient http, IngestRun o)
    {
        var eventId = $"{o.RunId}-marker";
        var body = $$"""{"api_key":"{{o.Key}}","batch":[{"event":"$load_marker","distinct_id":"marker","event_id":"{{eventId}}"}]}""";
        using var resp = await http.PostAsync(o.Url, new StringContent(body));
        if (resp.StatusCode != HttpStatusCode.Accepted) return null;
        var acceptedAt = Stopwatch.GetTimestamp();

        await using var conn = new MySqlConnection(o.StarRocks);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT count(*) FROM analytics.events WHERE project_id = @p AND event = '$load_marker' AND event_id = @id", conn);
        cmd.Parameters.AddWithValue("p", Setup.LoadTestProjectId);
        cmd.Parameters.AddWithValue("id", eventId);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0) return Stopwatch.GetElapsedTime(acceptedAt).TotalSeconds;
            await Task.Delay(250);
        }
        return null;
    }

    static byte[] BuildBatch(IngestRun o, int requestIndex)
    {
        var buffer = new ArrayBufferWriter<byte>(o.Batch * 700);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("api_key", o.Key);
            w.WriteString("sent_at", DateTime.UtcNow);
            w.WriteStartArray("batch");
            for (var k = 0; k < o.Batch; k++)
            {
                var seq = (long)requestIndex * o.Batch + k;
                var r = Rng.Mix((ulong)seq);
                var visitor = r % 50_000;
                var path = Paths[(int)((r >> 8) % (ulong)Paths.Length)];
                var roll = (int)((r >> 16) % 100);
                w.WriteStartObject();
                w.WriteString("event", roll < 50 ? "$pageview" : roll < 75 ? "$autocapture_click" : roll < 90 ? "product_viewed" : roll < 95 ? "signup" : "purchase");
                w.WriteString("distinct_id", $"lt_{visitor}");
                w.WriteString("event_id", $"{o.RunId}-{seq}");
                w.WriteStartObject("properties");
                w.WriteString("$current_url", "https://shop.example.com" + path);
                w.WriteString("$host", "shop.example.com");
                w.WriteString("$pathname", path);
                w.WriteString("$referrer", Referrers[(int)((r >> 24) % (ulong)Referrers.Length)]);
                w.WriteNumber("$screen_width", (r >> 28) % 2 == 0 ? 1920 : 390);
                w.WriteNumber("$screen_height", (r >> 28) % 2 == 0 ? 1080 : 844);
                w.WriteString("$session_id", $"s-{visitor}-{(r >> 32) % 4}");
                w.WriteString("$lib", "web");
                w.WriteString("$lib_version", "0.1.0");
                if ((r >> 36) % 5 == 0) w.WriteString("utm_source", "google");
                w.WriteString("plan", Plans[(int)((r >> 40) % (ulong)Plans.Length)]);
                w.WriteString("ab_test", (r >> 44) % 2 == 0 ? "control" : "variant_b");
                w.WriteNumber("cart_value", (double)((r >> 48) % 50_000) / 100);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    static long GzipLength(byte[] payload)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(payload);
        return ms.Length;
    }

    static void Write(IngestResult r)
    {
        Report.Heading($"Ingestion: {r.Name}");
        Report.Table(
            ["Target", "Batch", "Duration", "Requests", "Achieved", "p50", "p95", "p99", "max", "Errors"],
            [[
                $"{Stats.N(r.Rate)}/s", r.Batch.ToString(), $"{r.Seconds}s", Stats.N(r.Requests), $"{Stats.N(r.AchievedPerSecond)}/s",
                $"{Stats.Ms(r.P50)} ms", $"{Stats.Ms(r.P95)} ms", $"{Stats.Ms(r.P99)} ms", $"{Stats.Ms(r.Max)} ms", r.Errors,
            ]]);
        Report.Table(
            ["Events sent", "Accepted (202)", "In StarRocks", "Lost", "Routine Load error rows", "All loaded after", "Freshness ($load_marker)", "Network", "Gzip ratio", "Gzip CPU", "Max scheduler lag"],
            [[
                Stats.N(r.EventsSent), Stats.N(r.Accepted), Stats.N(r.Stored), Stats.N(Math.Max(0, r.Accepted - r.Stored)), Stats.N(r.ErrorRows),
                $"{Stats.F(r.SettleSeconds)} s", r.FreshnessSeconds is { } f ? $"{Stats.F(f)} s" : "-",
                $"{Stats.F(r.MegabytesPerSecond)} MB/s", $"{Stats.F(r.GzipRatio)}x", $"{Stats.F(r.GzipMicrosPerKb)} us/KB", $"{Stats.Ms(r.MaxLagMs)} ms",
            ]]);
    }
}

sealed record IngestResult(
    string Name, int Rate, int Batch, int Seconds, int Requests, long EventsSent, long Accepted, int OkRequests,
    double AchievedPerSecond, double P50, double P95, double P99, double Max, double MegabytesPerSecond,
    double GzipRatio, double GzipMicrosPerKb, double? FreshnessSeconds, double MaxLagMs, string Errors,
    long Stored, double SettleSeconds, long ErrorRows);
