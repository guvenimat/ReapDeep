namespace ReapDeep.QueryApi.Metadata;

public sealed record EventDefinition(string Name, DateTime LastSeenAt);
public sealed record PropertyDefinition(string Name, string? Type, DateTime LastSeenAt);
public sealed record MetadataResponse(List<EventDefinition> Events, List<string> EventColumns, List<PropertyDefinition> EventProperties, List<PropertyDefinition> PersonProperties);
