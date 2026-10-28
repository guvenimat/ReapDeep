using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;

namespace ReapDeep.QueryApi;

public static class Endpoints
{
    public static void MapQueryApi(this WebApplication app)
    {
        app.MapGet("/healthz", Health);

        var api = app.MapGroup("/api/v1").AddEndpointFilter(AuthAndErrors);
        api.MapPost("/query/trends", Trends);
        api.MapPost("/query/funnels", Funnels);
        api.MapGet("/events", Events);
        api.MapGet("/persons", Persons);
        api.MapGet("/persons/{personId}", Person);
        api.MapGet("/metadata", Metadata);
    }

    static async ValueTask<object?> AuthAndErrors(EndpointFilterInvocationContext ic, EndpointFilterDelegate next)
    {
        var ctx = ic.HttpContext;
        var project = await ctx.RequestServices.GetRequiredService<ApiKeyAuth>().Authenticate(ctx);
        if (project is null) return Error("Invalid or missing API key", StatusCodes.Status401Unauthorized);
        ctx.Items[nameof(ProjectContext)] = project;
        try
        {
            return await next(ic);
        }
        catch (QueryException ex)
        {
            return Error(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (MySqlException ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReapDeep.QueryApi").LogError(ex, "StarRocks query failed");
            return ex.Message.Contains("exceed", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? Error("Query exceeded the database resource limits; narrow the date range or filters", StatusCodes.Status503ServiceUnavailable)
                : Error("Query failed in the analytics database", StatusCodes.Status502BadGateway);
        }
    }

    static IResult Error(string message, int status) =>
        Results.Json(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, statusCode: status);

    static async Task<IResult> Trends(TrendRequest req, HttpContext ctx, MySqlDataSource db, IConfiguration cfg)
    {
        var sw = Stopwatch.StartNew();
        var project = ProjectContext.From(ctx);
        if (req.Series is not { Count: > 0 and <= 10 } series) throw new QueryException("series must contain 1-10 items");
        foreach (var s in series)
        {
            if (string.IsNullOrEmpty(s.Event)) throw new QueryException("Each series requires an event");
            if (s.Metric is not (null or "total" or "unique_users")) throw new QueryException($"Invalid metric: '{s.Metric}' (total | unique_users)");
        }
        var interval = req.Interval ?? "day";
        if (!TimeRange.Intervals.Contains(interval)) throw new QueryException("interval: hour | day | week | month");

        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(7));
        var buckets = range.Buckets(interval);

        SqlBuilder NewBuilder()
        {
            var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
            b.AddFilters(req.Filters);
            return b;
        }
        var plans = series.Select(s => (Series: s, Plan: Queries.Trend(NewBuilder, s, req.Deduplicate, interval, range, project.TimeZoneId, req.Breakdown))).ToList();

        if (req.Explain)
            return await Explain(db, cfg, plans.SelectMany(p => new[] { p.Plan.Buckets, p.Plan.Total }).OfType<Queries.Query>().Select(q => (q.Sql, q.Builder)), ctx.RequestAborted);

        Task<List<(DateTime? Bucket, string? Breakdown, long Count)>> Exec(Queries.Query q) => Run(db, q.Sql, q.Builder, r => (
            Bucket: r.IsDBNull(0) ? (DateTime?)null : r.GetDateTime(0),
            Breakdown: r.IsDBNull(1) ? null : r.GetString(1),
            Count: r.GetInt64(2)), ctx.RequestAborted);

        var bucketTasks = plans.Select(p => Exec(p.Plan.Buckets)).ToList();
        var totalTasks = plans.Select(p => p.Plan.Total is { } t ? Exec(t) : null).ToList();
        await Task.WhenAll(bucketTasks.Concat(totalTasks.OfType<Task<List<(DateTime?, string?, long)>>>()));

        var results = new List<TrendResult>();
        for (var i = 0; i < plans.Count; i++)
        {
            var s = plans[i].Series;
            var rows = bucketTasks[i].Result;
            var totals = totalTasks[i]?.Result.ToDictionary(r => r.Breakdown ?? "", r => r.Count);
            var groups = rows.GroupBy(r => r.Breakdown).Select(g => (g.Key, Rows: g.ToList())).ToList();
            if (req.Breakdown is null && groups.Count == 0) groups.Add((null, []));

            var seriesResults = new List<TrendResult>();
            foreach (var (key, grp) in groups)
            {
                var byBucket = grp.Where(r => r.Bucket is not null).ToDictionary(r => r.Bucket!.Value, r => r.Count);
                var total = totals is null ? byBucket.Values.Sum() : totals.GetValueOrDefault(key ?? "");
                seriesResults.Add(new TrendResult(s.Event, s.Metric ?? "total", key, total,
                    buckets.Select(bk => new TrendPoint(TimeRange.Format(bk), byBucket.GetValueOrDefault(bk))).ToList()));
            }
            results.AddRange(seriesResults.OrderByDescending(r => r.Total));
        }

        return Results.Json(new TrendResponse(interval, project.TimeZoneId, results, sw.Elapsed.TotalMilliseconds), AppJsonContext.Default.TrendResponse);
    }

    static async Task<IResult> Funnels(FunnelRequest req, HttpContext ctx, MySqlDataSource db, IConfiguration cfg)
    {
        var sw = Stopwatch.StartNew();
        var project = ProjectContext.From(ctx);
        if (req.Steps is not { Count: >= 2 and <= 8 } steps) throw new QueryException("steps must contain 2-8 items");
        if (steps.Any(s => string.IsNullOrEmpty(s.Event))) throw new QueryException("Each step requires an event");
        var window = req.WindowSeconds ?? 7 * 24 * 3600;
        if (window is < 1 or > 90 * 24 * 3600) throw new QueryException("window_seconds must be between 1 second and 90 days");
        var sampling = req.Sampling ?? 1;
        if (sampling is < 0.01 or > 1) throw new QueryException("sampling must be between 0.01 and 1");

        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(30));
        var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
        b.AddFilters(req.Filters);
        var sql = Queries.Funnel(b, steps, window, req.Breakdown, sampling);

        if (req.Explain) return await Explain(db, cfg, [(sql, b)], ctx.RequestAborted);

        var rows = await Run(db, sql, b, r => (
            Breakdown: r.IsDBNull(0) ? null : r.GetString(0),
            Counts: Enumerable.Range(1, steps.Count)
                .Select(i => r.IsDBNull(i) ? 0L : (long)Math.Round(Convert.ToInt64(r.GetValue(i)) / sampling)).ToArray()), ctx.RequestAborted);

        var results = rows.Select(row => new FunnelResult(row.Breakdown, steps.Select((st, i) => new FunnelStepResult(
            Step: i + 1,
            Event: st.Event,
            Count: row.Counts[i],
            ConversionFromFirst: Ratio(row.Counts[i], row.Counts[0]),
            ConversionFromPrevious: i == 0 ? 1 : Ratio(row.Counts[i], row.Counts[i - 1]))).ToList())).ToList();

        return Results.Json(new FunnelResponse(window, sampling, results, sw.Elapsed.TotalMilliseconds), AppJsonContext.Default.FunnelResponse);
    }

    static double Ratio(long a, long b) => b == 0 ? 0 : Math.Round((double)a / b, 4);

    const int MaxPersonDistinctIds = 1000;

    static async Task<IResult> Events(
        HttpContext ctx, MySqlDataSource db, NpgsqlDataSource pg,
        [FromQuery(Name = "event")] string? eventName, [FromQuery(Name = "distinct_id")] string? distinctId,
        [FromQuery(Name = "person_id")] string? personId, [FromQuery(Name = "date_from")] string? dateFrom,
        [FromQuery(Name = "date_to")] string? dateTo, int? limit)
    {
        var sw = Stopwatch.StartNew();
        var project = ProjectContext.From(ctx);
        var range = TimeRange.Parse(dateFrom, dateTo, project.TimeZone, TimeSpan.FromDays(7));

        IReadOnlyList<string>? personDistinctIds = null;
        string? personIdScan = null;
        if (personId is not null)
        {
            var ids = Guid.TryParse(personId, out var pid) ? await PersonDistinctIds(pg, project.ProjectId, pid, ctx.RequestAborted) : [];
            if (ids.Count == 0) personDistinctIds = [personId];
            else if (ids.Count <= MaxPersonDistinctIds) personDistinctIds = ids;
            else personIdScan = personId;
        }

        var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
        var sql = Queries.Events(b, eventName, distinctId, personDistinctIds, personIdScan, Math.Clamp(limit ?? 50, 1, 200));

        var events = await Run(db, sql, b, r =>
        {
            var columns = new Dictionary<string, string>();
            for (var i = 0; i < ColumnMapper.EventColumnList.Length; i++)
                if (!r.IsDBNull(6 + i)) columns[ColumnMapper.EventColumnList[i]] = r.GetString(6 + i);
            return new EventRow(
                r.GetString(0), r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetString(3), r.GetString(4),
                columns, r.IsDBNull(5) ? null : ParseJson(r.GetString(5)));
        }, ctx.RequestAborted);

        return Results.Json(new EventsResponse(events, sw.Elapsed.TotalMilliseconds), AppJsonContext.Default.EventsResponse);
    }

    static async Task<List<string>> PersonDistinctIds(NpgsqlDataSource pg, long projectId, Guid personId, CancellationToken ct)
    {
        await using var cmd = pg.CreateCommand(
            "SELECT distinct_id FROM person_distinct_ids WHERE project_id = @p AND person_id = @id LIMIT @n");
        cmd.Parameters.AddWithValue("p", projectId);
        cmd.Parameters.AddWithValue("id", personId);
        cmd.Parameters.AddWithValue("n", MaxPersonDistinctIds + 1);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    const string PersonSelect = """
        SELECT p.id, p.is_identified, p.created_at, p.properties::text,
               COALESCE((SELECT array_agg(d.distinct_id ORDER BY d.distinct_id) FROM person_distinct_ids d WHERE d.person_id = p.id), '{}')
        FROM persons p
        WHERE p.project_id = @p AND NOT p.is_deleted
        """;

    static async Task<IResult> Persons(HttpContext ctx, NpgsqlDataSource pg, string? search, int? limit, string? cursor, bool? identified)
    {
        var project = ProjectContext.From(ctx);
        var n = Math.Clamp(limit ?? 50, 1, 200);
        Guid? after = cursor is null ? null : Guid.TryParse(cursor, out var c) ? c : throw new QueryException("Invalid cursor");

        await using var cmd = pg.CreateCommand(PersonSelect + """

              AND (@cursor IS NULL OR p.id < @cursor)
              AND (@ident IS NULL OR p.is_identified = @ident)
              AND (@q IS NULL OR p.properties::text ILIKE @q
                   OR EXISTS (SELECT 1 FROM person_distinct_ids d WHERE d.person_id = p.id AND d.distinct_id ILIKE @q))
            ORDER BY p.id DESC LIMIT @n
            """);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        cmd.Parameters.Add(new NpgsqlParameter("cursor", NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ident", NpgsqlDbType.Boolean) { Value = (object?)identified ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(search) ? DBNull.Value : "%" + EscapeLike(search) + "%" });
        cmd.Parameters.AddWithValue("n", n);

        var persons = await ReadPersons(cmd, ctx.RequestAborted);
        return Results.Json(new PersonsResponse(persons, persons.Count == n ? persons[^1].Id : null), AppJsonContext.Default.PersonsResponse);
    }

    static async Task<IResult> Person(HttpContext ctx, NpgsqlDataSource pg, string personId)
    {
        var project = ProjectContext.From(ctx);
        if (!Guid.TryParse(personId, out var id)) return Error("Person not found", StatusCodes.Status404NotFound);

        await using var cmd = pg.CreateCommand(PersonSelect + " AND p.id = @id");
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        cmd.Parameters.AddWithValue("id", id);
        var persons = await ReadPersons(cmd, ctx.RequestAborted);
        return persons.Count == 0
            ? Error("Person not found", StatusCodes.Status404NotFound)
            : Results.Json(persons[0], AppJsonContext.Default.PersonRow);
    }

    static async Task<List<PersonRow>> ReadPersons(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<PersonRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PersonRow(r.GetGuid(0).ToString(), r.GetBoolean(1), r.GetDateTime(2), r.GetFieldValue<string[]>(4), ParseJson(r.GetString(3))));
        return list;
    }

    static readonly ConcurrentDictionary<long, (MetadataResponse Value, DateTime Expires)> MetadataCache = new();

    static async Task<IResult> Metadata(HttpContext ctx, MySqlDataSource db, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (MetadataCache.TryGetValue(project.ProjectId, out var hit) && hit.Expires > DateTime.UtcNow)
            return Results.Json(hit.Value, AppJsonContext.Default.MetadataResponse);

        var now = DateTime.UtcNow;
        var namesB = new SqlBuilder(project.ProjectId, now.AddDays(-30), now.AddMinutes(1));
        var keysB = new SqlBuilder(project.ProjectId, now.AddDays(-7), now.AddMinutes(1));
        var events = Run(db, Queries.EventNames(namesB), namesB, r => new NameCount(r.GetString(0), r.GetInt64(1)), ctx.RequestAborted);
        var eventProps = Run(db, Queries.EventPropertyKeys(keysB), keysB, r => new NameCount(r.GetString(0), r.GetInt64(1)), ctx.RequestAborted);

        await using var cmd = pg.CreateCommand("""
            SELECT k, count(*) FROM persons, jsonb_object_keys(properties) AS k
            WHERE project_id = @p AND NOT is_deleted GROUP BY k ORDER BY 2 DESC LIMIT 500
            """);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        var personProps = new List<NameCount>();
        await using (var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted))
            while (await r.ReadAsync(ctx.RequestAborted)) personProps.Add(new NameCount(r.GetString(0), r.GetInt64(1)));

        var columns = ColumnMapper.EventColumns.Select(c => c.StartsWith("utm_") ? c : "$" + c).Order().ToList();
        var result = new MetadataResponse(await events, columns, await eventProps, personProps);
        MetadataCache[project.ProjectId] = (result, DateTime.UtcNow.AddMinutes(5));
        return Results.Json(result, AppJsonContext.Default.MetadataResponse);
    }

    static async Task<IResult> Health(MySqlDataSource db, NpgsqlDataSource pg, CancellationToken ct)
    {
        async Task<string> Check(Func<Task> probe)
        {
            try { await probe(); return "ok"; }
            catch (Exception ex) { return ex.Message; }
        }
        var postgres = await Check(async () => { await using var c = pg.CreateCommand("SELECT 1"); await c.ExecuteScalarAsync(ct); });
        var starrocks = await Check(async () =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var c = new MySqlCommand("SELECT 1", conn);
            await c.ExecuteScalarAsync(ct);
        });
        var ok = postgres == "ok" && starrocks == "ok";
        return Results.Json(new HealthResponse(postgres, starrocks), AppJsonContext.Default.HealthResponse,
            statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    static async Task<List<T>> Run<T>(MySqlDataSource db, string sql, SqlBuilder b, Func<MySqlDataReader, T> map, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 30 };
        foreach (var (name, value) in b.Parameters) cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    static async Task<IResult> Explain(MySqlDataSource db, IConfiguration cfg, IEnumerable<(string Sql, SqlBuilder Builder)> queries, CancellationToken ct)
    {
        if (cfg["QueryApi:AllowExplain"] != "true") throw new QueryException("explain is disabled in this environment");
        var list = queries.ToList();
        var plans = await Task.WhenAll(list.Select(q => Run(db, "EXPLAIN " + q.Sql, q.Builder, r => r.GetString(0), ct)));
        return Results.Json(new ExplainResponse(list.Select(q => q.Sql).ToList(), plans.Select(p => string.Join('\n', p)).ToList()),
            AppJsonContext.Default.ExplainResponse);
    }

    static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
