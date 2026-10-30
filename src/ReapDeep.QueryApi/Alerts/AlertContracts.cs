namespace ReapDeep.QueryApi.Alerts;

public sealed record AlertBody(string? Name, long? InsightId, string? Condition, double? Threshold, string? Window, string? WebhookUrl, bool? Enabled);

public sealed record AlertRow(
    long Id, string Name, long InsightId, string InsightName, string Condition, double Threshold, string Window, string WebhookUrl,
    string? Secret, bool Enabled, string State, double? LastValue, double? PreviousValue, string? LastError,
    DateTime? LastCheckedAt, DateTime NextCheckAt, string? CreatedBy, DateTime CreatedAt);

public sealed record AlertsResponse(List<AlertRow> Alerts);

public sealed record AlertEventRow(long Id, string Kind, double? Value, double? PreviousValue, string Message, int? ResponseStatus, string? DeliveryError, DateTime CreatedAt);

public sealed record AlertEventsResponse(List<AlertEventRow> Events);

public sealed record AlertCheckResponse(string State, double? Value, double? PreviousValue, string Message, AlertEventRow? Event);

public sealed record AlertWebhookInsight(long Id, string Name);

public sealed record AlertWebhookPayload(
    long AlertId, string AlertName, long ProjectId, string State, string Condition, double Threshold, string Window,
    double Value, double? PreviousValue, string Message, AlertWebhookInsight Insight, DateTime CheckedAt);
