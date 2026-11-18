using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReapDeep.Server;

internal static class EventJson
{
    public const string Lib = "dotnet";
    public const string Version = "0.1.0";

    public static byte[] Batch(IReadOnlyList<ServerEvent> events)
    {
        var buffer = new ArrayBufferWriter<byte>(256 * events.Count);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("batch");
            foreach (var e in events) Event(w, e);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    static void Event(Utf8JsonWriter w, ServerEvent e)
    {
        w.WriteStartObject();
        w.WriteString("event", e.Event);
        w.WriteString("distinct_id", e.DistinctId);
        if (e.EventId is not null) w.WriteString("event_id", e.EventId);
        if (e.Timestamp is { } t) w.WriteString("timestamp", t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        w.WriteStartObject("properties");
        if (e.Properties is not null)
            foreach (var (name, value) in e.Properties)
            {
                if (name is "$lib" or "$lib_version") continue;
                w.WritePropertyName(name);
                Value(w, value, 0);
            }
        w.WriteString("$lib", Lib);
        w.WriteString("$lib_version", Version);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    const int MaxDepth = 16;

    internal static void Value(Utf8JsonWriter w, object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            w.WriteNullValue();
            return;
        }
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case short sh: w.WriteNumberValue(sh); break;
            case byte by: w.WriteNumberValue(by); break;
            case uint ui: w.WriteNumberValue(ui); break;
            case ulong ul: w.WriteNumberValue(ul); break;
            case decimal m: w.WriteNumberValue(m); break;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            case double or float: w.WriteNullValue(); break;
            case DateTimeOffset dto: w.WriteStringValue(dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTime dt: w.WriteStringValue((dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture)); break;
            case DateOnly date: w.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case Guid g: w.WriteStringValue(g); break;
            case Enum en: w.WriteStringValue(en.ToString()); break;
            case JsonElement je: je.WriteTo(w); break;
            case JsonNode node: node.WriteTo(w); break;
            case IEnumerable<KeyValuePair<string, object?>> map:
                w.WriteStartObject();
                foreach (var (k, v) in map)
                {
                    w.WritePropertyName(k);
                    Value(w, v, depth + 1);
                }
                w.WriteEndObject();
                break;
            case IDictionary dict:
                w.WriteStartObject();
                foreach (DictionaryEntry entry in dict)
                {
                    w.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                    Value(w, entry.Value, depth + 1);
                }
                w.WriteEndObject();
                break;
            case IEnumerable list:
                w.WriteStartArray();
                foreach (var item in list) Value(w, item, depth + 1);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    public static string FlagText(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
