using Npgsql;
using ReapDeep.PersonWorker.Identity;
using static ReapDeep.PersonWorker.Identity.EventConsumer;

namespace ReapDeep.UnitTests;

public class EventConsumerTests
{
    [Fact]
    public void Timeouts_are_retried() => Assert.Equal(Failure.Retry, Classify(new TimeoutException(), false));

    [Fact]
    public void Non_transient_database_errors_are_dead_lettered() =>
        Assert.Equal(Failure.DeadLetter, Classify(new NpgsqlException("constraint"), false));

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NullReferenceException))]
    public void Unexpected_errors_are_dead_lettered_instead_of_stopping_the_host(Type type) =>
        Assert.Equal(Failure.DeadLetter, Classify((Exception)Activator.CreateInstance(type)!, false));

    [Fact]
    public void Shutdown_stops_the_loop_whatever_the_error()
    {
        Assert.Equal(Failure.Stop, Classify(new OperationCanceledException(), true));
        Assert.Equal(Failure.Stop, Classify(new InvalidOperationException(), true));
        Assert.Equal(Failure.Stop, Classify(new TimeoutException(), true));
    }
}
