using System.Threading.Channels;
using ReapDeep.QueryApi.Alerts;

namespace ReapDeep.QueryApi.Auth;

public readonly record struct MemberLinkResult(string? Link, DateTime ExpiresAt);

public sealed class AccountMailer(AuthTokenRepository tokens, EmailSender email, IConfiguration cfg, ILogger<AccountMailer> log) : BackgroundService
{
    public const string SenderName = "ReapDeep";
    public const int MaxInvitesPerHour = 10;

    readonly Channel<string> _resets = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly string _publicUrl = cfg["Auth:PublicUrl"] ?? "http://localhost:3000";

    public bool CanSend => email.Configured;

    public void QueueReset(string address)
    {
        if (!_resets.Writer.TryWrite(address)) log.LogWarning("Password reset queue is full; a request was dropped");
    }

    public async Task<MemberLinkResult> CreateLinkAsync(long userId, string purpose, CancellationToken ct)
    {
        var token = AuthTokens.NewToken();
        var lifetime = AuthTokens.Lifetime(purpose);
        var limit = purpose == AuthTokens.Invite ? MaxInvitesPerHour : AuthTokens.MaxResetsPerHour;
        return await tokens.CreateAsync(userId, purpose, AuthTokens.Hash(token), lifetime, limit, ct)
            ? new MemberLinkResult(AuthTokens.Link(_publicUrl, purpose, token), DateTime.UtcNow + lifetime)
            : new MemberLinkResult(null, default);
    }

    public async Task<string?> SendInviteAsync(long projectId, long inviterId, long userId, string address, CancellationToken ct)
    {
        if (await tokens.InviteContextAsync(projectId, inviterId, userId, ct) is not { } context) return "Member not found";
        var token = AuthTokens.NewToken();
        if (!await tokens.CreateAsync(userId, AuthTokens.Invite, AuthTokens.Hash(token), AuthTokens.InviteLifetime, MaxInvitesPerHour, ct))
            return "Too many invitations for this user in the last hour";
        var message = AuthTokens.InviteEmail(context.Inviter, context.Organization, AuthTokens.Link(_publicUrl, AuthTokens.Invite, token));
        var result = await email.SendAsync([address], message, SenderName, ct);
        return result.Error;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var address in _resets.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendResetAsync(address, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Password reset email failed");
            }
        }
    }

    async Task SendResetAsync(string address, CancellationToken ct)
    {
        if (await tokens.FindUserIdAsync(address, ct) is not { } userId) return;
        var token = AuthTokens.NewToken();
        if (!await tokens.CreateAsync(userId, AuthTokens.Reset, AuthTokens.Hash(token), AuthTokens.ResetLifetime, AuthTokens.MaxResetsPerHour, ct))
        {
            log.LogInformation("Password reset limit reached for user {UserId}", userId);
            return;
        }
        var result = await email.SendAsync([address], AuthTokens.ResetEmail(AuthTokens.Link(_publicUrl, AuthTokens.Reset, token)), SenderName, ct);
        if (result.Error is not null) log.LogWarning("Password reset email to user {UserId} failed: {Error}", userId, result.Error);
    }
}
