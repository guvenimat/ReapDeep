namespace ReapDeep.Server;

public sealed class ReapDeepOptions
{
    public required string ServerKey { get; init; }
    public string IngestionUrl { get; init; } = "http://localhost:8000";
    public string RecommenderUrl { get; init; } = "http://localhost:8002";
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; init; } = 500;
    public int MaxQueueSize { get; init; } = 10_000;
    public TimeSpan FlagRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; init; } = 4;
    public bool SendFlagExposures { get; init; } = true;
    public Action<Exception>? OnError { get; init; }
}

public sealed record ServerEvent(
    string Event,
    string DistinctId,
    IReadOnlyDictionary<string, object?>? Properties = null,
    DateTimeOffset? Timestamp = null,
    string? EventId = null);

public sealed record ImportResult(int Accepted, int Rejected, int Duplicates)
{
    public static readonly ImportResult None = new(0, 0, 0);

    public ImportResult Add(ImportResult other) => new(Accepted + other.Accepted, Rejected + other.Rejected, Duplicates + other.Duplicates);
}

public readonly record struct FlagValue(bool Enabled, string? Variant)
{
    public static readonly FlagValue Off = new(false, null);
}

public sealed class ReapDeepException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}
