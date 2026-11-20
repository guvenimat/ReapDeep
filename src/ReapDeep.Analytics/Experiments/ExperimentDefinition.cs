using System.Text.Json;
using System.Text.Json.Serialization;
using ReapDeep.Analytics.Flags;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.Analytics.Experiments;

public sealed record ExperimentMetric(string? Kind, string? Event = null, List<string>? Steps = null, long? WindowSeconds = null, string? Property = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExperimentMetric))]
[JsonSerializable(typeof(List<ExperimentMetric>))]
public partial class ExperimentJsonContext : JsonSerializerContext;

public static class ExperimentStatus
{
    public const string Draft = "draft";
    public const string Running = "running";
    public const string Stopped = "stopped";
}

public static class MetricKinds
{
    public const string Conversion = "conversion";
    public const string Funnel = "funnel";
    public const string Revenue = "revenue";

    public static readonly string[] All = [Conversion, Funnel, Revenue];
}

public static class ExperimentRules
{
    public const int MaxExperimentsPerProject = 200;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxEventLength = 200;
    public const int MinSteps = 2;
    public const int MaxSteps = 8;
    public const long DefaultFunnelWindowSeconds = 7 * 24 * 3600;
    public const long MaxWindowSeconds = 90 * 24 * 3600;
    public const int MaxRangeDays = 366;
    public const int MaxSecondaryMetrics = 3;

    public static List<ExperimentMetric> SecondaryMetrics(IEnumerable<ExperimentMetric>? metrics)
    {
        var list = (metrics ?? []).ToList();
        Ensure.That(list.Count <= MaxSecondaryMetrics, $"An experiment can have at most {MaxSecondaryMetrics} secondary metrics");
        return list.Select(m => Metric(m, "secondary metric")).ToList();
    }

    public static ExperimentMetric Metric(ExperimentMetric? metric, string field = "metric")
    {
        Ensure.That(metric is not null, $"{field} is required");
        var kind = Ensure.OneOf(metric.Kind, $"{field}.kind", MetricKinds.All);
        var window = metric.WindowSeconds;
        if (window is not null) Ensure.That(window is >= 1 and <= MaxWindowSeconds, $"{field}.window_seconds must be between 1 second and 90 days");
        switch (kind)
        {
            case MetricKinds.Funnel:
                var steps = (metric.Steps ?? []).Select(s => Event(s, $"{field}.steps")).ToList();
                Ensure.That(steps.Count is >= MinSteps and <= MaxSteps, $"{field}.steps must contain {MinSteps}-{MaxSteps} events");
                return new ExperimentMetric(kind, Steps: steps, WindowSeconds: window ?? DefaultFunnelWindowSeconds);
            case MetricKinds.Revenue:
                var property = ColumnMapper.Validate(metric.Property?.Trim() ?? "");
                return new ExperimentMetric(kind, Event(metric.Event, $"{field}.event"), WindowSeconds: window, Property: property);
            default:
                return new ExperimentMetric(kind, Event(metric.Event, $"{field}.event"), WindowSeconds: window);
        }
    }

    static string Event(string? name, string field)
    {
        var e = Ensure.NotBlank(name, field, MaxEventLength);
        Ensure.That(e != Sql.ExperimentSql.ExposureEvent, $"{field} cannot be the exposure event {Sql.ExperimentSql.ExposureEvent}");
        return e;
    }

    public static string Control(string? requested, IReadOnlyList<FlagVariant> variants)
    {
        Ensure.That(variants.Count >= FlagRules.MinVariants, "An experiment needs a flag with at least two variants");
        if (string.IsNullOrWhiteSpace(requested)) return variants[0].Key;
        var key = requested.Trim();
        Ensure.That(variants.Any(v => v.Key == key), $"control_variant '{key}' is not a variant of the flag ({string.Join(", ", variants.Select(v => v.Key))})");
        return key;
    }

    public static (DateTime From, DateTime To) Range(DateTime? start, DateTime? end, DateTime now)
    {
        Ensure.That(start is not null, "The experiment has not started yet");
        var to = end is { } e && e < now ? e : now;
        Ensure.That(to > start.Value, "The experiment starts in the future; there are no results yet");
        Ensure.That(to - start.Value <= TimeSpan.FromDays(MaxRangeDays), $"An experiment can be analysed over at most {MaxRangeDays} days");
        return (start.Value, to);
    }

    public static bool SameVariants(IReadOnlyList<FlagVariant> a, IReadOnlyList<FlagVariant> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Key == p.Second.Key && p.First.Percentage == p.Second.Percentage);

    public static string MetricJson(ExperimentMetric metric) => JsonSerializer.Serialize(metric, ExperimentJsonContext.Default.ExperimentMetric);

    public static ExperimentMetric ParseMetric(string json) =>
        JsonSerializer.Deserialize(json, ExperimentJsonContext.Default.ExperimentMetric) ?? new ExperimentMetric(MetricKinds.Conversion);

    public static string MetricsJson(IEnumerable<ExperimentMetric> metrics) =>
        JsonSerializer.Serialize(metrics.ToList(), ExperimentJsonContext.Default.ListExperimentMetric);

    public static List<ExperimentMetric> ParseMetrics(string json) =>
        JsonSerializer.Deserialize(json, ExperimentJsonContext.Default.ListExperimentMetric) ?? [];
}
