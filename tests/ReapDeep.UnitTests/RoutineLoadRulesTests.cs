using ReapDeep.PersonWorker.Operations;

namespace ReapDeep.UnitTests;

public class RoutineLoadRulesTests
{
    static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    static RoutineLoadSnapshot Job(string state, long committed = 10, long aborted = 0, string reason = "", string other = "") =>
        new("events_from_kafka", state, committed, aborted, 1000, 0, reason, other);

    [Fact]
    public void A_job_paused_by_an_outage_is_resumed() =>
        Assert.Equal(RoutineLoadAction.Resume, RoutineLoadRules.Decide(null,
            Job("PAUSED", reason: "ErrorReason{errCode = 3, msg='failed to create task: Tablet lost replicas. Check if any backend is down or not.'}"), null, Now));

    [Fact]
    public void A_job_an_operator_paused_is_left_alone() =>
        Assert.Equal(RoutineLoadAction.None, RoutineLoadRules.Decide(null,
            Job("PAUSED", reason: "ErrorReason{errCode = 100, msg='User root pauses routine load job'}"), null, Now));

    [Fact]
    public void Resuming_waits_for_the_cooldown() =>
        Assert.Equal(RoutineLoadAction.None, RoutineLoadRules.Decide(null, Job("PAUSED", reason: "backend down"), Now.AddSeconds(-20), Now));

    [Fact]
    public void A_job_paused_by_bad_data_is_reported_not_resumed() =>
        Assert.Equal(RoutineLoadAction.ReportBlocked, RoutineLoadRules.Decide(null, Job("PAUSED", reason: "too many parse error rows"), null, Now));

    [Fact]
    public void A_running_job_that_keeps_aborting_on_a_parse_error_is_blocked()
    {
        var before = Job("RUNNING", committed: 1299, aborted: 300, other: "previous task aborted because of parse error. illegal json started with [?]");
        var after = before with { AbortedTasks = 310 };
        Assert.Equal(RoutineLoadAction.ReportBlocked, RoutineLoadRules.Decide(before, after, null, Now));
    }

    [Fact]
    public void A_job_that_commits_again_is_healthy()
    {
        var before = Job("RUNNING", committed: 1299, aborted: 300, other: "previous task aborted because of parse error.");
        Assert.Equal(RoutineLoadAction.None, RoutineLoadRules.Decide(before, before with { CommittedTasks = 1300, AbortedTasks = 301 }, null, Now));
        Assert.Equal(RoutineLoadAction.None, RoutineLoadRules.Decide(null, before, null, Now));
    }

    [Fact]
    public void Filtered_rows_are_counted_from_the_previous_check()
    {
        var before = Job("RUNNING") with { ErrorRows = 5 };
        Assert.Equal(0, RoutineLoadRules.NewErrorRows(null, before));
        Assert.Equal(0, RoutineLoadRules.NewErrorRows(before, before));
        Assert.Equal(3, RoutineLoadRules.NewErrorRows(before, before with { ErrorRows = 8 }));
        Assert.Equal(0, RoutineLoadRules.NewErrorRows(before, before with { ErrorRows = 0 }));
    }

    [Fact]
    public void Statistic_json_is_read_defensively()
    {
        Assert.Equal((1287L, 268L, 2189536L, 0L), RoutineLoadRules.ParseStatistic(
            """{"receivedBytes":1,"errorRows":0,"committedTaskNum":1287,"loadedRows":2189536,"abortedTaskNum":268}"""));
        Assert.Equal(default, RoutineLoadRules.ParseStatistic("not json"));
        Assert.Equal(default, RoutineLoadRules.ParseStatistic(null));
    }
}
