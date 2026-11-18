using System.Text.Json;
using ReapDeep.QueryApi.Experiments;

namespace ReapDeep.QueryApi.Ai;

public sealed record AiVerdict(string Decision, string Variant, string Reason);
public sealed record AiExperimentIdea(string Name, string Hypothesis, string GoalEvent, string FlagKey);

public sealed record AiAnswer(
    string Summary, List<string> Findings, string? Recommendation, List<string> Caveats, List<string> NextSteps,
    string Model, bool Cached, DateTime GeneratedAt, AiVerdict? Verdict = null, List<AiExperimentIdea>? Experiments = null);

public sealed record AiExplainRequest(string? Kind, JsonElement? Query);
public sealed record AiQueryRequest(string? Question);
public sealed record AiQueryAnswer(string Kind, JsonElement Query, string Explanation, string Model, bool Cached);

public static class AiParse
{
    const int MaxText = 1500;
    const int MaxItem = 500;
    const int MaxItems = 8;

    public static AiAnswer Answer(AiReply reply, AiVerdict? verdict = null, bool withExperiments = false) => new(
        Text(reply.Json, "summary", MaxText) ?? "The model gave no summary.",
        List(reply.Json, "findings"),
        Text(reply.Json, "recommendation", MaxText),
        List(reply.Json, "caveats"),
        List(reply.Json, "next_steps"),
        reply.Model, reply.Cached, reply.GeneratedAt, verdict,
        withExperiments ? Ideas(reply.Json) : null);

    public static string? Text(JsonElement json, string name, int max) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? AiGateway.Truncate(s, max)
            : null;

    public static List<string> List(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => AiGateway.Truncate(i.GetString()!, MaxItem))
                .Where(s => s.Length > 0).Take(MaxItems).ToList()
            : [];

    static List<AiExperimentIdea> Ideas(JsonElement json) =>
        json.TryGetProperty("experiments", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
                .Select(i => new AiExperimentIdea(Text(i, "name", 100) ?? "", Text(i, "hypothesis", MaxItem) ?? "", Text(i, "goal_event", 200) ?? "", FlagKey(Text(i, "flag_key", 100))))
                .Where(i => i.Name.Length > 0 && i.GoalEvent.Length > 0).Take(3).ToList()
            : [];

    static string FlagKey(string? key)
    {
        var chars = (key ?? "").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var k = new string(chars).Trim('-', '_');
        return k.Length == 0 ? "ai-idea" : k[..Math.Min(k.Length, 80)];
    }
}

public static class ExperimentVerdicts
{
    public const string Ship = "ship";
    public const string Stop = "stop";
    public const string KeepRunning = "keep_running";
    public const string NoDifference = "no_difference";
    public const string Investigate = "investigate";
    public const int MinPersonsPerVariant = 100;

    public static AiVerdict Decide(ExperimentResults r, string status)
    {
        if (r.Srm is { Warning: true } srm)
            return new(Investigate, "", $"The split between variants is off from the planned percentages (sample ratio check p = {srm.PValue:0.####}); fix assignment or tracking before trusting any result.");
        var small = r.Variants.Where(v => v.Persons < MinPersonsPerVariant).Select(v => v.Key).ToList();
        if (small.Count > 0)
            return new(KeepRunning, "", $"Too few people so far in {string.Join(", ", small)} (fewer than {MinPersonsPerVariant} each) to tell a real difference from noise.");
        var tested = r.Variants.Where(v => !v.IsControl && v.Comparison is not null).ToList();
        var winners = tested.Where(v => v.Comparison!.Significant && v.Comparison.Difference > 0).OrderByDescending(v => v.Comparison!.Difference).ToList();
        if (winners.Count > 0)
            return new(Ship, winners[0].Key, $"{winners[0].Key} beats {r.ControlVariant} and the 95% confidence interval of the difference stays above zero.");
        var losers = tested.Where(v => v.Comparison!.Significant && v.Comparison.Difference < 0).Select(v => v.Key).ToList();
        if (losers.Count == tested.Count && losers.Count > 0)
            return new(Stop, r.ControlVariant, $"Every variant does worse than {r.ControlVariant}; keep the control.");
        return status == "running"
            ? new(KeepRunning, "", "No variant differs from the control with 95% confidence yet; keep the experiment running until its planned end.")
            : new(NoDifference, r.ControlVariant, "The experiment ended without a variant that differs from the control with 95% confidence; keep the control or test a bolder change.");
    }
}
