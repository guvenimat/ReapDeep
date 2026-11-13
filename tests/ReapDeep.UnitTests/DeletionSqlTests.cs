using ReapDeep.Analytics.Sql;

namespace ReapDeep.UnitTests;

public class DeletionSqlTests
{
    [Fact]
    public void Events_are_deleted_by_project_and_parameterised_distinct_ids()
    {
        var q = DeletionSql.DeleteEvents(7, ["a", "b'c"]);
        Assert.Matches(@"^DELETE FROM analytics\.events WHERE project_id = @p\d+ AND distinct_id IN \(@p\d+, @p\d+\)$", q.Sql);
        Assert.DoesNotContain("b'c", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is "b'c");
        Assert.Contains(q.Parameters, p => p.Value is 7L);
    }

    [Fact]
    public void Tombstones_are_purged_only_from_known_tables()
    {
        var cutoff = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var q = DeletionSql.PurgeTombstones("analytics.persons", cutoff);
        Assert.Equal("DELETE FROM analytics.persons WHERE is_deleted = 1 AND updated_at < @cutoff", q.Sql);
        Assert.Equal(("@cutoff", (object)cutoff), q.Parameters.Single());
        Assert.Throws<ArgumentException>(() => DeletionSql.PurgeTombstones("analytics.events", cutoff));
    }
}
