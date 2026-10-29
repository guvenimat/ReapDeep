using ReapDeep.Analytics.Sql;

namespace ReapDeep.Analytics.Cohorts;

public static class CohortRules
{
    public const int MaxGroups = 5;
    public const int MaxConditions = 10;
    public const int MaxWithinDays = 90;
    public const int MaxCount = 10_000;
    public const int MaxConditionFilters = 5;
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
            Ensure.That(conditions.Any(c => c.Type is ConditionTypes.Performed or ConditionTypes.PersonProperty),
                "Each group needs a performed_event or person_property condition; not_performed_event alone has no population");
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
