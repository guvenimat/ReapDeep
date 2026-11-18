using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReapDeep.Server;

internal sealed class LocalFlag
{
    const int Buckets = 10_000;
    readonly string _salt;
    readonly int _rolloutBuckets;
    readonly (string Key, int Upper)[] _variants;
    readonly LocalCondition[] _conditions;

    public string Key { get; }

    public LocalFlag(string key, string salt, int rolloutPercentage, IEnumerable<(string Key, int Percentage)> variants, IEnumerable<LocalCondition> conditions)
    {
        Key = key;
        _salt = salt;
        _rolloutBuckets = rolloutPercentage * (Buckets / 100);
        var upper = 0;
        _variants = variants.Select(v => (v.Key, upper += v.Percentage * (Buckets / 100))).ToArray();
        _conditions = conditions.ToArray();
    }

    public FlagValue Evaluate(string identity, IReadOnlyDictionary<string, string> properties)
    {
        foreach (var c in _conditions)
            if (!c.Matches(properties)) return FlagValue.Off;
        if (Bucket(Key, _salt, "r", identity) >= _rolloutBuckets) return FlagValue.Off;
        if (_variants.Length == 0) return new FlagValue(true, null);

        var bucket = Bucket(Key, _salt, "v", identity);
        foreach (var (variant, upper) in _variants)
            if (bucket < upper) return new FlagValue(true, variant);
        return FlagValue.Off;
    }

    internal static int Bucket(string key, string salt, string purpose, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{key}.{salt}.{purpose}.{identity}"));
        return (int)(BinaryPrimitives.ReadUInt64BigEndian(hash) % Buckets);
    }

    public static List<LocalFlag> ParseDefinitions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var flags = new List<LocalFlag>();
        foreach (var f in doc.RootElement.GetProperty("flags").EnumerateArray())
        {
            var variants = f.GetProperty("variants").EnumerateArray()
                .Select(v => (v.GetProperty("key").GetString()!, v.GetProperty("percentage").GetInt32()));
            var conditions = f.GetProperty("conditions").EnumerateArray()
                .Select(c => new LocalCondition(c.GetProperty("property").GetString()!, c.GetProperty("op").GetString()!,
                    c.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null));
            flags.Add(new LocalFlag(f.GetProperty("key").GetString()!, f.GetProperty("salt").GetString()!, f.GetProperty("rollout_percentage").GetInt32(),
                variants.ToList(), conditions.ToList()));
        }
        return flags;
    }
}

internal sealed class LocalCondition
{
    readonly string _property;
    readonly string _op;
    readonly string[] _values;
    readonly double? _number;

    public LocalCondition(string property, string op, JsonElement? value)
    {
        _property = property;
        _op = op;
        _values = value is not { } v ? []
            : v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(Text).ToArray()
            : [Text(v)];
        _number = value is { } n && op is "gt" or "gte" or "lt" or "lte" ? Number(n) : null;
    }

    public bool Matches(IReadOnlyDictionary<string, string> properties)
    {
        var found = properties.TryGetValue(_property, out var actual);
        return _op switch
        {
            "is_set" => found,
            "is_not_set" => !found,
            "eq" or "in" => found && _values.Any(v => Equal(actual!, v)),
            "neq" or "not_in" => !found || !_values.Any(v => Equal(actual!, v)),
            "contains" => found && actual!.Contains(_values[0], StringComparison.OrdinalIgnoreCase),
            "not_contains" => !found || !actual!.Contains(_values[0], StringComparison.OrdinalIgnoreCase),
            _ => found && Compare(actual!),
        };
    }

    bool Compare(string actual)
    {
        if (Number(actual) is not { } a || _number is not { } b) return false;
        return _op switch
        {
            "gt" => a > b,
            "gte" => a >= b,
            "lt" => a < b,
            "lte" => a <= b,
            _ => false,
        };
    }

    static bool Equal(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
        || (Number(actual) is { } a && Number(expected) is { } b && a == b);

    internal static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => Number(value.GetString()),
        _ => null,
    };

    internal static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
}
