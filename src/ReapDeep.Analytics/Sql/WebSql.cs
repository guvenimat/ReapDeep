namespace ReapDeep.Analytics.Sql;

public static class WebSql
{
    public const string Pageview = "$pageview";
    public const string Direct = "$direct";
    public const int DefaultLimit = 10;
    public const int MaxLimit = 100;

    public static readonly string[] Dimensions =
        ["entry_page", "referring_domain", "utm_source", "utm_medium", "utm_campaign", "device_type", "browser", "os"];

    public static string DefaultInterval(TimeRange range) => (range.ToUtc - range.FromUtc) switch
    {
        var span when span <= TimeSpan.FromDays(2) => "hour",
        var span when span <= TimeSpan.FromDays(92) => "day",
        _ => "week",
    };

    public static SqlQuery Overview(SqlBuilder b, DateTime currentFromUtc)
    {
        var current = b.P(currentFromUtc);
        var ev = b.Select($"{b.Person()} AS person, e.session_id AS sid, e.timestamp AS ts, IF(e.event = {b.P(Pageview)}, 1, 0) AS pv");
        return b.Build(
            $"WITH ev AS ({ev}), " +
            "s AS (SELECT sid, MIN(ts) AS first_ts, MAX(ts) AS last_ts, SUM(pv) AS pageviews FROM ev WHERE sid IS NOT NULL GROUP BY sid HAVING SUM(pv) > 0) " +
            $"SELECT IF(ts >= {current}, 1, 0) AS cur, COUNT(DISTINCT person) AS visitors, COUNT(*) AS pageviews, 0 AS sessions, 0 AS bounces, 0 AS duration " +
            "FROM ev WHERE pv = 1 GROUP BY 1 " +
            $"UNION ALL SELECT IF(first_ts >= {current}, 1, 0) AS cur, 0, 0, COUNT(*), SUM(IF(pageviews = 1, 1, 0)), SUM(seconds_diff(last_ts, first_ts)) " +
            "FROM s GROUP BY 1");
    }

    public static SqlQuery Series(SqlBuilder b, string interval, TimeRange range, string tz)
    {
        b.Where($"e.event = {b.P(Pageview)}");
        var rows = b.Select($"{SqlFragments.Bucket(b, interval, range, tz)} AS bucket, {b.Person()} AS person");
        return b.Build($"SELECT bucket, COUNT(DISTINCT person) AS visitors, COUNT(*) AS pageviews FROM ({rows}) x GROUP BY bucket");
    }

    public static SqlQuery Pages(SqlBuilder b, int limit)
    {
        b.Where($"e.event = {b.P(Pageview)}");
        var rows = b.Select($"e.pathname AS pathname, {b.Person()} AS person");
        return b.Build($"SELECT pathname, COUNT(*) AS pageviews, COUNT(DISTINCT person) AS visitors FROM ({rows}) x " +
                       $"GROUP BY pathname ORDER BY pageviews DESC, pathname LIMIT {Limit(limit)}");
    }

    public static SqlQuery Sessions(SqlBuilder b, int limit)
    {
        b.Where("e.session_id IS NOT NULL");
        var referrer = $"CASE WHEN e.referring_domain IS NULL OR lower(e.referring_domain) = lower(e.host) " +
                       $"OR lower(e.referring_domain) = concat('www.', lower(e.host)) " +
                       $"OR concat('www.', lower(e.referring_domain)) = lower(e.host) THEN {b.P(Direct)} ELSE e.referring_domain END";
        var ev = b.Select($"{b.Person()} AS person, e.session_id AS sid, e.timestamp AS ts, IF(e.event = {b.P(Pageview)}, 1, 0) AS pv, " +
                          $"e.pathname AS entry_page, {referrer} AS referring_domain, " +
                          "e.utm_source, e.utm_medium, e.utm_campaign, e.device_type, e.browser, e.os");
        const string first = "IF(pv = 1, ts, NULL)";
        var attributes = string.Join(", ", Dimensions.Select(d => $"min_by({d}, {first}) AS {d}"));
        var branches = Dimensions.Select((d, i) =>
            $"SELECT * FROM (SELECT {b.P(d)} AS dim, {d} AS value, COUNT(*) AS sessions, COUNT(DISTINCT person) AS visitors, " +
            $"SUM(IF(pageviews = 1, 1, 0)) AS bounces, SUM(duration) AS duration FROM s GROUP BY {d} " +
            $"ORDER BY sessions DESC, value LIMIT {Limit(limit)}) d{i}");
        return b.Build(
            $"WITH ev AS ({ev}), " +
            $"s AS (SELECT sid, MIN(person) AS person, SUM(pv) AS pageviews, seconds_diff(MAX(ts), MIN(ts)) AS duration, {attributes} " +
            "FROM ev GROUP BY sid HAVING SUM(pv) > 0) " +
            string.Join(" UNION ALL ", branches));
    }

    public static int Limit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}

public readonly record struct WebTotals(long Visitors, long Pageviews, long Sessions, long Bounces, long DurationSeconds)
{
    public double? BounceRate => Sessions == 0 ? null : Math.Round((double)Bounces / Sessions, 4);
    public double? AvgSessionSeconds => Sessions == 0 ? null : Math.Round((double)DurationSeconds / Sessions, 1);

    public static WebTotals Sum(IEnumerable<WebTotals> rows) => rows.Aggregate(default(WebTotals), (a, r) =>
        new WebTotals(a.Visitors + r.Visitors, a.Pageviews + r.Pageviews, a.Sessions + r.Sessions, a.Bounces + r.Bounces, a.DurationSeconds + r.DurationSeconds));
}
