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
    JsonElement? Value = null,
    List<string>? Segments = null,
    List<string>? Steps = null,
    int? Step = null,
    string? Outcome = null,
    long? WindowSeconds = null);

public static class ConditionTypes
{
    public const string Performed = "performed_event";
    public const string NotPerformed = "not_performed_event";
    public const string PersonProperty = "person_property";
    public const string RfmSegment = "rfm_segment";
    public const string Funnel = "funnel";

    public static readonly string[] All = [Performed, NotPerformed, PersonProperty, RfmSegment, Funnel];
    public static bool DefinesPopulation(string type) => type is Performed or PersonProperty or RfmSegment or Funnel;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CohortDefinition))]
public partial class CohortJsonContext : JsonSerializerContext;
