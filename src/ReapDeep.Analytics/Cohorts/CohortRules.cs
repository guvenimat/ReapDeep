using ReapDeep.Analytics.Rfm;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.Analytics.Cohorts;

public static class CohortRules
{
    public const int MaxGroups = 5;
    public const int MaxConditions = 10;
    public const int MaxWithinDays = 90;
    public const int MaxCount = 10_000;
    public const int MaxConditionFilters = 5;
    public const int MaxFunnelSteps = 8;
    public const long MaxFunnelWindowSeconds = 90L * 86400;
    public const string Dropped = "dropped";
    public const string Converted = "converted";
    static readonly string[] CountOps = ["gte", "lte", "eq"];

    public static CohortDefinition Normalize(CohortDefinition? definition)
    {
        var groups = definition?.Groups ?? [];
        Ensure.That(groups.Count is > 0 and <= MaxGroups, $"A cohort needs 1-{MaxGroups} groups");
        Ensure.That(groups.Sum(g => g.Conditions?.Count ?? 0) <= MaxConditions, $"A cohort can have at most {MaxConditions} conditions");

        return new CohortDefinition(groups.Select(g =>
        {
            var conditions = g.Conditions ?? [];
            Ensure.That(conditions.Count > 0, "Each group needs at least one condition");
            Ensure.That(conditions.Any(c => ConditionTypes.DefinesPopulation(c.Type)),
                "Each group needs a performed_event, person_property, rfm_segment or funnel condition; not_performed_event alone has no population");
            return new CohortGroup(conditions.Select(NormalizeCondition).ToList());
        }).ToList());
    }

    static CohortCondition NormalizeCondition(CohortCondition c)
    {
        Ensure.OneOf(c.Type, "type", ConditionTypes.All);
        if (c.Type == ConditionTypes.PersonProperty)
        {
            ColumnMapper.Validate(c.Property ?? "");
            Ensure.That(!string.IsNullOrEmpty(c.Op), $"Condition on '{c.Property}' requires op");
            return new CohortCondition(c.Type, Property: c.Property, Op: c.Op, Value: c.Value);
        }

        if (c.Type == ConditionTypes.RfmSegment)
        {
            var segments = (c.Segments ?? []).Distinct().ToList();
            Ensure.That(segments.Count > 0, "An rfm_segment condition needs at least one segment");
            foreach (var s in segments) Ensure.OneOf(s, "segments", RfmSegments.Keys);
            var window = c.WithinDays ?? RfmSql.WindowDays[^1];
            Ensure.That(RfmSql.WindowDays.Contains(window), $"within_days of an rfm_segment condition: {string.Join(" | ", RfmSql.WindowDays)}");
            return new CohortCondition(c.Type, Event: string.IsNullOrWhiteSpace(c.Event) ? RfmSql.DefaultEvent : Ensure.NotBlank(c.Event, "event"),
                WithinDays: window, Segments: segments);
        }

        if (c.Type == ConditionTypes.Funnel)
        {
            var steps = (c.Steps ?? []).Select(s => Ensure.NotBlank(s, "steps")).ToList();
            Ensure.That(steps.Count is >= 2 and <= MaxFunnelSteps, $"A funnel condition needs 2-{MaxFunnelSteps} steps");
            var outcome = Ensure.OneOf(c.Outcome ?? Dropped, "outcome", [Dropped, Converted]);
            var step = c.Step ?? 1;
            Ensure.That(outcome == Dropped ? step >= 1 && step < steps.Count : step >= 2 && step <= steps.Count,
                outcome == Dropped ? $"step of a dropped funnel condition: 1-{steps.Count - 1}" : $"step of a converted funnel condition: 2-{steps.Count}");
            var window = c.WindowSeconds ?? 86400;
            Ensure.That(window is >= 60 and <= MaxFunnelWindowSeconds, "window_seconds of a funnel condition must be between 60 seconds and 90 days");
            Ensure.That(c.WithinDays is >= 1 and <= MaxWithinDays, $"within_days must be between 1 and {MaxWithinDays}");
            return new CohortCondition(c.Type, WithinDays: c.WithinDays, Steps: steps, Step: step, Outcome: outcome, WindowSeconds: window);
        }

        var eventName = Ensure.NotBlank(c.Event, "event");
        Ensure.That(c.WithinDays is >= 1 and <= MaxWithinDays, $"within_days must be between 1 and {MaxWithinDays}");
        if (c.Type == ConditionTypes.NotPerformed)
            return new CohortCondition(c.Type, Event: eventName, WithinDays: c.WithinDays);

        var countOp = Ensure.OneOf(c.CountOp ?? "gte", "count_op", CountOps);
        var count = Ensure.InRange(c.Count ?? 1, 1, MaxCount, "count");
        var filters = c.Filters ?? [];
        Ensure.That(filters.Count <= MaxConditionFilters, $"A condition can have at most {MaxConditionFilters} filters");
        Ensure.That(filters.All(f => (f.Scope ?? "event") == "event"), "Filters inside a performed_event condition must use event properties");
        return new CohortCondition(c.Type, Event: eventName, CountOp: countOp, Count: count, WithinDays: c.WithinDays,
            Filters: filters.Count == 0 ? null : filters);
    }
}
