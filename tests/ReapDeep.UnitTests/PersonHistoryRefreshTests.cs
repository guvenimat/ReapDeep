using System.Text.Json;
using ReapDeep.PersonWorker.Recommendations;
using static ReapDeep.PersonWorker.Recommendations.RecommendationRefreshJob;

namespace ReapDeep.UnitTests;

public class PersonHistoryRefreshTests
{
    static readonly DateTime At = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    static async Task<List<HistoryBatch>> Batches(int size, params (string Person, string Item)[] rows)
    {
        var batches = new List<HistoryBatch>();
        var source = rows.Select(r => new HistoryRow(r.Person, r.Item, 1, At)).ToAsyncEnumerable();
        await foreach (var b in HistoryBatches(source, size)) batches.Add(b);
        return batches;
    }

    [Fact]
    public async Task Rows_are_grouped_per_person_and_split_into_batches()
    {
        var batches = await Batches(2, ("a", "1"), ("a", "2"), ("b", "3"), ("c", "4"), ("c", "5"), ("d", "6"), ("e", "7"));
        Assert.Equal([["a", "b"], ["c", "d"], ["e"]], batches.Select(b => b.People));
        Assert.Equal([3, 3, 1], batches.Select(b => b.Items));
        var first = JsonDocument.Parse(batches[0].Histories[0]).RootElement;
        Assert.Equal(["1", "2"], first.EnumerateArray().Select(e => e.GetProperty("item_id").GetString()));
        Assert.Equal(new DateTimeOffset(At).ToUnixTimeSeconds(), first[0].GetProperty("at").GetInt64());
    }

    [Fact]
    public async Task An_empty_result_gives_no_batches() => Assert.Empty(await Batches(10));

    [Fact]
    public void History_is_upserted_and_stale_rows_are_removed_by_run_start()
    {
        Assert.Contains("ON CONFLICT (project_id, person_key) DO UPDATE", RecommendationRepository.UpsertHistorySql);
        Assert.Contains("@RunStart", RecommendationRepository.UpsertHistorySql);
        Assert.Equal("DELETE FROM person_item_history WHERE project_id = @Project AND calculated_at < @RunStart",
            RecommendationRepository.DeleteStaleHistorySql);
    }
}
