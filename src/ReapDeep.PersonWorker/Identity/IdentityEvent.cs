using System.Globalization;
using System.Text.Json;

namespace ReapDeep.PersonWorker.Identity;

public sealed record PersonChanges(string? Set, string? SetOnce, string? Unset)
{
    public bool IsEmpty => Set is null && SetOnce is null && Unset is null;
}

public sealed record IdentityEvent(long ProjectId, string Event, string DistinctId, string? AnonId, PersonChanges Changes, long TimestampMicros)
{
    public const string Identify = "$identify";
    public const string Set = "$set";
    public const string SetOnce = "$set_once";
    public const string Unset = "$unset";
    public const int MaxUnsetKeys = 1000;

    static readonly byte[] IdentifyToken = "\"$identify\""u8.ToArray();
    static readonly byte[] SetToken = "\"$set\""u8.ToArray();
    static readonly byte[] SetOnceToken = "\"$set_once\""u8.ToArray();
    static readonly byte[] UnsetToken = "\"$unset\""u8.ToArray();

    public string? SetJson => Changes.Set;

    public bool IsIdentify => Event == Identify;

    public bool HasWork => IsIdentify || !Changes.IsEmpty;

    public static bool MayBeRelevant(ReadOnlySpan<byte> message) =>
        message.IndexOf(IdentifyToken) >= 0 || message.IndexOf(SetToken) >= 0 || message.IndexOf(SetOnceToken) >= 0 || message.IndexOf(UnsetToken) >= 0;

    public static IdentityEvent? Parse(byte[] message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            string? anon = null, set = null, setOnce = null, unset = null;
            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("$anon_distinct_id", out var a) && a.ValueKind == JsonValueKind.String) anon = a.GetString();
                if (props.TryGetProperty(Set, out var s) && s.ValueKind == JsonValueKind.Object) set = s.GetRawText();
                if (props.TryGetProperty(SetOnce, out var so) && so.ValueKind == JsonValueKind.Object) setOnce = so.GetRawText();
                if (props.TryGetProperty(Unset, out var u)) unset = UnsetKeys(u);
            }
            var eventName = root.GetProperty("event").GetString();
            var distinctId = root.GetProperty("distinct_id").GetString();
            if (string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(distinctId)) return null;

            return new IdentityEvent(root.GetProperty("project_id").GetInt64(), eventName, distinctId,
                string.IsNullOrEmpty(anon) || anon == distinctId ? null : anon, new PersonChanges(set, setOnce, unset), TimestampMicrosOf(root));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static string? UnsetKeys(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var keys = value.EnumerateArray()
            .Where(k => k.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(k.GetString()))
            .Select(k => k.GetString()!).Distinct(StringComparer.Ordinal).Take(MaxUnsetKeys).ToList();
        if (keys.Count == 0) return null;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var k in keys) w.WriteStringValue(k);
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static long ToMicros(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    static long TimestampMicrosOf(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
        DateTime.TryParseExact(ts.GetString(), "yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? ToMicros(parsed)
            : ToMicros(DateTime.UtcNow);
}
