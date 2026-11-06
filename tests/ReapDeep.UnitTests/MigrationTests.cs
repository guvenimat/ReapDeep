using ReapDeep.Migrations;

namespace ReapDeep.UnitTests;

public class MigrationTests
{
    static readonly IReadOnlyList<Migration> All = MigrationCatalog.Load(typeof(MigrationCatalog).Assembly);

    [Fact]
    public void Migrations_are_embedded_and_ordered_per_target()
    {
        Assert.Equal(["0001", "0002", "0003", "0004", "0005"], All.Where(m => m.Target == MigrationCatalog.Postgres).Select(m => m.Version));
        Assert.Equal(["0001", "0002"], All.Where(m => m.Target == MigrationCatalog.StarRocks).Select(m => m.Version));
        Assert.All(All, m => Assert.False(string.IsNullOrWhiteSpace(m.Sql)));
    }

    [Theory]
    [InlineData("CREATE ROUTINE LOAD analytics.events_from_kafka ON events\nCOLUMNS (a)", "analytics", "events_from_kafka")]
    [InlineData("  create routine load persons_from_kafka on persons", "analytics", "persons_from_kafka")]
    [InlineData("CREATE ROUTINE LOAD other.job_1 ON t", "other", "job_1")]
    public void Routine_load_statements_are_recognized(string statement, string database, string name) =>
        Assert.Equal((database, name), MigrationCatalog.RoutineLoadJob(statement, "analytics"));

    [Fact]
    public void Other_statements_are_not_routine_loads()
    {
        Assert.Null(MigrationCatalog.RoutineLoadJob("CREATE TABLE IF NOT EXISTS events (a INT)", "analytics"));
        Assert.Null(MigrationCatalog.RoutineLoadJob("PAUSE ROUTINE LOAD FOR analytics.events_from_kafka", "analytics"));
    }

    [Fact]
    public void Every_routine_load_in_the_migrations_can_be_skipped_when_it_exists()
    {
        var creates = All.Where(m => m.Target == MigrationCatalog.StarRocks).SelectMany(m => MigrationCatalog.Statements(m.Sql))
            .Where(s => s.Contains("ROUTINE LOAD", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, creates.Count);
        Assert.All(creates, s => Assert.NotNull(MigrationCatalog.RoutineLoadJob(s, "analytics")));
    }

    [Theory]
    [InlineData("Postgres/0003_add_index.sql", "postgres", "0003", "add_index")]
    [InlineData("StarRocks/0010_new_table.sql", "starrocks", "0010", "new_table")]
    public void File_names_give_target_version_and_name(string resource, string target, string version, string name)
    {
        var m = MigrationCatalog.Parse(resource, "SELECT 1");
        Assert.Equal((target, version, name), (m!.Target, m.Version, m.Name));
    }

    [Theory]
    [InlineData("Postgres/3_short.sql")]
    [InlineData("Other/0001_x.sql")]
    [InlineData("Postgres/0001_x.txt")]
    public void Other_resources_are_ignored(string resource) => Assert.Null(MigrationCatalog.Parse(resource, ""));

    [Fact]
    public void StarRocks_statements_are_split_outside_quotes_and_comments_are_dropped()
    {
        var schema = All.Single(m => m.Target == MigrationCatalog.StarRocks && m.Version == "0001").Sql;
        var statements = MigrationCatalog.Statements(schema);
        Assert.Equal(3, statements.Count(s => s.StartsWith("CREATE ROUTINE LOAD", StringComparison.Ordinal)));
        Assert.Contains(statements, s => s.Contains("\\\"$.project_id\\\",\\\"$.event\\\"", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, s => s.Contains("--", StringComparison.Ordinal));
        Assert.Equal("USE analytics", statements[1]);
    }

    [Fact]
    public void Semicolons_inside_strings_do_not_split() =>
        Assert.Equal(["INSERT INTO t VALUES ('a;b', \"c;\\\"d\")", "SELECT 1"],
            MigrationCatalog.Statements("INSERT INTO t VALUES ('a;b', \"c;\\\"d\");\n-- a comment; with a semicolon\nSELECT 1;"));

    [Fact]
    public void Only_unapplied_migrations_of_the_target_are_pending() =>
        Assert.Equal(["0002"], MigrationCatalog.Pending(All, new HashSet<string> { "0001" }, MigrationCatalog.StarRocks).Select(m => m.Version));

    [Fact]
    public void Users_are_created_only_when_their_password_is_set_and_passwords_are_escaped()
    {
        var env = new Dictionary<string, string> { ["STARROCKS_QUERY_PASSWORD"] = "it's" };
        var statements = StarRocksUsers.Statements(k => env.GetValueOrDefault(k)).ToList();
        Assert.All(statements, s => Assert.DoesNotContain("reapdeep_jobs", s));
        Assert.Contains("CREATE USER IF NOT EXISTS 'reapdeep_query'@'%' IDENTIFIED BY 'it''s'", statements);
    }
}
