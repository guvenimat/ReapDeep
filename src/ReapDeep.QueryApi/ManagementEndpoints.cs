using System.Security.Cryptography;
using Npgsql;

namespace ReapDeep.QueryApi;

public static class ManagementEndpoints
{
    static readonly string[] Roles = ["owner", "admin", "member"];

    public static void MapManagement(this RouteGroupBuilder api)
    {
        api.MapGet("/members", Members);
        api.MapPost("/members", AddMember);
        api.MapPatch("/members/{userId:long}", ChangeRole);
        api.MapDelete("/members/{userId:long}", RemoveMember);
        api.MapGet("/api-keys", ApiKeys);
        api.MapPost("/api-keys", CreateApiKey);
        api.MapDelete("/api-keys/{id:long}", RevokeApiKey);
    }

    static IResult Error(string message, int status) =>
        Results.Json(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, statusCode: status);

    static IResult? RequireUser(ProjectContext project) =>
        project.UserId is null ? Error("This endpoint requires a signed-in user", StatusCodes.Status403Forbidden) : null;

    static IResult? RequireManager(ProjectContext project) =>
        RequireUser(project) ?? (project.CanManage ? null : Error("Requires the owner or admin role", StatusCodes.Status403Forbidden));

    const string MemberSelect = """
        SELECT u.id, u.email, u.name, m.role, m.created_at
        FROM organization_members m JOIN users u ON u.id = m.user_id
        WHERE m.organization_id = (SELECT organization_id FROM projects WHERE id = @p)
        """;

