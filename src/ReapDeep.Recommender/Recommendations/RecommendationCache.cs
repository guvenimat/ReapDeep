using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace ReapDeep.Recommender.Recommendations;

public sealed class RecommendationCache(NpgsqlDataSource db, IConfiguration cfg, ILogger<RecommendationCache> log) : BackgroundService
{
    readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(int.TryParse(cfg["Recommender:RefreshSeconds"], out var s) && s > 0 ? s : 15);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    internal sealed record VersionRecord(long ProjectId, long Items, DateTime? ItemsUpdated, DateTime? Calculated);
    internal sealed record ItemRecord(string ItemId, string Title, string Category, decimal? Price, string Currency, bool InStock, string Url, string ImageUrl);
    internal sealed record ListRecord(string Model, string SourceItemId, string RecommendedItems);

    readonly ConcurrentDictionary<long, (VersionRecord Version, CatalogSnapshot Snapshot)> _projects = new();

    public bool IsLoaded { get; private set; }

    public CatalogSnapshot Get(long projectId) => _projects.TryGetValue(projectId, out var p) ? p.Snapshot : CatalogSnapshot.Empty;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
                IsLoaded = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Failed to refresh the recommendation cache");
            }
            await Task.Delay(IsLoaded ? _refreshInterval : RetryInterval, ct);
        }
    }

    async Task RefreshAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var versions = (await conn.QueryAsync<VersionRecord>("""
            SELECT p.id AS project_id,
                   (SELECT count(*) FROM items i WHERE i.project_id = p.id) AS items,
                   (SELECT max(i.updated_at) FROM items i WHERE i.project_id = p.id) AS items_updated,
                   (SELECT max(r.calculated_at) FROM recommendation_runs r WHERE r.project_id = p.id) AS calculated
            FROM projects p
            """)).AsList();

        foreach (var v in versions)
        {
            if (_projects.TryGetValue(v.ProjectId, out var current) && current.Version == v) continue;
            if (v.Items == 0)
            {
                _projects[v.ProjectId] = (v, CatalogSnapshot.Empty);
                continue;
            }
            var snapshot = await LoadAsync(conn, v.ProjectId);
            _projects[v.ProjectId] = (v, snapshot);
            log.LogInformation("Project {Project}: loaded {Items} items and their recommendations", v.ProjectId, snapshot.ItemCount);
        }
        foreach (var gone in _projects.Keys.Except(versions.Select(v => v.ProjectId)).ToList()) _projects.TryRemove(gone, out _);
    }

    static async Task<CatalogSnapshot> LoadAsync(NpgsqlConnection conn, long projectId)
    {
        var items = (await conn.QueryAsync<ItemRecord>(
            "SELECT item_id, title, category, price, currency, in_stock, url, image_url FROM items WHERE project_id = @ProjectId",
            new { ProjectId = projectId }))
            .ToFrozenDictionary(i => i.ItemId, i => new CatalogItem(i.ItemId, i.Title, i.Category, i.Price, i.Currency, i.InStock, i.Url, i.ImageUrl));

        var lists = (await conn.QueryAsync<ListRecord>(
            "SELECT model, source_item_id, recommended_items::text AS recommended_items FROM item_recommendations WHERE project_id = @ProjectId",
            new { ProjectId = projectId })).AsList();

        var global = lists.Where(l => l.SourceItemId == "").ToFrozenDictionary(l => l.Model, l => Parse(l.RecommendedItems));
        var similar = lists.Where(l => l.SourceItemId != "").GroupBy(l => l.Model)
            .ToFrozenDictionary(g => g.Key, g => g.ToFrozenDictionary(l => l.SourceItemId, l => Parse(l.RecommendedItems)));
        return new CatalogSnapshot(items, global, similar);
    }

    static Candidate[] Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new Candidate(e.GetProperty("item_id").GetString()!, e.GetProperty("score").GetDouble()))
            .ToArray();
    }
}
