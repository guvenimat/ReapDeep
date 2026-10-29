using System.Diagnostics.CodeAnalysis;

namespace ReapDeep.QueryApi.Infrastructure;

public sealed class ValidationException(string message) : Exception(message);

public static class Ensure
{
    public static void That([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new ValidationException(message);
    }

    public static string NotBlank(string? value, string name, int maxLength = 200)
    {
        var trimmed = value?.Trim();
        That(trimmed is { Length: > 0 } && trimmed.Length <= maxLength, $"{name} is required (at most {maxLength} characters)");
        return trimmed;
    }

    public static T InRange<T>(T value, T min, T max, string name) where T : IComparable<T>
    {
        That(value.CompareTo(min) >= 0 && value.CompareTo(max) <= 0, $"{name} must be between {min} and {max}");
        return value;
    }

    public static string OneOf(string? value, string name, params string[] allowed)
    {
        That(value is not null && allowed.Contains(value), $"{name}: {string.Join(" | ", allowed)}");
        return value;
    }
}
