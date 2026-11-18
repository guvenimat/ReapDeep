using System.Net;
using System.Text;
using System.Text.Json;
using ReapDeep.Analytics.Flags;
using ReapDeep.Ingestion.Capture;
using ReapDeep.Server;

namespace ReapDeep.UnitTests;

public class ServerSdkTests
{
    const string Definitions = """
        {"flags":[
          {"key":"new-checkout","salt":"s1","rollout_percentage":40,"variants":[],"conditions":[]},
          {"key":"similar-model","salt":"s2","rollout_percentage":100,"variants":[{"key":"co_viewed","percentage":50},{"key":"co_viewed_weighted","percentage":50}],"conditions":[]},
          {"key":"vip","salt":"s3","rollout_percentage":100,"variants":[],"conditions":[
            {"property":"plan","op":"in","value":["pro","Team"]},{"property":"orders","op":"gte","value":3},{"property":"email","op":"not_contains","value":"@example.test"}]}
        ]}
        """;

    sealed record Rule(string Key, string Salt, int Rollout, string Variants, string Conditions);

    static readonly Rule[] Rules =
    [
        new("new-checkout", "s1", 40, "[]", "[]"),
        new("similar-model", "s2", 100, """[{"key":"co_viewed","percentage":50},{"key":"co_viewed_weighted","percentage":50}]""", "[]"),
        new("vip", "s3", 100, "[]", """[{"property":"plan","op":"in","value":["pro","Team"]},{"property":"orders","op":"gte","value":3},{"property":"email","op":"not_contains","value":"@example.test"}]"""),
    ];

    [Fact]
    public void Local_evaluation_matches_the_recommender_for_every_identity()
    {
        var local = LocalFlag.ParseDefinitions(Definitions).ToDictionary(f => f.Key);
        var server = Rules.ToDictionary(r => r.Key, r => new CompiledFlag(r.Key, r.Salt, r.Rollout, FlagRules.ParseVariants(r.Variants), FlagRules.ParseConditions(r.Conditions)));
        Dictionary<string, string>[] people =
        [
            [],
            new() { ["plan"] = "PRO", ["orders"] = "3", ["email"] = "a@example.com" },
            new() { ["plan"] = "team", ["orders"] = "2.5", ["email"] = "b@example.com" },
            new() { ["plan"] = "pro", ["orders"] = "10", ["email"] = "c@example.test" },
        ];

        var mismatches = 0;
        for (var i = 0; i < 2000; i++)
        {
            var identity = $"user-{i}";
            foreach (var props in people)
                foreach (var key in server.Keys)
                {
                    var expected = server[key].Evaluate(identity, props);
                    var actual = local[key].Evaluate(identity, props);
                    if (expected.Enabled != actual.Enabled || expected.Variant != actual.Variant) mismatches++;
                }
        }
        Assert.Equal(0, mismatches);
        Assert.Equal(FlagHashing.Bucket("k", "s", "r", "ünïcode-id"), LocalFlag.Bucket("k", "s", "r", "ünïcode-id"));
    }

    [Fact]
    public void Events_are_written_with_the_server_library_and_any_property_shape()
    {
        var at = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var json = Encoding.UTF8.GetString(EventJson.Batch([
            new ServerEvent("purchase", "user-1", new Dictionary<string, object?>
            {
                ["amount"] = 49.9m,
                ["count"] = 2,
                ["paid"] = true,
                ["tags"] = new[] { "a", "b" },
                ["meta"] = new Dictionary<string, string> { ["k"] = "v" },
                ["broken"] = double.NaN,
                ["$lib"] = "spoofed",
            }, at, "evt-1"),
        ]));
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement.GetProperty("batch")[0];
        var p = e.GetProperty("properties");

        Assert.Equal("2026-03-01T09:00:00.0000000+00:00", e.GetProperty("timestamp").GetString());
        Assert.Equal("evt-1", e.GetProperty("event_id").GetString());
        Assert.Equal(49.9m, p.GetProperty("amount").GetDecimal());
        Assert.True(p.GetProperty("paid").GetBoolean());
        Assert.Equal("b", p.GetProperty("tags")[1].GetString());
        Assert.Equal("v", p.GetProperty("meta").GetProperty("k").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("broken").ValueKind);
        Assert.Equal("dotnet", p.GetProperty("$lib").GetString());
    }

    sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request, body));
            return respond(request, Requests.Count);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static HttpResponseMessage Accepted(string body) =>
        Json(HttpStatusCode.Accepted, $$"""{"accepted":{{JsonDocument.Parse(body).RootElement.GetProperty("batch").GetArrayLength()}},"rejected":0,"duplicates":0}""");

    static ReapDeepClient Client(FakeHandler handler, int batchSize = 500, int maxQueue = 10_000, bool exposures = true, bool background = true) =>
        new(new ReapDeepOptions
        {
            ServerKey = "sk_test",
            FlushInterval = TimeSpan.FromHours(1),
            BatchSize = batchSize,
            MaxQueueSize = maxQueue,
            SendFlagExposures = exposures,
        }, new HttpClient(handler), background)
        { Delay = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task Transient_failures_are_retried_with_the_same_body_and_the_server_key()
    {
        FakeHandler handler = null!;
        handler = new FakeHandler((_, n) => n < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Accepted(handler.Requests[^1].Body));
        await using var client = Client(handler);

        client.Capture("signup", "user-1");
        await client.FlushAsync();

        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(r => r.Body).Distinct());
        Assert.All(handler.Requests, r => Assert.Equal("Bearer sk_test", r.Request.Headers.Authorization!.ToString()));
        Assert.EndsWith("/batch", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal(0, client.QueuedEvents);
    }

    [Fact]
    public async Task A_rejected_batch_is_not_retried_and_counts_as_dropped()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.Unauthorized, "A server key is required"));
        await using var client = Client(handler);

        client.Capture("signup", "user-1");
        var ex = await Assert.ThrowsAsync<ReapDeepException>(() => client.FlushAsync());

        Assert.Equal(401, ex.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal(1, client.DroppedEvents);
    }

    [Fact]
    public void Retry_after_from_the_server_wins_over_backoff()
    {
        var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), ReapDeepClient.RetryDelay(res, 3));
        Assert.Equal(TimeSpan.FromSeconds(2), ReapDeepClient.RetryDelay(null, 3));
        Assert.Equal(TimeSpan.FromSeconds(30), ReapDeepClient.RetryDelay(null, 12));
    }

    [Fact]
    public async Task Import_sends_chunks_of_5000_with_event_ids_and_sums_the_results()
    {
        var handler = new FakeHandler((req, _) => Accepted(req.Content!.ReadAsStringAsync().Result));
        await using var client = Client(handler);

        var result = await client.ImportAsync(Enumerable.Range(0, 12_001).Select(i => new ServerEvent("order", $"c{i}", Timestamp: DateTimeOffset.UtcNow.AddDays(-30))));

        Assert.Equal(new ImportResult(12_001, 0, 0), result);
        Assert.Equal([5000, 5000, 2001], handler.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("batch").GetArrayLength()));
        Assert.All(handler.Requests, r => Assert.All(JsonDocument.Parse(r.Body).RootElement.GetProperty("batch").EnumerateArray(),
            e => Assert.False(string.IsNullOrEmpty(e.GetProperty("event_id").GetString()))));
    }

    [Fact]
    public async Task A_full_queue_drops_new_events_and_dispose_flushes_the_rest()
    {
        var handler = new FakeHandler((req, _) => Accepted(req.Content!.ReadAsStringAsync().Result));
        var client = Client(handler, batchSize: 2, maxQueue: 3, background: false);

        for (var i = 0; i < 5; i++) client.Capture("view", $"u{i}");
        Assert.Equal(2, client.DroppedEvents);
        await client.DisposeAsync();

        Assert.Equal(3, handler.Requests.Sum(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("batch").GetArrayLength()));
    }

    [Fact]
    public async Task Flags_are_evaluated_locally_and_each_exposure_is_sent_once()
    {
        var handler = new FakeHandler((req, _) => req.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, Definitions)
            : Accepted(req.Content!.ReadAsStringAsync().Result));
        await using var client = Client(handler, background: false);

        Assert.Null(client.GetFlag("vip", "user-1"));
        await client.LoadFlagsAsync();
        var vipProps = new Dictionary<string, object?> { ["plan"] = "Pro", ["orders"] = 5, ["email"] = "a@example.com", ["tags"] = new[] { "x" } };

        Assert.True(client.IsEnabled("vip", "user-1", vipProps));
        Assert.True(client.IsEnabled("vip", "user-1", vipProps));
        Assert.False(client.IsEnabled("vip", "user-1", new Dictionary<string, object?> { ["plan"] = "free" }));
        Assert.Null(client.GetFlag("unknown", "user-1"));
        var variant = client.GetFlag("similar-model", "user-1")!.Value.Variant;
        Assert.Contains(variant, new[] { "co_viewed", "co_viewed_weighted" });
        Assert.Equal(3, client.GetAllFlags("user-1").Count);
        await client.FlushAsync();

        var exposures = handler.Requests.Where(r => r.Request.Method == HttpMethod.Post)
            .SelectMany(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("batch").EnumerateArray())
            .Where(e => e.GetProperty("event").GetString() == "$feature_flag_called")
            .Select(e => e.GetProperty("properties").GetProperty("$feature_flag_response").ToString()).ToList();
        Assert.Equal(["True", "False", variant!], exposures);
        Assert.Equal("Bearer sk_test", handler.Requests[0].Request.Headers.Authorization!.ToString());
        Assert.EndsWith("/flags/definitions", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Server_timestamps_are_kept_as_sent_within_a_year_and_an_hour_ahead()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now, CaptureEndpoint.ServerTimestamp(null, now));
        Assert.Equal(now.AddDays(-300), CaptureEndpoint.ServerTimestamp(new DateTimeOffset(now.AddDays(-300)), now));
        Assert.Null(CaptureEndpoint.ServerTimestamp(new DateTimeOffset(now.AddDays(-365)), now));
        Assert.Null(CaptureEndpoint.ServerTimestamp(new DateTimeOffset(now.AddHours(2)), now));
    }

    [Fact]
    public void Server_events_take_the_ip_from_the_ip_property_and_remove_it()
    {
        var props = new Dictionary<string, JsonElement> { ["$ip"] = JsonDocument.Parse("\"203.0.113.77\"").RootElement, ["plan"] = JsonDocument.Parse("\"pro\"").RootElement };

        Assert.Equal("203.0.113.0", CaptureEndpoint.EventIp(props, anonymize: true));
        Assert.False(props.ContainsKey("$ip"));
        Assert.Null(CaptureEndpoint.EventIp(new Dictionary<string, JsonElement> { ["$ip"] = JsonDocument.Parse("\"not-an-ip\"").RootElement }, false));
        Assert.Null(CaptureEndpoint.EventIp(null, false));
    }
}
