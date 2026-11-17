using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.Analytics.Flags;

public sealed record FlagVariant(string Key, int Percentage);

public sealed record FlagCondition(string Property, string Op, JsonElement? Value = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<FlagVariant>))]
[JsonSerializable(typeof(List<FlagCondition>))]
public partial class FlagJsonContext : JsonSerializerContext;

public static class FlagOps
{
    public const string Eq = "eq";
    public const string Neq = "neq";
    public const string Contains = "contains";
    public const string NotContains = "not_contains";
    public const string In = "in";
    public const string NotIn = "not_in";
    public const string Gt = "gt";
    public const string Gte = "gte";
    public const string Lt = "lt";
    public const string Lte = "lte";
    public const string IsSet = "is_set";
    public const string IsNotSet = "is_not_set";

    public static readonly string[] All = [Eq, Neq, Contains, NotContains, In, NotIn, Gt, Gte, Lt, Lte, IsSet, IsNotSet];

    public static bool IsNumeric(string op) => op is Gt or Gte or Lt or Lte;
    public static bool IsList(string op) => op is In or NotIn;
    public static bool HasValue(string op) => op is not (IsSet or IsNotSet);
}

public static partial class FlagRules
{
    public const int MaxVariants = 10;
    public const int MinVariants = 2;
    public const int MaxConditions = 20;
    public const int MaxListValues = 100;
    public const int MaxValueLength = 500;
    public const int MaxFlagsPerProject = 200;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,49}$")]
    private static partial Regex VariantPattern();

    public static string Key(string? key)
    {
        var k = key?.Trim() ?? "";
        Ensure.That(KeyPattern().IsMatch(k), "key must be 1-100 characters: letters, digits, '-' and '_', starting with a letter or digit");
        return k;
    }

    public static int Rollout(int? percentage) => Ensure.InRange(percentage ?? 100, 0, 100, "rollout_percentage");

    public static List<FlagVariant> Variants(List<FlagVariant>? variants)
    {
        var list = variants ?? [];
        if (list.Count == 0) return [];
        Ensure.That(list.Count is >= MinVariants and <= MaxVariants, $"A multivariate flag needs {MinVariants}-{MaxVariants} variants");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FlagVariant>(list.Count);
        foreach (var v in list)
        {
            var key = v?.Key?.Trim() ?? "";
            Ensure.That(VariantPattern().IsMatch(key), "variant key must be 1-50 characters: letters, digits, '-' and '_', starting with a letter or digit");
            Ensure.That(seen.Add(key), $"Duplicate variant key: '{key}'");
            Ensure.InRange(v!.Percentage, 0, 100, $"percentage of variant '{key}'");
            result.Add(new FlagVariant(key, v.Percentage));
        }
        var sum = result.Sum(v => v.Percentage);
        Ensure.That(sum == 100, $"Variant percentages must add up to 100 (got {sum})");
        return result;
    }

    public static List<FlagCondition> Conditions(List<FlagCondition>? conditions)
    {
        var list = conditions ?? [];
        Ensure.That(list.Count <= MaxConditions, $"A flag can have at most {MaxConditions} conditions");
        return list.Select(Condition).ToList();
    }

    static FlagCondition Condition(FlagCondition? c)
    {
        Ensure.That(c is not null, "A condition cannot be empty");
        var property = ColumnMapper.Validate(c.Property?.Trim() ?? "");
        var op = Ensure.OneOf(c.Op, "op", FlagOps.All);
        if (!FlagOps.HasValue(op)) return new FlagCondition(property, op);

        var value = c.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } v
            ? v
            : throw new ValidationException($"Condition on '{property}' requires a value");
        if (FlagOps.IsList(op))
        {
            Ensure.That(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= MaxListValues,
                $"'{op}' on '{property}' requires an array of 1-{MaxListValues} values");
            foreach (var item in value.EnumerateArray()) Scalar(item, property);
        }
        else if (FlagOps.IsNumeric(op))
        {
            Ensure.That(Number(value) is not null, $"'{op}' on '{property}' requires a number");
        }
        else
        {
            Scalar(value, property);
        }
        return new FlagCondition(property, op, value.Clone());
    }

    static void Scalar(JsonElement value, string property)
    {
        Ensure.That(value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
            $"Values of '{property}' must be strings, numbers or booleans");
        Ensure.That(Text(value).Length <= MaxValueLength, $"Values of '{property}' can be at most {MaxValueLength} characters");
    }

    public static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    public static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => Number(value.GetString()),
        _ => null,
    };

    public static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    public static string VariantsJson(List<FlagVariant> variants) => JsonSerializer.Serialize(variants, FlagJsonContext.Default.ListFlagVariant);

    public static string ConditionsJson(List<FlagCondition> conditions) => JsonSerializer.Serialize(conditions, FlagJsonContext.Default.ListFlagCondition);

    public static List<FlagVariant> ParseVariants(string json) => JsonSerializer.Deserialize(json, FlagJsonContext.Default.ListFlagVariant) ?? [];

    public static List<FlagCondition> ParseConditions(string json) => JsonSerializer.Deserialize(json, FlagJsonContext.Default.ListFlagCondition) ?? [];
}
