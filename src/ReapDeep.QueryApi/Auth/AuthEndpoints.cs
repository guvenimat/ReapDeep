using ReapDeep.Analytics;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Auth;

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public static void MapAuth(this RouteGroupBuilder group)
    {
        group.MapPost("/login", Login).RequireRateLimiting(LoginRateLimit);
        group.MapPost("/logout", Logout);
        group.MapGet("/me", Me);
        group.MapPost("/password", ChangePassword);
    }

    static async Task<IResult> Login(LoginRequest req, HttpContext ctx, SessionService sessions)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password) || req.Password.Length > PasswordHasher.MaxLength)
            return ApiResults.BadRequest("email and password are required");
        return await sessions.SignInAsync(ctx, req.Email, req.Password) is { } me
            ? ApiResults.Ok(me, AppJsonContext.Default.MeResponse)
            : ApiResults.Unauthorized("Invalid email or password");
    }

    static async Task<IResult> Logout(HttpContext ctx, SessionService sessions)
    {
        await sessions.SignOutAsync(ctx);
        return Results.NoContent();
    }

    static async Task<IResult> Me(HttpContext ctx, SessionService sessions) =>
        await sessions.GetCurrentAsync(ctx) is { } me
            ? ApiResults.Ok(me, AppJsonContext.Default.MeResponse)
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
}
