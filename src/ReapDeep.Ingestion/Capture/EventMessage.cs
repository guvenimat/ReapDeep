using System.Buffers;
using System.Text;
using System.Text.Json;

namespace ReapDeep.Ingestion.Capture;

public static class EventMessage
{
    static readonly Dictionary<string, (string Column, int MaxBytes)> Extracted = new()
    {
        ["$session_id"] = ("session_id", 64),
        ["$current_url"] = ("current_url", 4096),
        ["$host"] = ("host", 255),
        ["$pathname"] = ("pathname", 2048),
        ["$referrer"] = ("referrer", 4096),
        ["$referring_domain"] = ("referring_domain", 255),
        ["utm_source"] = ("utm_source", 255),
        ["utm_medium"] = ("utm_medium", 255),
        ["utm_campaign"] = ("utm_campaign", 255),
        ["utm_term"] = ("utm_term", 255),
        ["utm_content"] = ("utm_content", 255),
        ["$browser"] = ("browser", 64),
        ["$browser_version"] = ("browser_version", 32),
        ["$os"] = ("os", 64),
        ["$os_version"] = ("os_version", 32),
        ["$device_type"] = ("device_type", 16),
        ["$screen_width"] = ("screen_width", 0),
        ["$screen_height"] = ("screen_height", 0),
        ["$lib"] = ("lib", 32),
        ["$lib_version"] = ("lib_version", 32),
        ["item_id"] = ("item_id", 200),
    };

    public const int MaxIdBytes = 200;

    public static string? ColumnFor(string property) => Extracted.TryGetValue(property, out var c) ? c.Column : null;

    public static byte[] Build(
        long projectId, string eventName, string distinctId, string eventId,
        DateTime timestamp, DateTime insertedAt, string? ip, UserAgentParser.Result ua,
        Dictionary<string, JsonElement>? props)
    {
        var cols = new Dictionary<string, JsonElement>();
        if (props is not null)
            foreach (var (name, value) in props)
                if (Extracted.TryGetValue(name, out var c)) cols[c.Column] = value;

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("project_id", projectId);
            w.WriteString("event", eventName);
            w.WriteString("timestamp", Format(timestamp));
            w.WriteString("distinct_id", distinctId);
            w.WriteString("event_id", eventId);
            w.WriteString("ip", ip);

            foreach (var (_, (column, maxBytes)) in Extracted)
            {
                cols.TryGetValue(column, out var v);
                if (maxBytes == 0)
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt16(out var n)) w.WriteNumber(column, n);
                    else w.WriteNull(column);
                }
                else
                {
                    var s = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Number => v.GetRawText(),
                        _ => Derive(column, cols, ua),
                    };
                    w.WriteString(column, Truncate(s, maxBytes));
                }
            }

            w.WriteStartObject("properties");
            if (props is not null)
                foreach (var (name, value) in props)
                    if (!Extracted.ContainsKey(name))
                    {
                        w.WritePropertyName(name);
                        value.WriteTo(w);
                    }
            w.WriteEndObject();

            w.WriteString("inserted_at", Format(insertedAt));
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    static string? Derive(string column, Dictionary<string, JsonElement> cols, UserAgentParser.Result ua) => column switch
    {
        "host" => UrlPart(cols, "current_url", u => u.Host),
        "pathname" => UrlPart(cols, "current_url", u => u.AbsolutePath),
        "referring_domain" => UrlPart(cols, "referrer", u => u.Host),
        "browser" => ua.Browser,
        "browser_version" => ua.BrowserVersion,
        "os" => ua.Os,
        "os_version" => ua.OsVersion,
        "device_type" => ua.DeviceType,
        _ => null,
    };

    static string? UrlPart(Dictionary<string, JsonElement> cols, string column, Func<Uri, string> part) =>
        cols.TryGetValue(column, out var v) && v.ValueKind == JsonValueKind.String &&
        Uri.TryCreate(v.GetString(), UriKind.Absolute, out var uri) ? part(uri) : null;

    static string Format(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss.ffffff");

    public static string? Truncate(string? s, int maxBytes)
    {
        if (s is null || Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        var len = Math.Min(s.Length, maxBytes);
        while (len > 0 && Encoding.UTF8.GetByteCount(s.AsSpan(0, len)) > maxBytes) len--;
        if (len > 0 && char.IsHighSurrogate(s[len - 1])) len--;
        return s[..len];
    }
}
