using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ReapDeep.QueryApi;

public static class InsightEndpoints
{
    const int MaxQueryBytes = 32 * 1024;
    const int MaxDashboardItems = 30;

    public static void MapInsights(this RouteGroupBuilder api)
    {
        api.MapGet("/insights", Insights);
        api.MapPost("/insights", CreateInsight);
        api.MapPut("/insights/{id:long}", UpdateInsight);
        api.MapDelete("/insights/{id:long}", DeleteInsight);
        api.MapGet("/dashboards", Dashboards);
        api.MapGet("/dashboards/{id:long}", Dashboard);
        api.MapPost("/dashboards", CreateDashboard);
        api.MapPut("/dashboards/{id:long}", UpdateDashboard);
        api.MapDelete("/dashboards/{id:long}", DeleteDashboard);
    }

    static IResult Error(string message, int status) =>
        Results.Json(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, statusCode: status);

    static IResult? RequireUser(ProjectContext project) =>
        project.UserId is null ? Error("Saving requires a signed-in user", StatusCodes.Status403Forbidden) : null;

    static string Name(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length is 0 or > 200) throw new QueryException("name is required (at most 200 characters)");
        return n;
    }

    static string Query(JsonElement? query)
    {
        if (query is not { ValueKind: JsonValueKind.Object } q) throw new QueryException("query must be a JSON object");
        var raw = q.GetRawText();
        if (raw.Length > MaxQueryBytes) throw new QueryException("query is too large");
        return raw;
    }

    const string InsightSelect = """
        SELECT i.id, i.name, i.kind, i.query::text, u.email, i.created_at, i.updated_at
        FROM insights i LEFT JOIN users u ON u.id = i.created_by
        WHERE i.project_id = @p
        """;

    static async Task<IResult> Insights(HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        await using var cmd = pg.CreateCommand(InsightSelect + " ORDER BY i.updated_at DESC LIMIT 500");
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        return Results.Json(new InsightsResponse(await ReadInsights(cmd, ctx.RequestAborted)), AppJsonContext.Default.InsightsResponse);
    }

    static async Task<IResult> CreateInsight(InsightBody req, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireUser(project) is { } denied) return denied;
        if (req.Kind is not ("trends" or "funnels")) throw new QueryException("kind: trends | funnels");
        var name = Name(req.Name);
        var query = Query(req.Query);

        await using var cmd = pg.CreateCommand("""
            INSERT INTO insights (project_id, name, kind, query, created_by) VALUES (@p, @n, @k, @q, @u) RETURNING id
            """);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("k", req.Kind);
        cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Jsonb) { Value = query });
        cmd.Parameters.AddWithValue("u", project.UserId!.Value);
        var id = (long)(await cmd.ExecuteScalarAsync(ctx.RequestAborted))!;
        return Results.Json(await Insight(pg, project.ProjectId, id, ctx.RequestAborted), AppJsonContext.Default.InsightRow, statusCode: StatusCodes.Status201Created);
    }

    static async Task<IResult> UpdateInsight(long id, InsightBody req, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireUser(project) is { } denied) return denied;
        var name = req.Name is null ? null : Name(req.Name);
        var query = req.Query is null ? null : Query(req.Query);

        await using var cmd = pg.CreateCommand("""
            UPDATE insights SET name = COALESCE(@n, name), query = COALESCE(@q, query), updated_at = now()
            WHERE id = @id AND project_id = @p
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)name ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Jsonb) { Value = (object?)query ?? DBNull.Value });
        if (await cmd.ExecuteNonQueryAsync(ctx.RequestAborted) == 0) return Error("Insight not found", StatusCodes.Status404NotFound);
        return Results.Json(await Insight(pg, project.ProjectId, id, ctx.RequestAborted), AppJsonContext.Default.InsightRow);
    }

    static async Task<IResult> DeleteInsight(long id, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireUser(project) is { } denied) return denied;
        await using var cmd = pg.CreateCommand("DELETE FROM insights WHERE id = @id AND project_id = @p");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        return await cmd.ExecuteNonQueryAsync(ctx.RequestAborted) == 0
            ? Error("Insight not found", StatusCodes.Status404NotFound)
            : Results.NoContent();
    }

    static async Task<InsightRow> Insight(NpgsqlDataSource pg, long projectId, long id, CancellationToken ct)
    {
        await using var cmd = pg.CreateCommand(InsightSelect + " AND i.id = @id");
        cmd.Parameters.AddWithValue("p", projectId);
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadInsights(cmd, ct))[0];
    }

    static async Task<List<InsightRow>> ReadInsights(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<InsightRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            using var doc = JsonDocument.Parse(r.GetString(3));
            list.Add(new InsightRow(r.GetInt64(0), r.GetString(1), r.GetString(2), doc.RootElement.Clone(),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetDateTime(5), r.GetDateTime(6)));
        }
        return list;
    }

    static async Task<IResult> Dashboards(HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        await using var cmd = pg.CreateCommand("""
            SELECT d.id, d.name, d.description, u.email, d.updated_at,
                   (SELECT count(*) FROM dashboard_items di WHERE di.dashboard_id = d.id)
            FROM dashboards d LEFT JOIN users u ON u.id = d.created_by
            WHERE d.project_id = @p ORDER BY d.updated_at DESC LIMIT 200
            """);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        var list = new List<DashboardRow>();
        await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
        while (await r.ReadAsync(ctx.RequestAborted))
            list.Add(new DashboardRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetDateTime(4), (int)r.GetInt64(5)));
        return Results.Json(new DashboardsResponse(list), AppJsonContext.Default.DashboardsResponse);
    }

    static async Task<IResult> Dashboard(long id, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        return await LoadDashboard(pg, project.ProjectId, id, ctx.RequestAborted) is { } d
            ? Results.Json(d, AppJsonContext.Default.DashboardDetail)
            : Error("Dashboard not found", StatusCodes.Status404NotFound);
    }

    static async Task<DashboardDetail?> LoadDashboard(NpgsqlDataSource pg, long projectId, long id, CancellationToken ct)
    {
        await using var conn = await pg.OpenConnectionAsync(ct);
        DashboardDetail? detail = null;
        await using (var cmd = new NpgsqlCommand("SELECT id, name, description, updated_at FROM dashboards WHERE id = @id AND project_id = @p", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("p", projectId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) detail = new DashboardDetail(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTime(3), []);
        }
        if (detail is null) return null;

        await using (var cmd = new NpgsqlCommand("""
            SELECT i.id, i.name, i.kind, i.query::text, u.email, i.created_at, i.updated_at
            FROM dashboard_items di JOIN insights i ON i.id = di.insight_id LEFT JOIN users u ON u.id = i.created_by
            WHERE di.dashboard_id = @id ORDER BY di.position
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            detail.Items.AddRange(await ReadInsights(cmd, ct));
        }
        return detail;
    }

    static async Task<IResult> CreateDashboard(DashboardBody req, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireUser(project) is { } denied) return denied;
        var name = Name(req.Name);
        await using var cmd = pg.CreateCommand("""
            INSERT INTO dashboards (project_id, name, description, created_by) VALUES (@p, @n, @d, @u) RETURNING id
            """);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("d", Description(req.Description));
        cmd.Parameters.AddWithValue("u", project.UserId!.Value);
        var id = (long)(await cmd.ExecuteScalarAsync(ctx.RequestAborted))!;
        if (req.InsightIds is { Count: > 0 }) await SetItems(pg, project.ProjectId, id, req.InsightIds, ctx.RequestAborted);
        return Results.Json((await LoadDashboard(pg, project.ProjectId, id, ctx.RequestAborted))!, AppJsonContext.Default.DashboardDetail,
            statusCode: StatusCodes.Status201Created);
    }

    static async Task<IResult> UpdateDashboard(long id, DashboardBody req, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireUser(project) is { } denied) return denied;
        var name = req.Name is null ? null : Name(req.Name);
        await using (var cmd = pg.CreateCommand("""
            UPDATE dashboards SET name = COALESCE(@n, name), description = COALESCE(@d, description), updated_at = now()
            WHERE id = @id AND project_id = @p
            """))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("p", project.ProjectId);
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)name ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = req.Description is null ? DBNull.Value : Description(req.Description) });
            if (await cmd.ExecuteNonQueryAsync(ctx.RequestAborted) == 0) return Error("Dashboard not found", StatusCodes.Status404NotFound);
        }
        if (req.InsightIds is not null) await SetItems(pg, project.ProjectId, id, req.InsightIds, ctx.RequestAborted);
        return Results.Json((await LoadDashboard(pg, project.ProjectId, id, ctx.RequestAborted))!, AppJsonContext.Default.DashboardDetail);
    }

    static async Task SetItems(NpgsqlDataSource pg, long projectId, long dashboardId, List<long> insightIds, CancellationToken ct)
    {
        var ids = insightIds.Distinct().ToArray();
        if (ids.Length > MaxDashboardItems) throw new QueryException($"A dashboard can hold at most {MaxDashboardItems} insights");
        await using var conn = await pg.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand("SELECT count(*) FROM insights WHERE project_id = @p AND id = ANY(@ids)", conn, tx))
        {
            check.Parameters.AddWithValue("p", projectId);
            check.Parameters.AddWithValue("ids", ids);
            if ((long)(await check.ExecuteScalarAsync(ct))! != ids.Length) throw new QueryException("insight_ids contains an unknown insight");
        }
        await using (var cmd = new NpgsqlCommand("""
            DELETE FROM dashboard_items WHERE dashboard_id = @d;
            INSERT INTO dashboard_items (dashboard_id, insight_id, position)
            SELECT @d, id, ord FROM unnest(@ids) WITH ORDINALITY AS u(id, ord);
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("d", dashboardId);
            cmd.Parameters.AddWithValue("ids", ids);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    static async Task<IResult> DeleteDashboard(long id, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireUser(project) is { } denied) return denied;
        await using var cmd = pg.CreateCommand("DELETE FROM dashboards WHERE id = @id AND project_id = @p");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        return await cmd.ExecuteNonQueryAsync(ctx.RequestAborted) == 0
            ? Error("Dashboard not found", StatusCodes.Status404NotFound)
            : Results.NoContent();
    }

    static string Description(string? d)
    {
        var s = d?.Trim() ?? "";
        if (s.Length > 1000) throw new QueryException("description can be at most 1000 characters");
        return s;
    }
}
