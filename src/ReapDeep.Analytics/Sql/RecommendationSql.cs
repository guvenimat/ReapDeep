namespace ReapDeep.Analytics.Sql;

public static class RecommendationSql
{
    public const string Popular = "popular";
    public const string Trending = "trending";
    public const string CoViewed = "co_viewed";
    public const string BoughtTogether = "bought_together";
    public const string ForYou = "for_you";
    public static readonly string[] Models = [Popular, Trending, CoViewed, BoughtTogether];

    public const string ViewEvent = "product_viewed";
    public const string CartEvent = "add_to_cart";
    public const string PurchaseEvent = "purchase";
    public const int PurchaseWeight = 5;
    public const int CartWeight = 3;
    public const int ViewWeight = 1;

    public const int GlobalCandidates = 200;
    public const int ItemCandidates = 50;
    public const int MaxItemsPerPerson = 50;
    public const int MinCoOccurrence = 2;
    public const int MaxHistoryItems = 30;

    public static readonly TimeSpan PopularWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan TrendingRecent = TimeSpan.FromDays(7);
    public static readonly TimeSpan TrendingBaseline = TimeSpan.FromDays(21);
    public static readonly TimeSpan CoViewedWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan BoughtTogetherWindow = TimeSpan.FromDays(90);
    public static readonly TimeSpan EvaluationWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(90);

    public static SqlQuery Build(string model, long projectId, DateTime nowUtc) => model switch
    {
        Popular => PopularItems(projectId, nowUtc - PopularWindow, nowUtc, GlobalCandidates),
        Trending => TrendingItems(projectId, nowUtc),
        CoViewed => Similar(projectId, nowUtc - CoViewedWindow, nowUtc, purchasesOnly: false, ItemCandidates),
        BoughtTogether => Similar(projectId, nowUtc - BoughtTogetherWindow, nowUtc, purchasesOnly: true, ItemCandidates),
        _ => throw new ValidationException($"Unknown model: '{model}' ({string.Join(" | ", Models)})"),
    };

    public static SqlQuery PopularItems(long projectId, DateTime fromUtc, DateTime toUtc, int limit)
    {
        var b = new SqlBuilder(projectId, fromUtc, toUtc);
        return b.Build($"WITH {PersonItems(b, projectId, fromUtc, toUtc)} " +
                       $"SELECT '' AS source, item_id, SUM(w) AS score FROM pi GROUP BY item_id ORDER BY score DESC, item_id LIMIT {limit}");
    }

    static SqlQuery TrendingItems(long projectId, DateTime nowUtc)
    {
        var from = nowUtc - TrendingRecent - TrendingBaseline;
        var b = new SqlBuilder(projectId, from, nowUtc);
        var recentFrom = b.P(nowUtc - TrendingRecent);
        var ratio = TrendingRecent.TotalDays / TrendingBaseline.TotalDays;
        return b.Build(
            $"WITH {PersonItems(b, projectId, from, nowUtc)}, " +
            $"t AS (SELECT item_id, SUM(IF(ts >= {recentFrom}, w, 0)) AS recent, SUM(IF(ts < {recentFrom}, w, 0)) * {ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)} AS expected FROM pi GROUP BY item_id) " +
            $"SELECT '' AS source, item_id, (recent - expected) / sqrt(expected + 1) AS score FROM t WHERE recent >= 3 AND recent > expected " +
            $"ORDER BY score DESC, item_id LIMIT {GlobalCandidates}");
    }

    static SqlQuery Similar(long projectId, DateTime fromUtc, DateTime toUtc, bool purchasesOnly, int perItem)
    {
        var b = new SqlBuilder(projectId, fromUtc, toUtc);
        return b.Build($"WITH {PersonItems(b, projectId, fromUtc, toUtc)}, {SimilarityCtes(b, purchasesOnly)} " +
                       $"SELECT src AS source, dst AS item_id, score FROM ranked WHERE rn <= {perItem}");
    }

    public static SqlQuery PersonHistory(long projectId, DateTime nowUtc)
    {
        var from = nowUtc - HistoryWindow;
        var b = new SqlBuilder(projectId, from, nowUtc);
        return b.Build(
            $"WITH {PersonItems(b, projectId, from, nowUtc)}, " +
            "h AS (SELECT person, item_id, w, ts, ROW_NUMBER() OVER (PARTITION BY person ORDER BY ts DESC, item_id) AS n FROM pi) " +
            $"SELECT person, item_id, w, ts FROM h WHERE n <= {MaxHistoryItems} OR w >= {PurchaseWeight} ORDER BY person, ts DESC, item_id");
    }

