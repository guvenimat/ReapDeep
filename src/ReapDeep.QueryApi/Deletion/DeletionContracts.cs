namespace ReapDeep.QueryApi.Deletion;

public sealed record DeletionRequestBody(string? TargetType, string? TargetId);
public sealed record DeletionRequestRow(long Id, string TargetType, string TargetId, string? RequestedBy, DateTime RequestedAt, DateTime? ProcessedAt);
public sealed record DeletionRequestsResponse(List<DeletionRequestRow> DeletionRequests);
