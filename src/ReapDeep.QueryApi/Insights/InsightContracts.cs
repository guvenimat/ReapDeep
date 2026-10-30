using System.Text.Json;

namespace ReapDeep.QueryApi.Insights;

public static class InsightKinds
{
    public static readonly string[] All = ["trends", "funnels", "retention", "paths", "rfm", "lifecycle"];
}

public sealed record InsightBody(string? Name, string? Kind, JsonElement? Query);
public sealed record InsightRow(long Id, string Name, string Kind, JsonElement Query, string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record InsightsResponse(List<InsightRow> Insights);

public sealed record DashboardBody(string? Name, string? Description, List<long>? InsightIds);
public sealed record DashboardRow(long Id, string Name, string Description, string? CreatedBy, DateTime UpdatedAt, int Items);
public sealed record DashboardsResponse(List<DashboardRow> Dashboards);
public sealed record DashboardDetail(long Id, string Name, string Description, DateTime UpdatedAt, List<InsightRow> Items);
