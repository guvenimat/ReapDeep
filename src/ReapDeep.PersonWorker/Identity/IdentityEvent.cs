using System.Globalization;
using System.Text.Json;

namespace ReapDeep.PersonWorker.Identity;

public sealed record IdentityEvent(long ProjectId, string Event, string DistinctId, string? AnonId, string? SetJson, long TimestampMicros)
{
    public const string Identify = "$identify";
    public const string Set = "$set";

    static readonly byte[] IdentifyToken = "\"$identify\""u8.ToArray();
    static readonly byte[] SetToken = "\"$set\""u8.ToArray();

    public bool IsIdentify => Event == Identify;

    public bool HasWork => IsIdentify || SetJson is not null;

    public static bool MayBeRelevant(ReadOnlySpan<byte> message) => message.IndexOf(IdentifyToken) >= 0 || message.IndexOf(SetToken) >= 0;

    public static IdentityEvent? Parse(byte[] message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            string? anon = null, set = null;
            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("$anon_distinct_id", out var a) && a.ValueKind == JsonValueKind.String) anon = a.GetString();
                if (props.TryGetProperty(Set, out var s) && s.ValueKind == JsonValueKind.Object) set = s.GetRawText();
            }
            var eventName = root.GetProperty("event").GetString();
            var distinctId = root.GetProperty("distinct_id").GetString();
            if (string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(distinctId)) return null;

            return new IdentityEvent(root.GetProperty("project_id").GetInt64(), eventName, distinctId,
                string.IsNullOrEmpty(anon) || anon == distinctId ? null : anon, set, TimestampMicrosOf(root));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static long ToMicros(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    static long TimestampMicrosOf(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
        DateTime.TryParseExact(ts.GetString(), "yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? ToMicros(parsed)
            : ToMicros(DateTime.UtcNow);
}
