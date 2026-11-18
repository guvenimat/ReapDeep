using ReapDeep.Analytics;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Auth;

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";
    public const string AccountRateLimit = "account";
    public const string InvalidLink = "This link is invalid, already used or expired";

    public static void MapAuth(this RouteGroupBuilder group)
    {
        group.MapPost("/login", Login).RequireRateLimiting(LoginRateLimit);
        group.MapPost("/logout", Logout);
        group.MapGet("/me", Me);
        group.MapPost("/password", ChangePassword);
        group.MapPost("/password/forgot", ForgotPassword).RequireRateLimiting(AccountRateLimit);
        group.MapPost("/token", TokenInfo).RequireRateLimiting(AccountRateLimit);
        group.MapPost("/password/reset", ResetPassword).RequireRateLimiting(AccountRateLimit);
    }

    static IResult ForgotPassword(ForgotPasswordRequest req, AccountMailer mailer)
    {
        var email = SessionService.NormalizeEmail(req.Email ?? "");
        Ensure.That(email.Length is >= 3 and <= 254 && email.Contains('@'), "A valid email is required");
        if (!mailer.CanSend) return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "Password reset by email is not available: no SMTP server is configured");
        mailer.QueueReset(email);
        return Results.Accepted();
    }

    static async Task<IResult> TokenInfo(AuthTokenRequest req, AuthTokenRepository tokens, CancellationToken ct)
    {
        if (!AuthTokens.IsWellFormed(req.Token)) return ApiResults.BadRequest(InvalidLink);
        return await tokens.PeekAsync(AuthTokens.Hash(req.Token!), ct) is { } info
            ? ApiResults.Ok(new AuthTokenInfo(info.Email, info.Purpose), AppJsonContext.Default.AuthTokenInfo)
            : ApiResults.BadRequest(InvalidLink);
    }

    static async Task<IResult> ResetPassword(ResetPasswordRequest req, HttpContext ctx, AuthTokenRepository tokens, SessionService sessions)
    {
        Ensure.That(PasswordHasher.IsValidLength(req.NewPassword), PasswordHasher.LengthRule);
        if (!AuthTokens.IsWellFormed(req.Token)) return ApiResults.BadRequest(InvalidLink);
        if (await tokens.ConsumeAsync(AuthTokens.Hash(req.Token!), PasswordHasher.Hash(req.NewPassword!), ctx.RequestAborted) is not { } userId)
            return ApiResults.BadRequest(InvalidLink);
        await sessions.InvalidateUserAsync(userId);
        return await sessions.SignInUserAsync(ctx, userId) is { } me
            ? ApiResults.Ok(WithPlatformRole(me, ctx), AppJsonContext.Default.MeResponse)
            : ApiResults.Unauthorized("Not signed in");
    }

    static async Task<IResult> Login(LoginRequest req, HttpContext ctx, SessionService sessions)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password) || req.Password.Length > PasswordHasher.MaxLength)
            return ApiResults.BadRequest("email and password are required");
        return await sessions.SignInAsync(ctx, req.Email, req.Password) is { } me
            ? ApiResults.Ok(WithPlatformRole(me, ctx), AppJsonContext.Default.MeResponse)
            : ApiResults.Unauthorized("Invalid email or password");
    }

    static async Task<IResult> Logout(HttpContext ctx, SessionService sessions)
    {
        await sessions.SignOutAsync(ctx);
        return Results.NoContent();
    }

    static async Task<IResult> Me(HttpContext ctx, SessionService sessions) =>
        await sessions.GetCurrentAsync(ctx) is { } me
            ? ApiResults.Ok(WithPlatformRole(me, ctx), AppJsonContext.Default.MeResponse)
            : ApiResults.Unauthorized("Not signed in");

    static async Task<IResult> ChangePassword(PasswordChangeRequest req, HttpContext ctx, SessionService sessions)
    {
        if (await sessions.GetCurrentAsync(ctx) is not { } me) return ApiResults.Unauthorized("Not signed in");
        Ensure.That(!string.IsNullOrEmpty(req.CurrentPassword), "current_password is required");
        Ensure.That(PasswordHasher.IsValidLength(req.NewPassword), PasswordHasher.LengthRule);
        return await sessions.ChangePasswordAsync(ctx, me.User.Id, req.CurrentPassword, req.NewPassword)
            ? Results.NoContent()
            : ApiResults.BadRequest("Current password is incorrect");
    }

    static MeResponse WithPlatformRole(MeResponse me, HttpContext ctx)
    {
        var role = ctx.RequestServices.GetRequiredService<ReapDeep.QueryApi.Ai.PlatformAdmins>().RoleOf(me.User.Email);
        return me with { PlatformAdmin = role == ReapDeep.QueryApi.Ai.PlatformAdmins.Admin, PlatformRole = role };
    }
}
