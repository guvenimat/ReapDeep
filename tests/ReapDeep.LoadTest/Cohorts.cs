using System.Diagnostics;
using Npgsql;

namespace ReapDeep.LoadTest;

static class Cohorts
{
    public const string Heavy = "Bench: 20+ pageviews in 30 days";
    public const string NoPurchase = "Bench: signed up, no purchase in 7 days";
    public const string Pro = "Bench: pro plan";
    public const string Champions = "Bench: RFM champions and loyal";

    static readonly (string Name, string Definition)[] Definitions =
    [
        (Heavy, """{"groups":[{"conditions":[{"type":"performed_event","event":"$pageview","count_op":"gte","count":20,"within_days":30}]}]}"""),
        (NoPurchase, """{"groups":[{"conditions":[{"type":"performed_event","event":"signup","count_op":"gte","count":1,"within_days":7},{"type":"not_performed_event","event":"purchase","within_days":7}]}]}"""),
        (Pro, """{"groups":[{"conditions":[{"type":"person_property","property":"plan","op":"eq","value":"pro"}]}]}"""),
        (Champions, """{"groups":[{"conditions":[{"type":"rfm_segment","segments":["champions","loyal"],"within_days":365}]}]}"""),
    ];

    public static async Task Run(string pg, int timeoutSeconds)
    {
        await using var db = NpgsqlDataSource.Create(pg);
        await using (var cmd = db.CreateCommand($"DELETE FROM cohorts WHERE project_id = {Setup.BenchProjectId}"))
            await cmd.ExecuteNonQueryAsync();
        foreach (var (name, definition) in Definitions)
        {
            await using var cmd = db.CreateCommand(
                $"INSERT INTO cohorts (project_id, name, kind, definition) VALUES ({Setup.BenchProjectId}, @name, 'dynamic', @definition::jsonb)");
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("definition", definition);
            await cmd.ExecuteNonQueryAsync();
        }
        Console.WriteLine("Waiting for the person worker to calculate the benchmark cohorts...");

        var sw = Stopwatch.StartNew();
        List<string[]> rows;
        while (true)
        {
            rows = [];
            await using (var cmd = db.CreateCommand($"""
                SELECT name, status, member_count, calculation_ms, last_error FROM cohorts
                WHERE project_id = {Setup.BenchProjectId} AND calculated_at IS NOT NULL ORDER BY id
                """))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    rows.Add([r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "-" : Stats.F(r.GetInt64(2), "N0"),
                        r.IsDBNull(3) ? "-" : $"{Stats.F(r.GetInt32(3), "N0")} ms", r.IsDBNull(4) ? "" : r.GetString(4)]);
            if (rows.Count == Definitions.Length || sw.Elapsed.TotalSeconds > timeoutSeconds) break;
            await Task.Delay(2000);
        }

        Report.Heading($"Cohort calculation (project {Setup.BenchProjectId}, first calculation by the person worker)");
        Report.Table(["Cohort", "Status", "Persons", "Calculation", "Error"], rows);
        if (rows.Count < Definitions.Length) throw new TimeoutException("Cohorts were not calculated in time");
    }
}
