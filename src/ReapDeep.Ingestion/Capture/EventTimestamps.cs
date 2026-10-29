namespace ReapDeep.Ingestion.Capture;

public readonly record struct EventTimestamps(DateTime ServerTime, TimeSpan ClockSkew)
{
    static readonly TimeSpan MaxFuture = TimeSpan.FromHours(1);
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(364);

    public static EventTimestamps For(DateTime serverTime, DateTimeOffset? sentAt) =>
        new(serverTime, sentAt is { } s ? serverTime - s.UtcDateTime : TimeSpan.Zero);

    public DateTime? Resolve(DateTimeOffset? clientTimestamp)
    {
        var ts = clientTimestamp is { } t ? t.UtcDateTime + ClockSkew : ServerTime;
        if (ts > ServerTime + MaxFuture) return ServerTime;
        return ts < ServerTime - MaxAge ? null : ts;
    }
}
