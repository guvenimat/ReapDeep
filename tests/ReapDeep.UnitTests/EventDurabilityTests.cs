using System.Text;
using Confluent.Kafka;
using ReapDeep.Ingestion.Capture;
using ReapDeep.PersonWorker.Identity;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.UnitTests;

public class EventDurabilityTests
{
    [Theory]
    [InlineData(5024, "53400", "Query reached its timeout of 1 seconds, please increase the 'query_timeout' session variable and retry", StarRocksFailure.ResourceLimit)]
    [InlineData(1064, "HY000", "Memory of Query01a1 exceed limit. Pipeline Backend: 127.0.0.1", StarRocksFailure.ResourceLimit)]
    [InlineData(1064, "HY000", "Mem usage has exceed the limit of single query", StarRocksFailure.ResourceLimit)]
    [InlineData(1064, "HY000", "Getting analyzing error. Detail message: Column 'x' cannot be resolved.", StarRocksFailure.QueryError)]
    [InlineData(5502, "42602", "Getting analyzing error. Detail message: Unknown table 'analytics.nope'.", StarRocksFailure.QueryError)]
    [InlineData(1064, "HY000", "Failed to allocate resource to query: pending timeout [15s], you could modify the session variable [query_queue_pending_timeout_second] to pending more time", StarRocksFailure.Busy)]
    [InlineData(1064, "HY000", "Failed to allocate resource to query: com.starrocks.common.StarRocksException: Resource is not enough and the number of pending queries exceeds capacity [1024], you could modify the session variable [query_queue_max_queued_queries] to make more query can be queued", StarRocksFailure.Busy)]
    [InlineData(1064, "HY000", "Exceed concurrency limit: 8 backend [id=10001] [host=127.0.0.1]", StarRocksFailure.Busy)]
    [InlineData(1042, null, "Unable to connect to any of the specified MySQL hosts.", StarRocksFailure.Unavailable)]
    [InlineData(1064, "HY000", "Backend node not found. Check if any backend node is down.backend: [127.0.0.1 alive: true inBlacklist: true]", StarRocksFailure.Unavailable)]
    public void StarRocks_errors_are_classified_by_code_first(int number, string? sqlState, string message, StarRocksFailure expected) =>
        Assert.Equal(expected, StarRocksErrors.Classify(number, sqlState, message));

    [Fact]
    public void The_rejected_cancel_cleanup_statement_is_classified_as_a_canceled_query()
    {
        const string message = "Getting syntax error at line 1, column 21. Detail message: Unexpected input '@', the most similar input is {'OUTFILE'}.";
        Assert.True(StarRocksErrors.IsCancelCleanup(1064, message));
        Assert.Equal(StarRocksFailure.ResourceLimit, StarRocksErrors.Classify(1064, "HY000", message));
    }

    [Theory]
    [InlineData(1064, "Getting syntax error at line 1, column 40. Detail message: Unexpected input '@', the most similar input is {<EOF>}.")]
    [InlineData(1064, "Getting syntax error at line 1, column 21. Detail message: Unexpected input 'x', the most similar input is {'FROM'}.")]
    [InlineData(5502, "Getting syntax error at line 1, column 21. Detail message: Unexpected input '@', the most similar input is {'OUTFILE'}.")]
    public void Other_syntax_errors_stay_query_errors(int number, string message)
    {
        Assert.False(StarRocksErrors.IsCancelCleanup(number, message));
        Assert.Equal(StarRocksFailure.QueryError, StarRocksErrors.Classify(number, "HY000", message));
    }

    [Fact]
    public void A_timeout_mentioned_in_an_ordinary_error_is_not_a_resource_limit() =>
        Assert.Equal(StarRocksFailure.QueryError, StarRocksErrors.Classify(5502, "42602", "Unknown column 'timeout'"));

    [Fact]
    public void Dedup_keys_are_scoped_to_the_project()
    {
        Assert.Equal("dedup:7:evt-1", EventDeduplicator.Key(7, "evt-1"));
        Assert.NotEqual(EventDeduplicator.Key(7, "evt-1"), EventDeduplicator.Key(8, "evt-1"));
    }

    [Fact]
    public void Dead_letters_keep_the_stage_the_error_and_the_source_offset()
    {
        var headers = DeadLetterProducer.HeadersFor("identity", new string('x', 5000), new TopicPartitionOffset("events", 3, 42));
        Assert.Equal("identity", Encoding.UTF8.GetString(headers.GetLastBytes(DeadLetterProducer.StageHeader)));
        Assert.Equal(1000, headers.GetLastBytes(DeadLetterProducer.ErrorHeader).Length);
        Assert.Equal("events/3/42", Encoding.UTF8.GetString(headers.GetLastBytes(DeadLetterProducer.SourceHeader)));
    }
}
