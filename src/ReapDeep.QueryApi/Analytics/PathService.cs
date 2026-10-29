using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class PathService(StarRocksClient starRocks)
{
    const int NodesPerStep = 8;
    const int MaxPaths = 25;

    sealed record Plan(string StartEvent, int Depth, SqlQuery Steps, SqlQuery Sequences);

    public Task<ExplainResponse> ExplainAsync(ProjectContext project, PathsRequest req, CancellationToken ct)
    {
        var plan = BuildPlan(project, req);
        return starRocks.ExplainAsync([plan.Steps, plan.Sequences], ct);
    }

    public async Task<PathsResponse> RunAsync(ProjectContext project, PathsRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = BuildPlan(project, req);
        var stepRows = starRocks.QueryAsync(plan.Steps, r => (Step: Convert.ToInt32(r.GetValue(0)), Node: r.GetString(1), Persons: r.GetInt64(2)), ct);
        var pathRows = starRocks.QueryAsync(plan.Sequences, r => (Path: r.GetString(0), Persons: r.GetInt64(1)), ct);
        await Task.WhenAll(stepRows, pathRows);

        var steps = stepRows.Result
            .GroupBy(r => r.Step)
            .OrderBy(g => g.Key)
            .Select(g => new PathStep(g.Key, g.Sum(r => r.Persons), g
                .OrderByDescending(r => r.Persons).ThenBy(r => r.Node, StringComparer.Ordinal)
                .Take(NodesPerStep)
                .Select(r => new PathNode(r.Node, r.Persons))
                .ToList()))
            .ToList();
        var paths = pathRows.Result.Select(r => new PathSequence(r.Path.Split(PathSql.Separator).ToList(), r.Persons)).ToList();

        return new PathsResponse(plan.StartEvent, plan.Depth, steps.FirstOrDefault()?.Persons ?? 0, steps, paths, sw.Elapsed.TotalMilliseconds);
    }

    static Plan BuildPlan(ProjectContext project, PathsRequest req)
    {
        var startEvent = Ensure.NotBlank(req.StartEvent, "start_event");
        var depth = Ensure.InRange(req.Depth ?? 4, 2, 6, "depth");
        var window = Ensure.InRange(req.WindowSeconds ?? 86_400, 60, 7 * 86_400, "window_seconds");
        var pageviewPaths = req.PageviewPaths ?? true;
        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(7));

        SqlBuilder NewBuilder()
        {
            var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
            b.AddFilters(req.Filters);
            return b;
        }
        return new Plan(startEvent, depth,
            PathSql.Steps(NewBuilder(), startEvent, depth, window, pageviewPaths),
            PathSql.Sequences(NewBuilder(), startEvent, depth, window, pageviewPaths, MaxPaths));
    }
}
