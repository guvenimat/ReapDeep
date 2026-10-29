using System.Collections.Frozen;
using System.Text.RegularExpressions;
using ReapDeep.Analytics;

namespace ReapDeep.Analytics.Sql;

public static partial class ColumnMapper
{
    public static readonly string[] EventColumnList =
    [
        "session_id", "ip",
        "current_url", "host", "pathname", "referrer", "referring_domain",
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "browser", "browser_version", "os", "os_version", "device_type", "screen_width", "screen_height",
        "lib", "lib_version", "item_id",
    ];
    public static readonly FrozenSet<string> EventColumns = EventColumnList.ToFrozenSet();

    [GeneratedRegex(@"^[\p{L}\p{N}_$][\p{L}\p{N}_$\-. ]{0,199}$")]
    private static partial Regex KeyPattern();

    public static string Validate(string property)
    {
        if (string.IsNullOrEmpty(property) || !KeyPattern().IsMatch(property))
            throw new ValidationException($"Invalid property name: '{property}'");
        return property;
    }

    public static string Expression(SqlBuilder b, string? scope, string property)
    {
        Validate(property);
        switch (scope ?? "event")
        {
            case "person":
                b.RequirePersons();
                return $"get_json_string(p.properties, {b.P(JsonPath(property))})";
            case "event":
                var column = property.TrimStart('$');
                if (EventColumns.Contains(column)) return "e." + column;
                return $"get_json_string(e.properties, {b.P(JsonPath(property))})";
            default:
                throw new ValidationException($"Invalid scope: '{scope}' (event | person)");
        }
    }

    static string JsonPath(string key) => $"$.\"{key}\"";
}
