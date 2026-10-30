using ReapDeep.Analytics.Rfm;

namespace ReapDeep.Analytics.Sql;

public static class RfmSql
{
    public const string DefaultEvent = "purchase";
    public const string DefaultAmountProperty = "amount";
    public const string OrderProperty = "order_id";
    public const string CurrencyProperty = "currency";
    public static readonly int[] WindowDays = [90, 180, 365];
    const string Pdi = "analytics.person_distinct_ids";

    public static SqlQuery Report(SqlBuilder b, string purchaseEvent, string amountProperty, DateTime nowUtc)
    {
        b.Where($"e.event = {b.P(purchaseEvent)}");
        var orders = b.EventsOnly(OrderColumns(b, amountProperty), "1, 2, 3");
        var scored = Scored(b, $"({orders}) o{b.PersonJoins("o")}{b.PersonWhere()}", nowUtc);
        return b.Build(
            $"WITH s AS ({scored}) SELECT s.r, s.fs, COUNT(*) AS n, SUM(s.rd), MIN(s.rd), MAX(s.rd), SUM(s.f), MIN(s.f), MAX(s.f), SUM(s.m) " +
            "FROM s GROUP BY s.r, s.fs");
    }

    public static SqlQuery Currencies(SqlBuilder b, string purchaseEvent, int limit)
    {
        b.Where($"e.event = {b.P(purchaseEvent)}");
        var currency = ColumnMapper.Expression(b, "event", CurrencyProperty);
        return b.Build($"SELECT DISTINCT c FROM ({b.Select($"{currency} AS c")}) x WHERE c IS NOT NULL AND c <> '' ORDER BY c LIMIT {limit}");
    }

    public static string Members(SqlBuilder b, string project, string purchaseEvent, int windowDays, IReadOnlyList<string> segments, DateTime nowUtc)
    {
        var cells = segments.SelectMany(RfmSegments.Cells).Distinct().Order().ToList();
        Ensure.That(cells.Count > 0, "An RFM condition needs at least one segment");
        return $"SELECT s.person FROM ({AllCustomers(b, project, purchaseEvent, null, windowDays, nowUtc)}) s WHERE s.r * 10 + s.fs IN ({string.Join(", ", cells)})";
    }

    public static SqlQuery Person(long projectId, string purchaseEvent, string amountProperty, int windowDays, DateTime nowUtc, string person)
    {
        var b = new SqlBuilder(projectId, nowUtc.AddDays(-windowDays), nowUtc);
        var all = AllCustomers(b, b.P(projectId), purchaseEvent, amountProperty, windowDays, nowUtc);
        return b.Build($"SELECT s.r, s.fs, s.rd, s.f, s.m FROM ({all}) s WHERE s.person = {b.P(person)}");
    }

    public static SqlQuery Migration(long projectId, string purchaseEvent, int windowDays, DateTime nowUtc, DateTime pastUtc)
    {
        var b = new SqlBuilder(projectId, pastUtc.AddDays(-windowDays), nowUtc);
        var project = b.P(projectId);
        var past = AllCustomers(b, project, purchaseEvent, null, windowDays, pastUtc);
        var now = AllCustomers(b, project, purchaseEvent, null, windowDays, nowUtc);
        return b.Build($"SELECT p.r, p.fs, c.r, c.fs, COUNT(*) FROM ({past}) p FULL OUTER JOIN ({now}) c ON p.person = c.person GROUP BY 1, 2, 3, 4");
    }

    static string AllCustomers(SqlBuilder b, string project, string purchaseEvent, string? amountProperty, int windowDays, DateTime asOfUtc)
    {
        var orders = $"SELECT {OrderColumns(b, amountProperty)} FROM analytics.events e WHERE e.project_id = {project} " +
                     $"AND e.timestamp >= {b.P(asOfUtc.AddDays(-windowDays))} AND e.timestamp < {b.P(asOfUtc)} " +
                     $"AND e.event = {b.P(purchaseEvent)} GROUP BY 1, 2, 3";
        var from = $"({orders}) o LEFT JOIN {Pdi} pdi ON o.project_id = pdi.project_id AND o.distinct_id = pdi.distinct_id AND pdi.is_deleted = 0";
        return Scored(b, from, asOfUtc);
    }

    static string OrderColumns(SqlBuilder b, string? amountProperty)
    {
        var order = ColumnMapper.Expression(b, "event", OrderProperty);
        var amount = amountProperty is null ? "0" : $"CAST({ColumnMapper.Expression(b, "event", amountProperty)} AS DOUBLE)";
        var money = amountProperty is null ? "0" : $"SUM(IF({amount} > 0, {amount}, 0))";
        return $"e.project_id, e.distinct_id, COALESCE(NULLIF({order}, ''), e.event_id) AS ok, MAX(e.timestamp) AS ts, {money} AS m";
    }

    static string Scored(SqlBuilder b, string ordersFrom, DateTime nowUtc) =>
        $"SELECT pp.person, pp.f, pp.m, timestampdiff(DAY, pp.last_ts, {b.P(nowUtc)}) AS rd, {Score("pp.last_ts")} AS r, {Score("pp.f")} AS fs " +
        $"FROM (SELECT COALESCE(pdi.person_id, o.distinct_id) AS person, MAX(o.ts) AS last_ts, COUNT(DISTINCT o.ok) AS f, SUM(o.m) AS m " +
        $"FROM {ordersFrom} GROUP BY 1) pp";

    static string Score(string column) => $"LEAST(5, 1 + FLOOR(5 * (RANK() OVER (ORDER BY {column}) - 1) / COUNT(*) OVER ()))";
}
