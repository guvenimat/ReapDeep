using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Organization;

public static class MemberEndpoints
{
    public static void MapMembers(this RouteGroupBuilder api)
    {
        api.MapGet("/members", List).RequireUser();
        api.MapPost("/members", Add).RequireManager();
        api.MapPatch("/members/{userId:long}", ChangeRole).RequireManager();
        api.MapDelete("/members/{userId:long}", Remove).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, MemberRepository members, CancellationToken ct) =>
        ApiResults.Ok(new MembersResponse(await members.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.MembersResponse);

    static async Task<IResult> Add(AddMemberRequest req, ProjectContext project, MemberRepository members, SessionService sessions, CancellationToken ct)
    {
        var email = SessionService.NormalizeEmail(req.Email ?? "");
        Ensure.That(email.Length is >= 3 and <= 254 && email.Contains('@'), "A valid email is required");
        var role = Ensure.OneOf(req.Role ?? Roles.Member, "role", Roles.All);
        var name = string.IsNullOrWhiteSpace(req.Name) ? email.Split('@')[0] : Ensure.NotBlank(req.Name, "name", 100);
        if (MembershipRules.CanAdd(project.Role!, role) != MembershipError.None) return ApiResults.Forbidden("Only owners can add owners");
        if (req.Password is not null) Ensure.That(PasswordHasher.IsValidLength(req.Password), PasswordHasher.LengthRule);

        var hash = req.Password is null ? null : PasswordHasher.Hash(req.Password);
        var (result, userId) = await members.AddAsync(project.ProjectId, email, name, hash, role, ct);
        switch (result)
        {
            case AddMemberResult.PasswordRequired: return ApiResults.BadRequest(PasswordHasher.LengthRule);
            case AddMemberResult.AlreadyMember: return ApiResults.Conflict("This user is already a member");
        }
        sessions.InvalidateUser(userId);
        return ApiResults.Created((await members.GetAsync(project.ProjectId, userId, ct))!, AppJsonContext.Default.MemberRow);
    }

    static async Task<IResult> ChangeRole(long userId, RoleChangeRequest req, ProjectContext project, MemberRepository members, SessionService sessions,
        CancellationToken ct)
    {
        var role = Ensure.OneOf(req.Role, "role", Roles.All);
        if (await Change(project, members, sessions, userId, role, ct) is { } error) return error;
        return ApiResults.Ok((await members.GetAsync(project.ProjectId, userId, ct))!, AppJsonContext.Default.MemberRow);
    }

    static async Task<IResult> Remove(long userId, ProjectContext project, MemberRepository members, SessionService sessions, CancellationToken ct)
    {
        if (userId != project.UserId && !project.CanManage) return ApiResults.Forbidden("This action requires the owner or admin role");
        return await Change(project, members, sessions, userId, null, ct) ?? Results.NoContent();
    }

    static async Task<IResult?> Change(ProjectContext project, MemberRepository members, SessionService sessions, long userId, string? role,
        CancellationToken ct)
    {
        var error = await members.ChangeAsync(project.ProjectId, project.Role!, userId, role, ct);
        if (error == MembershipError.None) sessions.InvalidateUser(userId);
        return error switch
        {
            MembershipError.None => null,
            MembershipError.NotFound => ApiResults.NotFound("Member not found"),
            MembershipError.OwnerOnly => ApiResults.Forbidden("Only owners can change owners"),
            _ => ApiResults.Conflict("An organization needs at least one owner"),
        };
    }
}
