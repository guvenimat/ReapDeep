namespace ReapDeep.QueryApi.Alerts;

public sealed class AlertJob(AlertRepository alerts, AlertService service, IConfiguration cfg, ILogger<AlertJob> log) : BackgroundService
{
    const int BatchSize = 5;
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(int.TryParse(cfg["Alerts:PollSeconds"], out var s) && s > 0 ? s : 30);
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                foreach (var alert in await alerts.ClaimDueAsync(BatchSize, DateTime.UtcNow + Lease, stoppingToken))
                    await service.EvaluateAsync(alert, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Alert job failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
