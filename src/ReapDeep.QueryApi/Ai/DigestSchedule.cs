using System.Globalization;
using ReapDeep.Analytics;

namespace ReapDeep.QueryApi.Ai;

public sealed record DigestPeriod(DateOnly From, DateOnly? To, string Label)
{
    public string FromText => From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string? ToText => To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed record DigestScheduleBody(string? Frequency, int? Weekday, int? MonthDay, int? Hour, List<string>? EmailTo);
public sealed record DigestScheduleResponse(string Frequency, int Weekday, int MonthDay, int Hour, List<string> EmailTo, DateTime? NextRunAt,
    DateTime? LastRunAt, string? LastError, string Timezone, bool EmailAvailable);
public sealed record AiNote(long Id, string Trigger, string PeriodFrom, string PeriodTo, List<string> EmailedTo, DateTime CreatedAt, AiAnswer Answer);
public sealed record AiNotesResponse(List<AiNote> Notes);

public static class DigestRules
{
    public const string Off = "off";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public static readonly string[] Frequencies = [Off, Daily, Weekly, Monthly];

    public static (string Frequency, int Weekday, int MonthDay, int Hour) Validate(DigestScheduleBody b)
    {
        var frequency = Ensure.OneOf(b.Frequency, "frequency", Frequencies);
        return (frequency, Ensure.InRange(b.Weekday ?? 1, 1, 7, "weekday"), Ensure.InRange(b.MonthDay ?? 1, 1, 28, "month_day"), Ensure.InRange(b.Hour ?? 9, 0, 23, "hour"));
    }

    static int IsoWeekday(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    public static DateTime NextRunUtc(string frequency, int weekday, int monthDay, int hour, TimeZoneInfo tz, DateTime afterUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), tz);
        for (var day = local.Date; day < local.Date.AddDays(400); day = day.AddDays(1))
        {
            var matches = frequency switch
            {
                Daily => true,
                Weekly => IsoWeekday(day) == weekday,
                _ => day.Day == monthDay,
            };
            if (!matches) continue;
            var at = day.AddHours(hour);
            if (tz.IsInvalidTime(at)) at = at.AddHours(1);
            if (at <= local) continue;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), tz);
        }
        throw new InvalidOperationException("No next run within 400 days");
    }

    public static DigestPeriod PeriodFor(string frequency, DateOnly runDay) => frequency switch
    {
        Daily => new(runDay.AddDays(-1), runDay.AddDays(-1), "yesterday, compared with the day before"),
        Weekly => new(runDay.AddDays(-7), runDay.AddDays(-1), "the last 7 full days, compared with the 7 days before"),
        _ => new(new DateOnly(runDay.Year, runDay.Month, 1).AddMonths(-1), new DateOnly(runDay.Year, runDay.Month, 1).AddDays(-1),
            "the previous calendar month, compared with the same number of days before it"),
    };

    public static DigestPeriod OnDemand(DateOnly today) => new(today.AddDays(-6), null, "the last 7 days up to now, compared with the 7 days before");
}
