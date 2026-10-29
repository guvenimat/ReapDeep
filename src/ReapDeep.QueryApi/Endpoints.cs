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

        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/login", Login).RequireRateLimiting(LoginRateLimit);
        auth.MapPost("/logout", Logout);
        auth.MapGet("/me", Me);
        auth.MapPost("/password", ChangePassword);

        var api = app.MapGroup("/api/v1").AddEndpointFilter(AuthAndErrors);
        api.MapPost("/query/trends", Trends);
        api.MapPost("/query/funnels", Funnels);
        api.MapGet("/events", Events);
        api.MapGet("/persons", Persons);
        api.MapGet("/persons/{personId}", Person);
        api.MapGet("/metadata", Metadata);
        api.MapGet("/deletion-requests", DeletionRequests);
        api.MapPost("/deletion-requests", CreateDeletionRequest);
        api.MapManagement();
    }

    public const string LoginRateLimit = "login";

    static async Task<IResult> Login(LoginRequest req, HttpContext ctx, SessionAuth sessions)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password) || req.Password.Length > 1024)
            return Error("email and password are required", StatusCodes.Status400BadRequest);
        var me = await sessions.Login(ctx, req.Email, req.Password);
        return me is null
            ? Error("Invalid email or password", StatusCodes.Status401Unauthorized)
            : Results.Json(me, AppJsonContext.Default.MeResponse);
    }

    static async Task<IResult> Logout(HttpContext ctx, SessionAuth sessions)
    {
        await sessions.Logout(ctx);
        return Results.NoContent();
    }

    static async Task<IResult> Me(HttpContext ctx, SessionAuth sessions) =>
        await sessions.Current(ctx) is { } me
            ? Results.Json(me, AppJsonContext.Default.MeResponse)
            : Error("Not signed in", StatusCodes.Status401Unauthorized);

    static async Task<IResult> ChangePassword(PasswordChangeRequest req, HttpContext ctx, SessionAuth sessions)
    {
        if (await sessions.Current(ctx) is not { } me) return Error("Not signed in", StatusCodes.Status401Unauthorized);
        if (string.IsNullOrEmpty(req.CurrentPassword)) return Error("current_password is required", StatusCodes.Status400BadRequest);
        if (!ManagementEndpoints.ValidPassword(req.NewPassword)) return Error(ManagementEndpoints.PasswordRule, StatusCodes.Status400BadRequest);
        return await sessions.ChangePassword(ctx, me.User.Id, req.CurrentPassword, req.NewPassword!)
            ? Results.NoContent()
            : Error("Current password is incorrect", StatusCodes.Status400BadRequest);
    }

    static async ValueTask<(ProjectContext? Project, IResult? Error)> Authenticate(HttpContext ctx)
    {
        if (ApiKeyAuth.HasKey(ctx.Request))
            return await ctx.RequestServices.GetRequiredService<ApiKeyAuth>().Authenticate(ctx) is { } byKey
                ? (byKey, null)
                : (null, Error("Invalid or missing API key", StatusCodes.Status401Unauthorized));

        var sessions = ctx.RequestServices.GetRequiredService<SessionAuth>();
        if (await sessions.Current(ctx) is not { } me) return (null, Error("Not signed in", StatusCodes.Status401Unauthorized));
        if (!long.TryParse(ctx.Request.Headers["X-Project-Id"].ToString(), out var projectId))
            return (null, Error("X-Project-Id header is required", StatusCodes.Status400BadRequest));
        return sessions.Project(me, projectId) is { } project
            ? (project, null)
            : (null, Error("You do not have access to this project", StatusCodes.Status403Forbidden));
    }

    static async ValueTask<object?> AuthAndErrors(EndpointFilterInvocationContext ic, EndpointFilterDelegate next)
    {
        var ctx = ic.HttpContext;
        var (project, error) = await Authenticate(ctx);
        if (project is null) return error;
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

    const string DeletionSelect = """
        SELECT r.id, r.target_type, r.target_id, u.email, r.requested_at, r.processed_at
        FROM deletion_requests r LEFT JOIN users u ON u.id = r.requested_by
        WHERE r.project_id = @p
        """;

    static async Task<IResult> DeletionRequests(HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        await using var cmd = pg.CreateCommand(DeletionSelect + " ORDER BY r.id DESC LIMIT 100");
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        return Results.Json(new DeletionRequestsResponse(await ReadDeletionRequests(cmd, ctx.RequestAborted)), AppJsonContext.Default.DeletionRequestsResponse);
    }

    static async Task<IResult> CreateDeletionRequest(DeletionRequestBody req, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (!project.CanManage) return Error("Deletion requests require a signed-in project owner or admin", StatusCodes.Status403Forbidden);
        if (req.TargetType is not ("person" or "distinct_id")) throw new QueryException("target_type: person | distinct_id");
        if (string.IsNullOrWhiteSpace(req.TargetId) || req.TargetId.Length > 200) throw new QueryException("target_id is required (at most 200 characters)");

        await using var cmd = pg.CreateCommand("""
            WITH existing AS (
                SELECT id FROM deletion_requests
                WHERE project_id = @p AND target_type = @t AND target_id = @i AND processed_at IS NULL
                LIMIT 1
            ), created AS (
                INSERT INTO deletion_requests (project_id, target_type, target_id, requested_by)
                SELECT @p, @t, @i, @u WHERE NOT EXISTS (SELECT 1 FROM existing)
                RETURNING id
            )
            SELECT id FROM created UNION ALL SELECT id FROM existing
            """);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        cmd.Parameters.AddWithValue("t", req.TargetType);
        cmd.Parameters.AddWithValue("i", req.TargetId);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Bigint) { Value = (object?)project.UserId ?? DBNull.Value });
        var id = (long)(await cmd.ExecuteScalarAsync(ctx.RequestAborted))!;

        await using var read = pg.CreateCommand(DeletionSelect + " AND r.id = @id");
        read.Parameters.AddWithValue("p", project.ProjectId);
        read.Parameters.AddWithValue("id", id);
        var row = (await ReadDeletionRequests(read, ctx.RequestAborted))[0];
        return Results.Json(row, AppJsonContext.Default.DeletionRequestRow, statusCode: StatusCodes.Status202Accepted);
    }

    static async Task<List<DeletionRequestRow>> ReadDeletionRequests(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<DeletionRequestRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DeletionRequestRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetDateTime(5)));
        return list;
    }

    static async Task<IResult> Metadata(HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        var ct = ctx.RequestAborted;
        await using var conn = await pg.OpenConnectionAsync(ct);

        var events = new List<EventDefinition>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT name, last_seen_at FROM event_definitions WHERE project_id = @p ORDER BY last_seen_at DESC, name LIMIT 2000", conn))
        {
            cmd.Parameters.AddWithValue("p", project.ProjectId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) events.Add(new EventDefinition(r.GetString(0), r.GetDateTime(1)));
        }

        var eventProps = new List<PropertyDefinition>();
        var personProps = new List<PropertyDefinition>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT scope, name, property_type, last_seen_at FROM property_definitions
            WHERE project_id = @p AND column_name IS NULL ORDER BY last_seen_at DESC, name LIMIT 5000
            """, conn))
        {
            cmd.Parameters.AddWithValue("p", project.ProjectId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                (r.GetString(0) == "person" ? personProps : eventProps)
                    .Add(new PropertyDefinition(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDateTime(3)));
        }

        var columns = ColumnMapper.EventColumns.Select(c => c.StartsWith("utm_") ? c : "$" + c).Order().ToList();
        return Results.Json(new MetadataResponse(events, columns, eventProps, personProps), AppJsonContext.Default.MetadataResponse);
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
