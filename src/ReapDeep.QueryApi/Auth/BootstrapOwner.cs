using Npgsql;

namespace ReapDeep.QueryApi.Auth;

public sealed class BootstrapOwner(AccountRepository accounts, IConfiguration cfg, ILogger<BootstrapOwner> log) : IHostedService
{
    const int MaxAttempts = 30;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken ct)
    {
        var email = cfg["Auth:BootstrapEmail"];
        var password = cfg["Auth:BootstrapPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return;
        email = SessionService.NormalizeEmail(email);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (await accounts.CreateFirstOwnerAsync(email, email.Split('@')[0], PasswordHasher.Hash(password), ct))
                    log.LogInformation("Created bootstrap user {Email} as owner of all organizations", email);
                return;
            }
            catch (NpgsqlException ex) when (attempt < MaxAttempts)
            {
                log.LogWarning("Bootstrap user check failed ({Message}), retrying", ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
