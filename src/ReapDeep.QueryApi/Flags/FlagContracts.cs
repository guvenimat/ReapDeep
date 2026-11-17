using ReapDeep.Analytics.Flags;

namespace ReapDeep.QueryApi.Flags;

public sealed record FlagBody(
    string? Key, string? Name, string? Description, bool? Enabled, int? RolloutPercentage, List<FlagVariant>? Variants, List<FlagCondition>? Conditions);

public sealed record FeatureFlag(
    long Id, string Key, string Name, string Description, bool Enabled, int RolloutPercentage, List<FlagVariant> Variants, List<FlagCondition> Conditions,
    string? CreatedBy, string? UpdatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record FeatureFlagsResponse(List<FeatureFlag> Flags);
