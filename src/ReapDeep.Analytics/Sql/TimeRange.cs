using System.Globalization;
using ReapDeep.Analytics;

namespace ReapDeep.Analytics.Sql;

public readonly record struct TimeRange(DateTime FromUtc, DateTime ToUtc, DateTime FromLocal, DateTime ToLocal, int? FixedOffsetSeconds)
{
    public static readonly string[] Intervals = ["hour", "day", "week", "month"];
    const int MaxDays = 366;
    const int MaxBuckets = 2000;

    public static TimeRange Parse(string? from, string? to, TimeZoneInfo tz, TimeSpan defaultSpan)
    {
        var toUtc = to is null ? DateTime.UtcNow : ParseBoundary(to, tz, isEnd: true, nameof(to));
        var fromUtc = from is null ? toUtc - defaultSpan : ParseBoundary(from, tz, isEnd: false, nameof(from));
        if (fromUtc >= toUtc) throw new ValidationException("date_from must be before date_to");
        if (toUtc - fromUtc > TimeSpan.FromDays(MaxDays)) throw new ValidationException($"Date range can be at most {MaxDays} days");
        return new TimeRange(fromUtc, toUtc, TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz), TimeZoneInfo.ConvertTimeFromUtc(toUtc, tz),
            FixedOffset(tz, fromUtc, toUtc));
    }

    static int? FixedOffset(TimeZoneInfo tz, DateTime fromUtc, DateTime toUtc)
    {
        var offset = tz.GetUtcOffset(fromUtc);
        for (var t = fromUtc.AddHours(1); t < toUtc; t = t.AddHours(1))
            if (tz.GetUtcOffset(t) != offset) return null;
        return tz.GetUtcOffset(toUtc) == offset ? (int)offset.TotalSeconds : null;
    }

    static DateTime ParseBoundary(string s, TimeZoneInfo tz, bool isEnd, string name)
    {
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return TimeZoneInfo.ConvertTimeToUtc(isEnd ? day.AddDays(1) : day, tz);
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime;
        throw new ValidationException($"Invalid date_{name}: '{s}' (expected YYYY-MM-DD or ISO-8601)");
    }

    public List<DateTime> Buckets(string interval)
    {
        var list = new List<DateTime>();
        for (var b = Truncate(FromLocal, interval); b < ToLocal; b = Next(b, interval))
        {
            list.Add(b);
            if (list.Count > MaxBuckets) throw new ValidationException($"At most {MaxBuckets} time buckets; choose a larger interval");
        }
        return list;
    }

    static DateTime Truncate(DateTime t, string interval) => interval switch
    {
        "hour" => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0),
        "day" => t.Date,
        "week" => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)),
        _ => new DateTime(t.Year, t.Month, 1),
    };

    static DateTime Next(DateTime t, string interval) => interval switch
    {
        "hour" => t.AddHours(1),
        "day" => t.AddDays(1),
        "week" => t.AddDays(7),
        _ => t.AddMonths(1),
    };

    public static string Format(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
