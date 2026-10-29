using System.Text.Json;

namespace ReapDeep.QueryApi.Persons;

public sealed record PersonRow(string Id, bool IsIdentified, DateTime CreatedAt, string[] DistinctIds, JsonElement Properties);
public sealed record PersonsResponse(List<PersonRow> Persons, string? NextCursor);
