using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ReapDeep.Analytics.Sql;
using ReapDeep.PersonWorker.Infrastructure;

namespace ReapDeep.PersonWorker.Recommendations;

public sealed record RecommendationOptions(TimeSpan PollInterval, TimeSpan RefreshInterval)
{
    public static RecommendationOptions From(IConfiguration cfg) => new(
        cfg.Seconds("Recommendations:PollSeconds", 30),
        cfg.Seconds("Recommendations:RefreshSeconds", 3_600));
}

public sealed class RecommendationRefreshJob(RecommendationRepository repository, StarRocksStore? store, RecommendationOptions options,
    ILogger<RecommendationRefreshJob> log) : BackgroundService
{
    readonly record struct Row(string Source, string ItemId, double Score);
    readonly record struct HistoryRow(string Person, string ItemId, int Weight, DateTime At);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (store is null)
        {
            log.LogWarning("ConnectionStrings:StarRocks is not set; recommendations will not be calculated");
            return;
        }
        log.LogInformation("Recommendation job: refresh every {Refresh}", options.RefreshInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var project in await repository.DueProjectsAsync(options.RefreshInterval, RecommendationSql.Models.Length, ct))
                    await RefreshAsync(store, project, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Recommendation job failed; retrying in the next run");
            }
            await Task.Delay(options.PollInterval, ct);
        }
    }

    async Task RefreshAsync(StarRocksStore store, long projectId, CancellationToken ct)
    {
        await using var lockConn = await repository.TryLockAsync(projectId, ct);
        if (lockConn is null) return;
        try
        {
            foreach (var model in RecommendationSql.Models)
            {
                var sw = Stopwatch.StartNew();
                var rows = await store.QueryAsync(RecommendationSql.Build(model, projectId, DateTime.UtcNow),
                    r => new Row(r.GetString(0), r.GetString(1), Convert.ToDouble(r.GetValue(2))), ct);
                var groups = rows.GroupBy(r => r.Source).ToList();
                await repository.ReplaceAsync(projectId, model, groups.Select(g => g.Key).ToArray(), groups.Select(Json).ToArray(),
                    (int)sw.ElapsedMilliseconds, ct);
                log.LogInformation("Recommendations {Model} (project {Project}): {Sources} source items, {Rows} candidates in {Ms} ms",
                    model, projectId, groups.Count, rows.Count, sw.ElapsedMilliseconds);
            }
            await RefreshHistoryAsync(store, projectId, ct);
        }
        finally
        {
            await RecommendationRepository.UnlockAsync(lockConn, projectId);
        }
    }

    async Task RefreshHistoryAsync(StarRocksStore store, long projectId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rows = await store.QueryAsync(RecommendationSql.PersonHistory(projectId, DateTime.UtcNow),
            r => new HistoryRow(r.GetString(0), r.GetString(1), Convert.ToInt32(r.GetValue(2)), r.GetDateTime(3)), ct);
        var people = rows.GroupBy(r => r.Person).ToList();
        await repository.ReplaceHistoryAsync(projectId, people.Select(g => g.Key).ToArray(), people.Select(HistoryJson).ToArray(), ct);
        log.LogInformation("Person history (project {Project}): {People} people, {Rows} items in {Ms} ms",
            projectId, people.Count, rows.Count, sw.ElapsedMilliseconds);
    }

    static string HistoryJson(IEnumerable<HistoryRow> items)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var i in items)
            {
                w.WriteStartObject();
                w.WriteString("item_id", i.ItemId);
                w.WriteNumber("weight", i.Weight);
                w.WriteNumber("at", new DateTimeOffset(DateTime.SpecifyKind(i.At, DateTimeKind.Utc)).ToUnixTimeSeconds());
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static string Json(IEnumerable<Row> candidates)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var c in candidates)
            {
                w.WriteStartObject();
                w.WriteString("item_id", c.ItemId);
                w.WriteNumber("score", Math.Round(c.Score, 6));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
