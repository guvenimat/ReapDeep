using System.Text;
using ReapDeep.Ingestion.Capture;
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
