namespace ReapDeep.QueryApi.Alerts;

public sealed class AlertJob(AlertRepository alerts, AlertService service, IConfiguration cfg, ILogger<AlertJob> log) : BackgroundService
{
    const int BatchSize = 5;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

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
                foreach (var delivery in await alerts.ClaimRetriesAsync(BatchSize, DateTime.UtcNow + Lease, stoppingToken))
                    await service.DeliverAsync(delivery.EventId, delivery.AlertId, delivery.Channel, delivery.WebhookUrl, delivery.EmailTo, delivery.Secret,
                        delivery.Payload, delivery.Attempts + 1, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Alert job failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
