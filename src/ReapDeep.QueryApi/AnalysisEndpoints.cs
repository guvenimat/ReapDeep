using System.Diagnostics;
using MySqlConnector;

namespace ReapDeep.QueryApi;

public static class AnalysisEndpoints
{
    static readonly DateTime Epoch = new(1970, 1, 5);

    public static async Task<IResult> Retention(RetentionRequest req, HttpContext ctx, MySqlDataSource db, IConfiguration cfg)
    {
        var sw = Stopwatch.StartNew();
        var project = ProjectContext.From(ctx);
        if (string.IsNullOrEmpty(req.StartEvent)) throw new QueryException("start_event is required");
        var returnEvent = string.IsNullOrEmpty(req.ReturnEvent) ? req.StartEvent : req.ReturnEvent;
        var period = req.Period ?? "week";
        if (period is not ("day" or "week")) throw new QueryException("period: day | week");
        var periods = req.Periods ?? (period == "week" ? 8 : 14);
        if (periods is < 1 or > 30) throw new QueryException("periods must be between 1 and 30");

        var cohorts = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, period == "week" ? TimeSpan.FromDays(56) : TimeSpan.FromDays(14));
        var step = period == "week" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        var activityEnd = Min(cohorts.ToUtc + step * (periods + 1), DateTime.UtcNow.AddMinutes(1));
        var activity = activityEnd > cohorts.FromUtc
            ? TimeRange.Parse(cohorts.FromUtc.ToString("o"), activityEnd.ToString("o"), project.TimeZone, TimeSpan.Zero)
            : cohorts;

        var b = new SqlBuilder(project.ProjectId, activity.FromUtc, activity.ToUtc);
        b.AddFilters(req.Filters);
        var sql = Queries.Retention(b, req.StartEvent, returnEvent, period, periods, activity, project.TimeZoneId, cohorts.ToUtc);
        if (req.Explain) return await Endpoints.Explain(db, cfg, [(sql, b)], ctx.RequestAborted);

        var rows = await Endpoints.Run(db, sql, b, r => (Cohort: Convert.ToInt64(r.GetValue(0)), K: Convert.ToInt64(r.GetValue(1)), N: r.GetInt64(2)), ctx.RequestAborted);
        var sizes = rows.Where(r => r.K < 0).ToDictionary(r => r.Cohort, r => r.N);
        var counts = rows.Where(r => r.K >= 0).ToDictionary(r => (r.Cohort, r.K), r => r.N);

        var nowIndex = Index(period, TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone));
        var first = Index(period, cohorts.FromLocal);
        var last = Index(period, cohorts.ToLocal.AddTicks(-1));
        var result = new List<RetentionCohort>();
        for (var c = first; c <= last; c++)
        {
            var size = sizes.GetValueOrDefault(c);
            var cells = Enumerable.Range(0, periods + 1)
                .Select(k => c + k > nowIndex ? (long?)null : counts.GetValueOrDefault((c, (long)k)))
                .ToList();
            result.Add(new RetentionCohort(TimeRange.Format(Start(period, c)), size, cells));
        }

        var average = Enumerable.Range(0, periods + 1).Select(k =>
        {
            var eligible = result.Where(r => r.Size > 0 && r.Counts[k] is not null).ToList();
            var total = eligible.Sum(r => r.Size);
            return total == 0 ? (double?)null : Math.Round(eligible.Sum(r => (double)r.Counts[k]!.Value) / total, 4);
        }).ToList();

        return Results.Json(new RetentionResponse(period, periods, project.TimeZoneId, result, average, sw.Elapsed.TotalMilliseconds),
            AppJsonContext.Default.RetentionResponse);
    }

    public static async Task<IResult> Paths(PathsRequest req, HttpContext ctx, MySqlDataSource db, IConfiguration cfg)
    {
        var sw = Stopwatch.StartNew();
        var project = ProjectContext.From(ctx);
        if (string.IsNullOrEmpty(req.StartEvent)) throw new QueryException("start_event is required");
        var depth = req.Depth ?? 4;
        if (depth is < 2 or > 6) throw new QueryException("depth must be between 2 and 6");
        var window = req.WindowSeconds ?? 86_400;
        if (window is < 60 or > 7 * 86_400) throw new QueryException("window_seconds must be between 60 seconds and 7 days");
        var pageviewPaths = req.PageviewPaths ?? true;

        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(7));
        SqlBuilder NewBuilder()
        {
            var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
            b.AddFilters(req.Filters);
            return b;
        }
        var stepsB = NewBuilder();
        var stepsSql = Queries.PathSteps(stepsB, req.StartEvent, depth, window, pageviewPaths);
        var pathsB = NewBuilder();
        var pathsSql = Queries.PathSequences(pathsB, req.StartEvent, depth, window, pageviewPaths, 25);
        if (req.Explain) return await Endpoints.Explain(db, cfg, [(stepsSql, stepsB), (pathsSql, pathsB)], ctx.RequestAborted);

        var stepRows = Endpoints.Run(db, stepsSql, stepsB, r => (Step: Convert.ToInt32(r.GetValue(0)), Node: r.GetString(1), N: r.GetInt64(2)), ctx.RequestAborted);
        var pathRows = Endpoints.Run(db, pathsSql, pathsB, r => (Path: r.GetString(0), N: r.GetInt64(1)), ctx.RequestAborted);
        await Task.WhenAll(stepRows, pathRows);

        var steps = stepRows.Result.GroupBy(r => r.Step).OrderBy(g => g.Key).Select(g => new PathStep(
            g.Key, g.Sum(r => r.N),
            g.OrderByDescending(r => r.N).ThenBy(r => r.Node, StringComparer.Ordinal).Take(8).Select(r => new PathNode(r.Node, r.N)).ToList())).ToList();
        var paths = pathRows.Result.Select(r => new PathSequence(r.Path.Split(Queries.PathSeparator).ToList(), r.N)).ToList();
        var persons = steps.FirstOrDefault()?.Persons ?? 0;

        return Results.Json(new PathsResponse(req.StartEvent, depth, persons, steps, paths, sw.Elapsed.TotalMilliseconds),
            AppJsonContext.Default.PathsResponse);
    }

    static long Index(string period, DateTime local)
    {
        var days = (long)Math.Floor((local.Date - Epoch).TotalDays);
        return period == "week" ? (long)Math.Floor(days / 7.0) : days;
    }

    static DateTime Start(string period, long index) => period == "week" ? Epoch.AddDays(index * 7) : Epoch.AddDays(index);

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