    public static SqlQuery HitRate(long projectId, DateTime nowUtc, int k)
    {
        var testFrom = nowUtc - EvaluationWindow;
        var trainFrom = testFrom - CoViewedWindow;
        var b = new SqlBuilder(projectId, trainFrom, nowUtc);
        var test = PersonItems(b, projectId, testFrom, nowUtc, name: "test_pi", firstSeen: true);
        var popular = $"pop AS (SELECT item_id, ROW_NUMBER() OVER (ORDER BY SUM(w) DESC, item_id) AS rn FROM pi GROUP BY item_id)";
        return b.Build(
            $"WITH {PersonItems(b, projectId, trainFrom, testFrom)}, {SimilarityCtes(b, purchasesOnly: false)}, {popular}, {test}, " +
            "seq AS (SELECT person, item_id, ROW_NUMBER() OVER (PARTITION BY person ORDER BY ts, item_id) AS rn FROM test_pi), " +
            "cases AS (SELECT a.item_id AS seen, n.item_id AS next FROM seq a JOIN seq n ON n.person = a.person AND a.rn = 1 AND n.rn = 2) " +
            "SELECT COUNT(*) AS cases, SUM(IF(r.dst IS NULL, 0, 1)) AS similar_hits, SUM(IF(cov.src IS NULL, 0, 1)) AS covered, " +
            "SUM(IF(p.item_id IS NULL, 0, 1)) AS popular_hits FROM cases c " +
            $"LEFT JOIN (SELECT src, dst FROM ranked WHERE rn <= {k}) r ON r.src = c.seen AND r.dst = c.next " +
            "LEFT JOIN (SELECT DISTINCT src FROM ranked) cov ON cov.src = c.seen " +
            $"LEFT JOIN (SELECT item_id FROM pop WHERE rn <= {k}) p ON p.item_id = c.next");
    }

    static string PersonItems(SqlBuilder b, long projectId, DateTime fromUtc, DateTime toUtc, string name = "pi", bool firstSeen = false)
    {
        var (purchase, cart, view) = (b.P(PurchaseEvent), b.P(CartEvent), b.P(ViewEvent));
        var time = firstSeen ? "MIN" : "MAX";
        return $"{name}_d AS (SELECT e.project_id, e.distinct_id, e.item_id, " +
               $"MAX(CASE e.event WHEN {purchase} THEN {PurchaseWeight} WHEN {cart} THEN {CartWeight} ELSE {ViewWeight} END) AS w, {time}(e.timestamp) AS ts " +
               $"FROM analytics.events e WHERE e.project_id = {b.P(projectId)} AND e.timestamp >= {b.P(fromUtc)} AND e.timestamp < {b.P(toUtc)} " +
               $"AND e.event IN ({purchase}, {cart}, {view}) AND e.item_id IS NOT NULL AND e.item_id <> '' GROUP BY 1, 2, 3), " +
               $"{name} AS (SELECT COALESCE(pdi.person_id, d.distinct_id) AS person, d.item_id, MAX(d.w) AS w, {time}(d.ts) AS ts FROM {name}_d d " +
               "LEFT JOIN analytics.person_distinct_ids pdi ON d.project_id = pdi.project_id AND d.distinct_id = pdi.distinct_id AND pdi.is_deleted = 0 " +
               "GROUP BY 1, 2)";
    }

    static string SimilarityCtes(SqlBuilder b, bool purchasesOnly) =>
        $"lim AS (SELECT person, item_id FROM (SELECT person, item_id, ROW_NUMBER() OVER (PARTITION BY person ORDER BY ts DESC, item_id) AS n FROM pi" +
        $"{(purchasesOnly ? $" WHERE w >= {PurchaseWeight}" : "")}) x WHERE n <= {MaxItemsPerPerson}), " +
        "cnt AS (SELECT item_id, COUNT(*) AS n FROM lim GROUP BY item_id), " +
        "pairs AS (SELECT a.item_id AS src, o.item_id AS dst, COUNT(*) AS co FROM lim a JOIN lim o ON o.person = a.person AND o.item_id <> a.item_id " +
        $"GROUP BY 1, 2 HAVING COUNT(*) >= {b.P(MinCoOccurrence)}), " +
        "scored AS (SELECT p.src, p.dst, p.co / sqrt(cs.n * cd.n) AS score FROM pairs p JOIN cnt cs ON cs.item_id = p.src JOIN cnt cd ON cd.item_id = p.dst), " +
        "ranked AS (SELECT src, dst, score, ROW_NUMBER() OVER (PARTITION BY src ORDER BY score DESC, dst) AS rn FROM scored)";
}
