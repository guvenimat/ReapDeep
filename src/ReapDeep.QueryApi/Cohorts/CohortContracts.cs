using System.Text.Json;
using ReapDeep.Analytics.Cohorts;

namespace ReapDeep.QueryApi.Cohorts;

public static class CohortKinds
{
    public const string Dynamic = "dynamic";
    public const string Static = "static";

    public static readonly string[] All = [Dynamic, Static];
}

public sealed record CohortBody(string? Name, string? Description, string? Kind, CohortDefinition? Definition, List<string>? Members);
public sealed record CohortMembersBody(List<string>? Members);
public sealed record CohortRow(
    long Id, string Name, string Description, string Kind, JsonElement? Definition, string Status,
    long? MemberCount, int? StaticMembers, DateTime? CalculatedAt, int? CalculationMs, string? LastError,
    string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record CohortsResponse(List<CohortRow> Cohorts);

public sealed record CohortPreviewRequest(CohortDefinition? Definition);
public sealed record CohortPreviewResponse(long Persons, double TookMs);

public sealed record CohortPerson(string Id, bool IsIdentified, string[] DistinctIds, JsonElement Properties);
public sealed record CohortPersonsResponse(List<CohortPerson> Persons, string? NextCursor);

public sealed record CohortUsage(long Id, string Name, string Kind);
public sealed record CohortUsageResponse(List<CohortUsage> Insights);
