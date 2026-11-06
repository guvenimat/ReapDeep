using System.Text.Json;

namespace ReapDeep.PersonWorker.Operations;

public sealed record RoutineLoadSnapshot(string Name, string State, long CommittedTasks, long AbortedTasks, long LoadedRows, long ErrorRows, string Reason, string OtherMessage,
    string ErrorLogUrls = "");

public enum RoutineLoadAction { None, Resume, ReportBlocked }

public static class RoutineLoadRules
{
    public static readonly TimeSpan ResumeCooldown = TimeSpan.FromMinutes(1);

    public static bool IsDataError(string text) =>
        text.Contains("parse error", StringComparison.OrdinalIgnoreCase) || text.Contains("data quality", StringComparison.OrdinalIgnoreCase);

    public static bool IsManualPause(string reason) => reason.Contains("pauses routine load job", StringComparison.OrdinalIgnoreCase);

    public static RoutineLoadAction Decide(RoutineLoadSnapshot? previous, RoutineLoadSnapshot current, DateTime? lastResume, DateTime now)
    {
        if (current.State == "PAUSED")
            return IsManualPause(current.Reason) ? RoutineLoadAction.None
                : IsDataError(current.Reason) ? RoutineLoadAction.ReportBlocked
                : lastResume is { } at && now - at < ResumeCooldown ? RoutineLoadAction.None
                : RoutineLoadAction.Resume;
        if (current.State is "RUNNING" or "NEED_SCHEDULE" && previous is not null && IsDataError(current.OtherMessage)
            && current.CommittedTasks == previous.CommittedTasks && current.AbortedTasks > previous.AbortedTasks)
            return RoutineLoadAction.ReportBlocked;
        return RoutineLoadAction.None;
    }

    public static long NewErrorRows(RoutineLoadSnapshot? previous, RoutineLoadSnapshot current) =>
        previous is null ? 0 : Math.Max(0, current.ErrorRows - previous.ErrorRows);

    public static (long Committed, long Aborted, long Loaded, long Errors) ParseStatistic(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            long Get(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return (Get("committedTaskNum"), Get("abortedTaskNum"), Get("loadedRows"), Get("errorRows"));
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
