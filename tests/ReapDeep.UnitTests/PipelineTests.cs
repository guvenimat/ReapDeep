using System.Text;
using System.Text.Json;
using ReapDeep.Ingestion.Capture;
using ReapDeep.Ingestion.Kafka;
using ReapDeep.PersonWorker.Identity;

namespace ReapDeep.UnitTests;

public class EventTimestampTests
{
    static readonly DateTime Server = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Client_clock_skew_is_corrected_using_sent_at()
    {
        var sentAt = new DateTimeOffset(Server.AddMinutes(-5));
        var clientTime = new DateTimeOffset(Server.AddMinutes(-6));
        Assert.Equal(Server.AddMinutes(-1), EventTimestamps.For(Server, sentAt).Resolve(clientTime));
    }

    [Fact]
    public void Missing_timestamp_uses_server_time()
    {
        Assert.Equal(Server, EventTimestamps.For(Server, null).Resolve(null));
    }

    [Fact]
    public void Timestamps_far_in_the_future_fall_back_to_server_time()
    {
        Assert.Equal(Server, EventTimestamps.For(Server, null).Resolve(new DateTimeOffset(Server.AddHours(2))));
    }

    [Fact]
    public void Events_older_than_364_days_are_rejected()
    {
        var timestamps = EventTimestamps.For(Server, null);
        Assert.Null(timestamps.Resolve(new DateTimeOffset(Server.AddDays(-365))));
        Assert.NotNull(timestamps.Resolve(new DateTimeOffset(Server.AddDays(-363))));
    }
}

public class IdentityEventTests
{
    static byte[] Message(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Event_time_is_read_from_the_message_in_utc_microseconds()
    {
        var e = IdentityEvent.Parse(Message("""{"project_id":1,"event":"$set","distinct_id":"u","timestamp":"2026-11-06 10:00:00.123456","properties":{"$set":{"a":1}}}"""));
        Assert.Equal(IdentityEvent.ToMicros(new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc)) + 123456, e!.TimestampMicros);
    }

    [Fact]
    public void Missing_event_time_falls_back_to_processing_time()
    {
        var before = IdentityEvent.ToMicros(DateTime.UtcNow);
        var e = IdentityEvent.Parse(Message("""{"project_id":1,"event":"$set","distinct_id":"u","properties":{"$set":{"a":1}}}"""));
        Assert.InRange(e!.TimestampMicros, before, IdentityEvent.ToMicros(DateTime.UtcNow));
    }

    [Fact]
    public void Consumer_lag_sums_known_partitions_only()
    {
        Assert.Equal(15, ReapDeep.PersonWorker.Identity.IdentityMetrics.Lag([(100, 90), (50, 45), (-1001, 3), (20, -1001), (7, 9)]));
    }

    [Fact]
    public void Identify_with_anonymous_id_and_properties_is_parsed()
    {
        var e = IdentityEvent.Parse(Message("""
            {"project_id":7,"event":"$identify","distinct_id":"user-1","properties":{"$anon_distinct_id":"anon-1","$set":{"plan":"pro"}}}
            """));
        Assert.NotNull(e);
        Assert.Equal(7, e.ProjectId);
        Assert.Equal("user-1", e.DistinctId);
        Assert.Equal("anon-1", e.AnonId);
        Assert.Equal("""{"plan":"pro"}""", e.SetJson);
        Assert.True(e.IsIdentify);
        Assert.True(e.HasWork);
    }

    [Fact]
    public void Anonymous_id_equal_to_the_user_id_is_ignored()
    {
        var e = IdentityEvent.Parse(Message("""{"project_id":1,"event":"$identify","distinct_id":"u","properties":{"$anon_distinct_id":"u"}}"""));
        Assert.Null(e!.AnonId);
    }

    [Fact]
    public void Regular_events_without_set_have_no_work()
    {
        var e = IdentityEvent.Parse(Message("""{"project_id":1,"event":"$pageview","distinct_id":"u","properties":{}}"""));
        Assert.False(e!.HasWork);
    }