    static async Task<IResult> Members(HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireUser(project) is { } denied) return denied;
        await using var cmd = pg.CreateCommand(MemberSelect + " ORDER BY u.email");
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        return Results.Json(new MembersResponse(await ReadMembers(cmd, ctx.RequestAborted)), AppJsonContext.Default.MembersResponse);
    }

    static async Task<IResult> AddMember(AddMemberRequest req, HttpContext ctx, NpgsqlDataSource pg, SessionAuth sessions)
    {
        var project = ProjectContext.From(ctx);
        if (RequireManager(project) is { } denied) return denied;
        var email = SessionAuth.NormalizeEmail(req.Email ?? "");
        if (email.Length is < 3 or > 254 || !email.Contains('@')) throw new QueryException("A valid email is required");
        var role = req.Role ?? "member";
        if (!Roles.Contains(role)) throw new QueryException("role: owner | admin | member");
        if (role == "owner" && project.Role != "owner") return Error("Only owners can add owners", StatusCodes.Status403Forbidden);
        var name = string.IsNullOrWhiteSpace(req.Name) ? email.Split('@')[0] : req.Name.Trim();
        if (name.Length > 100) throw new QueryException("name can be at most 100 characters");

        var ct = ctx.RequestAborted;
        await using var conn = await pg.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long? userId = null;
        await using (var cmd = new NpgsqlCommand("SELECT id FROM users WHERE email = @e", conn, tx))
        {
            cmd.Parameters.AddWithValue("e", email);
            if (await cmd.ExecuteScalarAsync(ct) is long id) userId = id;
        }
        if (userId is null)
        {
            ValidatePassword(req.Password);
            await using var cmd = new NpgsqlCommand("INSERT INTO users (email, name, password_hash) VALUES (@e, @n, @h) RETURNING id", conn, tx);
            cmd.Parameters.AddWithValue("e", email);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("h", Passwords.Hash(req.Password!));
            userId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO organization_members (organization_id, user_id, role)
            SELECT organization_id, @u, @r FROM projects WHERE id = @p
            ON CONFLICT DO NOTHING
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId.Value);
            cmd.Parameters.AddWithValue("r", role);
            cmd.Parameters.AddWithValue("p", project.ProjectId);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Error("This user is already a member", StatusCodes.Status409Conflict);
        }
        await tx.CommitAsync(ct);
        sessions.InvalidateUser(userId.Value);

        return Results.Json(await Member(pg, project.ProjectId, userId.Value, ct), AppJsonContext.Default.MemberRow, statusCode: StatusCodes.Status201Created);
    }

    static async Task<IResult> ChangeRole(long userId, RoleChangeRequest req, HttpContext ctx, NpgsqlDataSource pg, SessionAuth sessions)
    {
        var project = ProjectContext.From(ctx);
        if (RequireManager(project) is { } denied) return denied;
        if (req.Role is not { } role || !Roles.Contains(role)) throw new QueryException("role: owner | admin | member");
        return await UpdateMembership(ctx, pg, sessions, project, userId, role);
    }

    static async Task<IResult> RemoveMember(long userId, HttpContext ctx, NpgsqlDataSource pg, SessionAuth sessions)
    {
        var project = ProjectContext.From(ctx);
        if (userId != project.UserId && RequireManager(project) is { } denied) return denied;
        return await UpdateMembership(ctx, pg, sessions, project, userId, null);
    }

    static async Task<IResult> UpdateMembership(HttpContext ctx, NpgsqlDataSource pg, SessionAuth sessions, ProjectContext project, long userId, string? newRole)
    {
        var ct = ctx.RequestAborted;
        await using var conn = await pg.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var roles = new Dictionary<long, string>();
        long orgId = 0;
        await using (var cmd = new NpgsqlCommand("""
            SELECT m.organization_id, m.user_id, m.role FROM organization_members m
            WHERE m.organization_id = (SELECT organization_id FROM projects WHERE id = @p)
            FOR UPDATE
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("p", project.ProjectId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                orgId = r.GetInt64(0);
                roles[r.GetInt64(1)] = r.GetString(2);
            }
        }
        if (!roles.TryGetValue(userId, out var current)) return Error("Member not found", StatusCodes.Status404NotFound);
        if (project.Role != "owner" && (current == "owner" || newRole == "owner"))
            return Error("Only owners can change owners", StatusCodes.Status403Forbidden);
        if (current == "owner" && newRole != "owner" && roles.Values.Count(v => v == "owner") == 1)
            return Error("An organization needs at least one owner", StatusCodes.Status409Conflict);

        await using (var cmd = new NpgsqlCommand(newRole is null
            ? "DELETE FROM organization_members WHERE organization_id = @o AND user_id = @u"
            : "UPDATE organization_members SET role = @r WHERE organization_id = @o AND user_id = @u", conn, tx))
        {
            cmd.Parameters.AddWithValue("o", orgId);
            cmd.Parameters.AddWithValue("u", userId);
            if (newRole is not null) cmd.Parameters.AddWithValue("r", newRole);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        sessions.InvalidateUser(userId);

        return newRole is null
            ? Results.NoContent()
            : Results.Json(await Member(pg, project.ProjectId, userId, ct), AppJsonContext.Default.MemberRow);
    }

    static async Task<MemberRow> Member(NpgsqlDataSource pg, long projectId, long userId, CancellationToken ct)
    {
        await using var cmd = pg.CreateCommand(MemberSelect + " AND u.id = @u");
        cmd.Parameters.AddWithValue("p", projectId);
        cmd.Parameters.AddWithValue("u", userId);
        return (await ReadMembers(cmd, ct))[0];
    }

    static async Task<List<MemberRow>> ReadMembers(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<MemberRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new MemberRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
        return list;
    }

    const string KeySelect = """
        SELECT k.id, k.name, k.key_prefix, u.email, k.created_at, k.revoked_at
        FROM api_keys k LEFT JOIN users u ON u.id = k.created_by
        WHERE k.project_id = @p
        """;

    static async Task<IResult> ApiKeys(HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireManager(project) is { } denied) return denied;
        await using var cmd = pg.CreateCommand(KeySelect + " ORDER BY k.revoked_at IS NOT NULL, k.id DESC");
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        return Results.Json(new ApiKeysResponse(await ReadKeys(cmd, ctx.RequestAborted)), AppJsonContext.Default.ApiKeysResponse);
    }

    static async Task<IResult> CreateApiKey(CreateApiKeyRequest req, HttpContext ctx, NpgsqlDataSource pg)
    {
        var project = ProjectContext.From(ctx);
        if (RequireManager(project) is { } denied) return denied;
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new QueryException("name is required (at most 100 characters)");

        var key = "rk_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await using var cmd = pg.CreateCommand("""
            INSERT INTO api_keys (project_id, name, key_prefix, key_hash, created_by)
            VALUES (@p, @n, @x, @h, @u) RETURNING id
            """);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("x", key[..7]);
        cmd.Parameters.AddWithValue("h", ApiKeyAuth.HashKey(key));
        cmd.Parameters.AddWithValue("u", project.UserId!.Value);
        var id = (long)(await cmd.ExecuteScalarAsync(ctx.RequestAborted))!;

        await using var read = pg.CreateCommand(KeySelect + " AND k.id = @id");
        read.Parameters.AddWithValue("p", project.ProjectId);
        read.Parameters.AddWithValue("id", id);
        var row = (await ReadKeys(read, ctx.RequestAborted))[0];
        return Results.Json(new CreatedApiKey(row, key), AppJsonContext.Default.CreatedApiKey, statusCode: StatusCodes.Status201Created);
    }

    static async Task<IResult> RevokeApiKey(long id, HttpContext ctx, NpgsqlDataSource pg, ApiKeyAuth keys)
    {
        var project = ProjectContext.From(ctx);
        if (RequireManager(project) is { } denied) return denied;
        await using var cmd = pg.CreateCommand("""
            UPDATE api_keys SET revoked_at = COALESCE(revoked_at, now())
            WHERE id = @id AND project_id = @p RETURNING key_hash
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("p", project.ProjectId);
        if (await cmd.ExecuteScalarAsync(ctx.RequestAborted) is not string hash) return Error("API key not found", StatusCodes.Status404NotFound);
        keys.Invalidate(hash);
        return Results.NoContent();
    }

    static async Task<List<ApiKeyRow>> ReadKeys(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<ApiKeyRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ApiKeyRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetDateTime(5)));
        return list;
    }

    public const string PasswordRule = "Password must be 8-1024 characters";

    public static bool ValidPassword(string? password) => password is { Length: >= 8 and <= 1024 };

    static void ValidatePassword(string? password)
    {
        if (!ValidPassword(password)) throw new QueryException(PasswordRule);
    }
}
