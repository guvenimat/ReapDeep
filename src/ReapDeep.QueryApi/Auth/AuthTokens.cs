using System.Security.Cryptography;
using System.Text;
using ReapDeep.QueryApi.Alerts;

namespace ReapDeep.QueryApi.Auth;

public static class AuthTokens
{
    public const string Invite = "invite";
    public const string Reset = "reset";
    public const int MaxResetsPerHour = 3;
    public const int MinLength = 32;
    public const int MaxLength = 128;
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    public static TimeSpan Lifetime(string purpose) => purpose == Invite ? InviteLifetime : ResetLifetime;

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsWellFormed(string? token) =>
        token is { Length: >= MinLength and <= MaxLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string Link(string publicUrl, string purpose, string token) =>
        $"{publicUrl.TrimEnd('/')}/#/{(purpose == Invite ? "invite" : "reset")}?token={Uri.EscapeDataString(token)}";

    public static EmailMessage InviteEmail(string inviter, string organization, string link) => new(
        $"You are invited to {organization} on ReapDeep",
        $"{inviter} invited you to {organization} on ReapDeep.\n\nSet your password to sign in:\n{link}\n\n" +
        $"The link works once and expires in {(int)InviteLifetime.TotalDays} days. If you did not expect this invitation, you can ignore this email.");

    public static EmailMessage ResetEmail(string link) => new(
        "Reset your ReapDeep password",
        $"Someone asked to reset the password of your ReapDeep account.\n\nChoose a new password:\n{link}\n\n" +
        $"The link works once and expires in {(int)ResetLifetime.TotalMinutes} minutes. Signing in with it signs out your other sessions. " +
        "If you did not ask for this, you can ignore this email; your password stays the same.");

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
