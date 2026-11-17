using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReapDeep.Analytics.Flags;

public readonly record struct FlagResult(bool Enabled, string? Variant)
{
    public static readonly FlagResult Off = new(false, null);
}

public static class FlagHashing
{
    public const int Buckets = 10_000;
    public const string RolloutPurpose = "r";
    public const string VariantPurpose = "v";

    public static int Bucket(string key, string salt, string purpose, string identity)
    {
        var input = $"{key}.{salt}.{purpose}.{identity}";
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        var max = Encoding.UTF8.GetMaxByteCount(input.Length);
        Span<byte> bytes = max <= 1024 ? stackalloc byte[max] : new byte[max];
        var length = Encoding.UTF8.GetBytes(input, bytes);
        SHA256.HashData(bytes[..length], hash);
        return (int)(BinaryPrimitives.ReadUInt64BigEndian(hash) % Buckets);
    }
}

public sealed class CompiledFlag
{
    readonly string _salt;
    readonly int _rolloutBuckets;
    readonly (string Key, int Upper)[] _variants;
    readonly CompiledCondition[] _conditions;

    public string Key { get; }

    public CompiledFlag(string key, string salt, int rolloutPercentage, IReadOnlyList<FlagVariant> variants, IReadOnlyList<FlagCondition> conditions)
    {
        Key = key;
        _salt = salt;
        _rolloutBuckets = rolloutPercentage * (FlagHashing.Buckets / 100);
        var upper = 0;
        _variants = variants.Select(v => (v.Key, upper += v.Percentage * (FlagHashing.Buckets / 100))).ToArray();
        _conditions = conditions.Select(c => new CompiledCondition(c)).ToArray();
    }

    public FlagResult Evaluate(string identity, IReadOnlyDictionary<string, string> properties)
    {
        foreach (var c in _conditions)
            if (!c.Matches(properties)) return FlagResult.Off;
        if (FlagHashing.Bucket(Key, _salt, FlagHashing.RolloutPurpose, identity) >= _rolloutBuckets) return FlagResult.Off;
        if (_variants.Length == 0) return new FlagResult(true, null);

        var bucket = FlagHashing.Bucket(Key, _salt, FlagHashing.VariantPurpose, identity);
        foreach (var (variant, upper) in _variants)
            if (bucket < upper) return new FlagResult(true, variant);
        return FlagResult.Off;
    }
}

public sealed class CompiledCondition
{
    readonly string _property;
    readonly string _op;
    readonly string[] _values;
    readonly double? _number;

    public CompiledCondition(FlagCondition c)
    {
        _property = c.Property;
        _op = c.Op;
        _values = c.Value is not { } v ? []
            : v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(FlagRules.Text).ToArray()
            : [FlagRules.Text(v)];
        _number = c.Value is { } n && FlagOps.IsNumeric(c.Op) ? FlagRules.Number(n) : null;
    }

    public bool Matches(IReadOnlyDictionary<string, string> properties)
    {
        var found = properties.TryGetValue(_property, out var actual);
        return _op switch
        {
            FlagOps.IsSet => found,
            FlagOps.IsNotSet => !found,
            FlagOps.Eq or FlagOps.In => found && _values.Any(v => Equal(actual!, v)),
            FlagOps.Neq or FlagOps.NotIn => !found || !_values.Any(v => Equal(actual!, v)),
            FlagOps.Contains => found && actual!.Contains(_values[0], StringComparison.OrdinalIgnoreCase),
            FlagOps.NotContains => !found || !actual!.Contains(_values[0], StringComparison.OrdinalIgnoreCase),
            _ => found && Compare(actual!),
        };
    }

    bool Compare(string actual)
    {
        if (FlagRules.Number(actual) is not { } a || _number is not { } b) return false;
        return _op switch
        {
            FlagOps.Gt => a > b,
            FlagOps.Gte => a >= b,
            FlagOps.Lt => a < b,
            FlagOps.Lte => a <= b,
            _ => false,
        };
    }

    static bool Equal(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
        || (FlagRules.Number(actual) is { } a && FlagRules.Number(expected) is { } b && a == b);
}
