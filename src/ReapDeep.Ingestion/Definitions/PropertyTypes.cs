using System.Globalization;
using System.Text.Json;

namespace ReapDeep.Ingestion.Definitions;

public static class PropertyTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string DateTime = "datetime";

    public static string? Infer(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => Number,
        JsonValueKind.True or JsonValueKind.False => Boolean,
        JsonValueKind.String => LooksLikeDate(value.GetString()) ? DateTime : String,
        _ => null,
    };

    static bool LooksLikeDate(string? s) =>
        s is { Length: >= 10 and <= 35 } && char.IsAsciiDigit(s[0]) && s[4] == '-' && s[7] == '-' &&
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
}
