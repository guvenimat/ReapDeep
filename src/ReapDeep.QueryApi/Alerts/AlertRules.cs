using System.Globalization;
using ReapDeep.Analytics;

namespace ReapDeep.QueryApi.Alerts;

public static class AlertRules
{
    public const string Above = "above";
    public const string Below = "below";
    public const string Increase = "increase";
    public const string Decrease = "decrease";
    public static readonly string[] Conditions = [Above, Below, Increase, Decrease];
    public static readonly string[] Windows = ["hour", "day", "week"];

    public const string Pending = "pending";
    public const string Ok = "ok";
    public const string Firing = "firing";
    public const string Error = "error";

    public const string Fired = "fired";
    public const string Resolved = "resolved";

    public static bool ComparesPeriods(string condition) => condition is Increase or Decrease;

    public static TimeSpan Span(string window) => window switch
    {
        "hour" => TimeSpan.FromHours(1),
        "day" => TimeSpan.FromDays(1),
        _ => TimeSpan.FromDays(7),
    };

    public static TimeSpan CheckEvery(string window) => window switch
    {
        "hour" => TimeSpan.FromMinutes(10),
        "day" => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(6),
    };

    public static void Validate(string condition, double threshold)
    {
        Ensure.OneOf(condition, "condition", Conditions);
        Ensure.That(double.IsFinite(threshold), "threshold must be a number");
        if (ComparesPeriods(condition)) Ensure.That(threshold is > 0 and <= 10_000, "threshold of a change condition is a percentage between 0 and 10000");
    }

    public static (bool Firing, string Message) Evaluate(string condition, double threshold, double value, double? previous)
    {
        var v = Format(value);
        switch (condition)
        {
            case Above:
                return (value > threshold, $"{v} is {(value > threshold ? "above" : "not above")} {Format(threshold)}");
            case Below:
                return (value < threshold, $"{v} is {(value < threshold ? "below" : "not below")} {Format(threshold)}");
        }

        var before = previous ?? 0;
        if (before == 0)
        {
            var fired = condition == Increase && value > 0;
            return (fired, $"{v} against 0 in the previous window");
        }
        var change = (value - before) / before * 100;
        var hit = condition == Increase ? change >= threshold : -change >= threshold;
        return (hit, $"{v} is {(change >= 0 ? "+" : "")}{Format(Math.Round(change, 1))}% against {Format(before)} in the previous window (limit {(condition == Increase ? "+" : "-")}{Format(threshold)}%)");
    }

    public static string? Transition(string state, bool firing) => (state, firing) switch
    {
        (not Firing, true) => Fired,
        (Firing, false) => Resolved,
        _ => null,
    };

    static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
