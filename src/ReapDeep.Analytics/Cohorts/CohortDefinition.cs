using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReapDeep.Analytics.Cohorts;

public sealed record CohortDefinition(List<CohortGroup>? Groups);

public sealed record CohortGroup(List<CohortCondition>? Conditions);

public sealed record CohortCondition(
    string Type,
    string? Event = null,
    string? CountOp = null,
    int? Count = null,
    int? WithinDays = null,
    List<Filter>? Filters = null,
    string? Property = null,
    string? Op = null,
    JsonElement? Value = null);

public static class ConditionTypes
{
    public const string Performed = "performed_event";
    public const string NotPerformed = "not_performed_event";
    public const string PersonProperty = "person_property";

    public static readonly string[] All = [Performed, NotPerformed, PersonProperty];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CohortDefinition))]
public partial class CohortJsonContext : JsonSerializerContext;