    [Theory]
    [InlineData("""{"project_id":1,"event":"$pageview","distinct_id":"u"}""", false)]
    [InlineData("""{"event":"$identify"}""", true)]
    [InlineData("""{"properties":{"$set":{}}}""", true)]
    public void Messages_are_prefiltered_without_parsing(string json, bool expected)
    {
        Assert.Equal(expected, IdentityEvent.MayBeRelevant(Message(json)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"event":"$identify","distinct_id":"u"}""")]
    [InlineData("""{"project_id":1,"event":"","distinct_id":"u"}""")]
    public void Malformed_messages_are_rejected(string json)
    {
        Assert.Null(IdentityEvent.Parse(Message(json)));
    }
}

public class EventMessageTests
{
    static JsonElement Build(string properties)
    {
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(properties)!;
        var bytes = EventMessage.Build(1, "purchase", "d1", "e1", DateTime.UtcNow, DateTime.UtcNow, null, default, props);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    [Fact]
    public void Item_id_moves_from_properties_to_its_own_column()
    {
        var msg = Build("""{"item_id":"sku-42","order_id":"o-1"}""");

        Assert.Equal("sku-42", msg.GetProperty("item_id").GetString());
        Assert.False(msg.GetProperty("properties").TryGetProperty("item_id", out _));
        Assert.Equal("o-1", msg.GetProperty("properties").GetProperty("order_id").GetString());
    }

    [Fact]
    public void Numeric_ids_are_stored_as_text_and_missing_ids_as_null()
    {
        Assert.Equal("42", Build("""{"item_id":42}""").GetProperty("item_id").GetString());
        Assert.Equal(JsonValueKind.Null, Build("""{"order_id":"o-1"}""").GetProperty("item_id").ValueKind);
    }

    [Fact]
    public void Values_that_do_not_fit_their_column_stay_in_properties()
    {
        var msg = Build("""{"item_id":{"sku":"a"},"utm_source":["x","y"],"$screen_width":"1920","$screen_height":1080}""");
        var props = msg.GetProperty("properties");

        Assert.Equal(JsonValueKind.Null, msg.GetProperty("item_id").ValueKind);
        Assert.Equal("a", props.GetProperty("item_id").GetProperty("sku").GetString());
        Assert.Equal(2, props.GetProperty("utm_source").GetArrayLength());
        Assert.Equal("1920", props.GetProperty("$screen_width").GetString());
        Assert.Equal(JsonValueKind.Null, msg.GetProperty("screen_width").ValueKind);
        Assert.Equal(1080, msg.GetProperty("screen_height").GetInt32());
        Assert.False(props.TryGetProperty("$screen_height", out _));
    }
}

public class PersonResyncTests
{
    [Fact]
    public void Resync_runs_for_all_projects_or_one()
    {
        Assert.True(PersonResync.TryParseArgs(["resync-persons"], out var all));
        Assert.Null(all);
        Assert.True(PersonResync.TryParseArgs(["resync-persons", "--project", "4"], out var one));
        Assert.Equal(4, one);
    }

    [Fact]
    public void Other_arguments_start_the_worker_and_bad_ones_are_refused()
    {
        Assert.False(PersonResync.TryParseArgs([], out _));
        Assert.False(PersonResync.TryParseArgs(["--Kafka:GroupId=x"], out _));
        Assert.Throws<ArgumentException>(() => PersonResync.TryParseArgs(["resync-persons", "--project", "abc"], out _));
    }
}

public class IngestionHardeningTests
{
    [Fact]
    public void Kafka_key_keeps_the_events_of_one_distinct_id_on_one_partition()
    {
        Assert.Equal("7:user-1", EventProducer.Key(7, "user-1"));
        Assert.NotEqual(EventProducer.Key(7, "user-1"), EventProducer.Key(8, "user-1"));
    }

    [Theory]
    [InlineData("203.0.113.77", true, "203.0.113.0")]
    [InlineData("203.0.113.77", false, "203.0.113.77")]
    [InlineData("::ffff:198.51.100.9", true, "198.51.100.0")]
    [InlineData("2001:db8:85a3:8d3:1319:8a2e:370:7348", true, "2001:db8:85a3::")]
    [InlineData("2001:db8:85a3:8d3:1319:8a2e:370:7348", false, "2001:db8:85a3:8d3:1319:8a2e:370:7348")]
    public void Ip_addresses_are_anonymized_when_the_project_asks_for_it(string address, bool anonymize, string expected)
    {
        Assert.Equal(expected, IpAnonymizer.Format(System.Net.IPAddress.Parse(address), anonymize));
    }

    [Fact]
    public void Missing_ip_stays_missing()
    {
        Assert.Null(IpAnonymizer.Format(null, true));
    }

    [Theory]
    [InlineData(null, 2000, 2000)]
    [InlineData(5000, 2000, 5000)]
    [InlineData(0, 2000, null)]
    [InlineData(null, 0, null)]
    [InlineData(10, 2000, IngestionQuota.MinimumLimit)]
    public void Quota_uses_the_project_limit_then_the_server_default(int? projectLimit, int defaultLimit, int? expected)
    {
        Assert.Equal(expected, IngestionQuota.EffectiveLimit(projectLimit, defaultLimit));
    }

    [Fact]
    public void Quota_windows_are_per_project_and_per_second()
    {
        var t = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(IngestionQuota.WindowKey(1, t), IngestionQuota.WindowKey(1, t.AddMilliseconds(999)));
        Assert.NotEqual(IngestionQuota.WindowKey(1, t), IngestionQuota.WindowKey(1, t.AddSeconds(1)));
        Assert.NotEqual(IngestionQuota.WindowKey(1, t), IngestionQuota.WindowKey(2, t));
    }

    [Theory]
    [InlineData("evt-1", "evt-1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Client_event_ids_are_kept_when_they_fit(string? eventId, string? expected) =>
        Assert.Equal(expected, CaptureEndpoint.ClientEventId(eventId));

    [Fact]
    public void Event_id_limit_counts_utf8_bytes_not_characters()
    {
        Assert.Equal(new string('a', 64), CaptureEndpoint.ClientEventId(new string('a', 64)));
        Assert.Null(CaptureEndpoint.ClientEventId(new string('a', 65)));
        Assert.Equal(new string('\u011f', 32), CaptureEndpoint.ClientEventId(new string('\u011f', 32)));
        Assert.Null(CaptureEndpoint.ClientEventId(new string('\u011f', 40)));
    }

    [Fact]
    public void Only_events_kafka_did_not_acknowledge_release_their_dedup_claim()
    {
        var failed = Task.FromException(new InvalidOperationException("broker down"));
        var deliveries = new List<(Task, string?)> { (Task.CompletedTask, "ok-1"), (failed, "lost-1"), (failed, null), (Task.CompletedTask, "ok-2") };

        Assert.Equal(["lost-1"], CaptureEndpoint.FailedClaims(deliveries));
    }
}
