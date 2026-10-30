namespace ReapDeep.Analytics.Rfm;

public sealed record RfmSegment(string Key, string Name, string Description);

public static class RfmSegments
{
    public const string Champions = "champions";
    public const string Loyal = "loyal";
    public const string PotentialLoyalist = "potential_loyalist";
    public const string NewCustomers = "new_customers";
    public const string Promising = "promising";
    public const string NeedAttention = "need_attention";
    public const string AboutToSleep = "about_to_sleep";
    public const string AtRisk = "at_risk";
    public const string CantLose = "cant_lose";
    public const string Hibernating = "hibernating";
    public const string Lost = "lost";

    public static readonly IReadOnlyList<RfmSegment> All =
    [
        new(Champions, "Champions", "Bought recently and buy often"),
        new(Loyal, "Loyal", "Buy often; last purchase a little while ago"),
        new(PotentialLoyalist, "Potential Loyalist", "Recent customers with a few orders"),
        new(NewCustomers, "New Customers", "First order very recently"),
        new(Promising, "Promising", "Recent customers with a single order"),
        new(NeedAttention, "Need Attention", "Average recency and frequency"),
        new(AboutToSleep, "About to Sleep", "Below average recency and frequency"),
        new(AtRisk, "At Risk", "Used to buy often, not seen for a while"),
        new(CantLose, "Can't Lose", "Were among the most frequent buyers, now gone"),
        new(Hibernating, "Hibernating", "Long ago and rarely"),
        new(Lost, "Lost", "Lowest recency and frequency"),
    ];

    public static readonly string[] Keys = All.Select(s => s.Key).ToArray();

    public static string Of(int r, int f)
    {
        Ensure.That(r is >= 1 and <= 5 && f is >= 1 and <= 5, $"RFM scores must be 1-5, got R={r} F={f}");
        return r switch
        {
            1 or 2 when f == 5 => CantLose,
            1 or 2 when f >= 3 => AtRisk,
            1 when f == 1 => Lost,
            1 or 2 => Hibernating,
            3 when f >= 4 => Loyal,
            3 when f == 3 => NeedAttention,
            3 => AboutToSleep,
            4 when f >= 4 => Loyal,
            4 when f == 1 => Promising,
            4 => PotentialLoyalist,
            _ when f >= 4 => Champions,
            _ when f == 1 => NewCustomers,
            _ => PotentialLoyalist,
        };
    }

    public static IReadOnlyList<int> Cells(string segment) =>
        (from r in Enumerable.Range(1, 5) from f in Enumerable.Range(1, 5) where Of(r, f) == segment select r * 10 + f).ToList();
}
