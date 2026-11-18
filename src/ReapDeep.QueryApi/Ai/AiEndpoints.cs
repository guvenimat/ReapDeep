using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Actions;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Catalog;
using ReapDeep.QueryApi.Experiments;
using ReapDeep.QueryApi.Infrastructure;
using ReapDeep.QueryApi.Metadata;

namespace ReapDeep.QueryApi.Ai;

public static class AiEndpoints
{
    const int MaxBucketsInPrompt = 92;
    const int MaxQuestionLength = 500;

    const string AnswerSchema = """
        JSON schema of your reply: {"summary": string (2-4 sentences), "findings": [string, at most 5], "recommendation": string,
        "caveats": [string, at most 3], "next_steps": [string, at most 3]}.
        """;

    public static void MapAi(this RouteGroupBuilder api)
    {
        api.MapPost("/experiments/{id:long}/explain", ExplainExperiment).RequireUser();
        api.MapPost("/ai/explain", ExplainInsight).RequireUser();
        api.MapPost("/ai/query", Query).RequireUser();
        api.MapPost("/ai/digest", Digest).RequireUser();
    }

    static async Task<IResult> ExplainExperiment(long id, ProjectContext project, ExperimentRepository experiments, ExperimentService service, AiGateway ai,
        CancellationToken ct)
    {
        if (await experiments.GetAsync(project.ProjectId, id, ct) is not { } x) return ApiResults.NotFound("Experiment not found");
        if (x.StartDate is null) return ApiResults.Conflict("Start the experiment before asking for an interpretation; a draft has no results yet");
        var results = await service.RunAsync(project, x, ct);
        var verdict = ExperimentVerdicts.Decide(results, x.Status);
        var data = new JsonObject
        {
            ["experiment"] = new JsonObject
            {
                ["name"] = x.Name, ["description"] = x.Description, ["status"] = x.Status, ["flag"] = x.FlagKey,
                ["control_variant"] = x.ControlVariant, ["start_date"] = x.StartDate?.ToString("yyyy-MM-dd"), ["end_date"] = x.EndDate?.ToString("yyyy-MM-dd"),
                ["metric"] = JsonSerializer.SerializeToNode(x.Metric, ReapDeep.Analytics.Experiments.ExperimentJsonContext.Default.ExperimentMetric),
            },
            ["results"] = JsonSerializer.SerializeToNode(results with { TookMs = 0 }, AppJsonContext.Default.ExperimentResults),
            ["verdict"] = new JsonObject { ["decision"] = verdict.Decision, ["variant"] = verdict.Variant, ["reason"] = verdict.Reason },
        };
        var reply = await ai.AskAsync(project, AiFeatures.ExperimentExplain, AiTasks.Experiment, $"""
            Task: explain the result of an A/B test to the store team. "results" holds the statistics computed by ReapDeep: persons and
            conversions per variant, the difference to the control with its 95% confidence interval, the relative lift, the p-value and the
            sample ratio check. "verdict" was decided by fixed rules from these numbers: explain it and never contradict it.
            Say what the difference means in practice, how sure we can be, and what could make the result misleading (a still-running
            experiment looked at early, small samples, novelty effects, a sample ratio warning). In next_steps suggest follow-up experiments.
            {AnswerSchema}
            """, data, null, ct);
        return ApiResults.Ok(AiParse.Answer(reply, verdict), AppJsonContext.Default.AiAnswer);
    }

    static async Task<IResult> ExplainInsight(AiExplainRequest req, ProjectContext project, TrendService trends, FunnelService funnels, RetentionService retention,
        AiGateway ai, CancellationToken ct)
    {
        var kind = Ensure.OneOf(req.Kind, "kind", "trends", "funnels", "retention");
        Ensure.That(req.Query is { ValueKind: JsonValueKind.Object }, "query must be the analysis request object");
        var raw = req.Query!.Value.GetRawText();
        var (result, meaning, from, to) = kind switch
        {
            "trends" => await TrendPart(raw),
            "funnels" => await FunnelPart(raw),
            _ => await RetentionPart(raw),
        };
        var data = new JsonObject
        {
            ["date_from"] = from ?? DefaultFrom(project, kind == "trends" ? 7 : 30),
            ["date_to"] = to ?? "today",
            ["timezone"] = project.TimeZoneId,
            ["what_the_numbers_count"] = meaning,
            ["result"] = result,
        };

        async Task<(JsonNode, string, string?, string?)> TrendPart(string json)
        {
            var r = Parse(json, AppJsonContext.Default.TrendRequest);
            return (TrendData(await trends.RunAsync(project, r, ct)),
                "total = number of events; unique_users = distinct persons; sum, avg, median, p90, min, max aggregate the numeric property; formula series combine the lettered series; previous_period rows cover the same length right before the range",
                r.DateFrom, r.DateTo);
        }
        async Task<(JsonNode, string, string?, string?)> FunnelPart(string json)
        {
            var r = Parse(json, AppJsonContext.Default.FunnelRequest);
            var response = await funnels.RunAsync(project, r, ct);
            var node = JsonSerializer.SerializeToNode(response with { TookMs = 0 }, AppJsonContext.Default.FunnelResponse)!.AsObject();
            node["computed_weakest_steps"] = new JsonArray(response.Results.Take(10).Select(x => x.Steps.Skip(1).OrderBy(st => st.ConversionFromPrevious).FirstOrDefault() is { } w
                ? (JsonNode?)new JsonObject
                {
                    ["breakdown"] = x.BreakdownValue, ["step"] = w.Step, ["from_event"] = x.Steps[w.Step - 2].Event, ["to_event"] = w.Event,
                    ["conversion_from_previous"] = Math.Round(w.ConversionFromPrevious, 4),
                }
                : null).ToArray());
            return (node,
                "count = distinct persons who did the steps in this order within window_seconds of the first step; conversion values are fractions (0.5 = 50%); times are seconds; computed_weakest_steps is the step with the lowest conversion from the previous step, computed by ReapDeep: use it as the weakest step",
                r.DateFrom, r.DateTo);
        }
        async Task<(JsonNode, string, string?, string?)> RetentionPart(string json)
        {
            var r = Parse(json, AppJsonContext.Default.RetentionRequest);
            return (JsonSerializer.SerializeToNode((await retention.RunAsync(project, r, ct)) with { TookMs = 0 }, AppJsonContext.Default.RetentionResponse)!,
                "size = persons who did the start event in that period; counts[n] = how many of them did the return event n periods later; average values are fractions",
                r.DateFrom, r.DateTo);
        }
        var reply = await ai.AskAsync(project, AiFeatures.InsightExplain, AiTasks.Insight, $"""
            Task: explain a {kind} chart from ReapDeep to the store team. Point out the largest changes, peaks, drops, the weakest funnel step or
            the retention pattern, and compare with the previous period when it is given. Say when numbers are too small to mean much.
            The recommendation is one concrete thing to look at or try next.
            {AnswerSchema}
            """, data, null, ct);
        return ApiResults.Ok(AiParse.Answer(reply), AppJsonContext.Default.AiAnswer);
    }

    static async Task<IResult> Query(AiQueryRequest req, ProjectContext project, MetadataRepository metadata, ActionRepository actions, AiGateway ai,
        CancellationToken ct)
    {
        var question = Ensure.NotBlank(req.Question, "question", MaxQuestionLength);
        var meta = await metadata.GetAsync(project.ProjectId, ct);
        var actionNames = (await actions.ListAsync(project.ProjectId, ct)).Select(a => a.Name).ToList();
        var events = meta.Events.OrderByDescending(e => e.LastSeenAt).Select(e => e.Name).Take(150).ToList();
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var data = new JsonObject
        {
            ["today"] = today,
            ["timezone"] = project.TimeZoneId,
            ["events"] = new JsonArray(events.Select(e => (JsonNode?)e).ToArray()),
            ["actions"] = new JsonArray(actionNames.Select(a => (JsonNode?)a).ToArray()),
            ["event_properties"] = new JsonArray(meta.EventProperties.Select(p => (JsonNode?)p.Name).Take(100).ToArray()),
            ["event_columns"] = new JsonArray(meta.EventColumns.Select(c => (JsonNode?)c).ToArray()),
            ["known_values"] = new JsonObject { ["$device_type"] = new JsonArray("Desktop", "Mobile", "Tablet") },
            ["person_properties"] = new JsonArray(meta.PersonProperties.Select(p => (JsonNode?)p.Name).Take(60).ToArray()),
        };
        var reply = await ai.AskAsync(project, AiFeatures.Query, AiTasks.Query, """
            Task: turn the question into one ReapDeep analysis request. Use only event names from data.events (or action names from
            data.actions as {"action": name} instead of {"event": name}). Event filters and breakdowns use names from data.event_columns
            (device, browser, OS, page, referrer and UTM columns, for example "$device_type") or data.event_properties; person filters use
            data.person_properties. Use the exact spelling of data.known_values (for example "Mobile").
            Reply with {"kind": "trends" | "funnels", "explanation": string (one sentence), "query": object}.
            trends query: {"series": [{"event": name, "metric": "total" | "unique_users" | "sum" | "avg" | "median" | "p90" | "min" | "max",
              "property": numeric event property, only for sum, avg, median, p90, min, max}], "interval": "hour" | "day" | "week" | "month",
              "date_from": "YYYY-MM-DD", "date_to": "YYYY-MM-DD", "filters": [{"scope": "event" | "person", "property": name,
              "op": "eq" | "neq" | "contains" | "not_contains" | "in" | "not_in" | "gt" | "gte" | "lt" | "lte" | "is_set" | "is_not_set", "value": value}],
              "breakdown": {"scope": "event" | "person", "property": name}}
            funnels query: {"steps": [{"event": name}, ...2 to 8 steps], "window_seconds": seconds, "date_from": "YYYY-MM-DD", "date_to": "YYYY-MM-DD",
              "filters": [...as above], "breakdown": {...as above}}
            Leave out optional fields you do not need. Without a time in the question use the last 30 days ending today (data.today).
            Use a funnel for conversion between steps, a trend for counts over time.
            """, data, question, ct);

        var kind = AiParse.Text(reply.Json, "kind", 20);
        if (kind is not ("trends" or "funnels") || !reply.Json.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.Object)
            return ApiResults.Error(StatusCodes.Status422UnprocessableEntity, "The model could not turn the question into a trend or funnel; try rephrasing it");
        var known = events.Concat(meta.Events.Select(e => e.Name)).ToHashSet(StringComparer.Ordinal);
        var unknown = kind == "trends" ? UnknownSeries(query, known, actionNames) : UnknownSteps(query, known, actionNames);
        var eventProps = meta.EventProperties.Select(p => p.Name).Concat(meta.EventColumns).ToHashSet(StringComparer.Ordinal);
        var personProps = meta.PersonProperties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        unknown.AddRange(UnknownProperties(query, eventProps, personProps));
        if (unknown.Count > 0)
            return ApiResults.Error(StatusCodes.Status422UnprocessableEntity, $"The model used names this project does not have: {string.Join(", ", unknown.Take(5))}");
        return ApiResults.Ok(new AiQueryAnswer(kind, query, AiParse.Text(reply.Json, "explanation", 300) ?? "", reply.Model, reply.Cached),
            AppJsonContext.Default.AiQueryAnswer);
    }

    static async Task<IResult> Digest(ProjectContext project, WebAnalyticsService web, RfmService rfm, CatalogService catalog, ExperimentRepository experiments,
        MetadataRepository metadata, AiGateway ai, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("ReapDeep.QueryApi.Ai.Digest");
        var from = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone).Date.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        async Task<JsonNode?> Part<T>(string name, Func<Task<T>> run, Func<T, JsonNode?> map)
        {
            try
            {
                return map(await run());
            }
            catch (Exception ex) when (ex is ValidationException or InvalidOperationException or MySqlConnector.MySqlException)
            {
                log.LogInformation("Digest part {Part} skipped: {Error}", name, ex.Message);
                return null;
            }
        }

        var meta = await metadata.GetAsync(project.ProjectId, ct);
        var data = new JsonObject
        {
            ["period"] = $"last 7 days from {from}, compared with the 7 days before",
            ["web"] = await Part("web", () => web.RunAsync(project, new WebAnalyticsRequest(from, null, "day", null, 5, false), ct),
                r => JsonSerializer.SerializeToNode(r with { TookMs = 0, Series = [] }, AppJsonContext.Default.WebAnalyticsResponse)),
            ["rfm_segments"] = await Part("rfm", () => rfm.RunAsync(project, new RfmRequest(90, null, null, null, false), ct),
                r => new JsonArray(r.Segments.Where(s => s.Persons > 0).Select(s => (JsonNode?)new JsonObject
                {
                    ["segment"] = s.Name, ["persons"] = s.Persons, ["share"] = Math.Round(s.Share, 3), ["revenue_share"] = Math.Round(s.RevenueShare, 3),
                }).ToArray())),
            ["recommendations"] = await Part("recommendations", () => catalog.PerformanceAsync(project, 30, ct),
                r => JsonSerializer.SerializeToNode(r.Rows, AppJsonContext.Default.ListRecommendationPerformance)),
            ["experiments"] = new JsonArray((await experiments.ListAsync(project.ProjectId, ct)).Take(20).Select(x => (JsonNode?)new JsonObject
            {
                ["name"] = x.Name, ["status"] = x.Status, ["flag"] = x.FlagKey, ["goal"] = x.Metric.Event ?? x.Metric.Kind,
            }).ToArray()),
            ["events"] = new JsonArray(meta.Events.OrderByDescending(e => e.LastSeenAt).Take(80).Select(e => (JsonNode?)e.Name).ToArray()),
        };
        var reply = await ai.AskAsync(project, AiFeatures.Digest, AiTasks.Digest, """
            Task: write a weekly strategy note for the store team from these numbers: traffic and its sources, devices, top pages, customer
            segments (RFM), how recommendations perform, and the experiments that exist. Findings name the biggest changes and risks; the
            recommendation is the single most valuable thing to do next. Also propose up to 3 A/B experiments that are not already running:
            each must be a change on the website that a feature flag can switch between a control and a test version (a page, a component,
            a message, an offer shown on the site); campaigns sent outside the site, such as emails, are not experiments here.
            JSON schema of your reply: {"summary": string, "findings": [string, at most 5], "recommendation": string, "caveats": [string, at most 3],
            "next_steps": [string, at most 3], "experiments": [{"name": string, "hypothesis": string, "goal_event": one name from data.events,
            "flag_key": short lowercase key with letters, digits and "-"}]}.
            """, data, null, ct);
        var answer = AiParse.Answer(reply, withExperiments: true);
        var known = meta.Events.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        return ApiResults.Ok(answer with { Experiments = answer.Experiments?.Where(i => known.Contains(i.GoalEvent)).ToList() }, AppJsonContext.Default.AiAnswer);
    }

    static T Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type) ?? throw new ValidationException("query is empty");
        }
        catch (JsonException e)
        {
            throw new ValidationException($"query is not a valid request: {e.Message}");
        }
    }

    static JsonNode TrendData(TrendResponse r) => new JsonObject
    {
        ["interval"] = r.Interval,
        ["timezone"] = r.Timezone,
        ["series"] = new JsonArray(r.Results.Take(20).Select(s =>
        {
            var points = s.Data;
            var max = points.Count == 0 ? null : points.MaxBy(p => p.Count);
            var min = points.Count == 0 ? null : points.MinBy(p => p.Count);
            var o = new JsonObject
            {
                ["event"] = s.Event, ["series"] = s.Series, ["metric"] = s.Metric, ["breakdown"] = s.BreakdownValue,
                ["previous_period"] = s.Compare is not null, ["total"] = s.Total, ["buckets"] = points.Count,
                ["first"] = points.FirstOrDefault()?.Count, ["last"] = points.LastOrDefault()?.Count,
                ["max"] = max?.Count, ["max_at"] = max?.Bucket, ["min"] = min?.Count, ["min_at"] = min?.Bucket,
            };
            if (points.Count <= MaxBucketsInPrompt)
                o["values"] = new JsonArray(points.Select(p => (JsonNode?)new JsonArray(p.Bucket, p.Count)).ToArray());
            return (JsonNode?)o;
        }).ToArray()),
    };

    static IEnumerable<string> UnknownProperties(JsonElement query, HashSet<string> eventProps, HashSet<string> personProps)
    {
        var refs = new List<JsonElement>();
        if (query.TryGetProperty("filters", out var f) && f.ValueKind == JsonValueKind.Array) refs.AddRange(f.EnumerateArray());
        if (query.TryGetProperty("breakdown", out var b) && b.ValueKind == JsonValueKind.Object) refs.Add(b);
        foreach (var r in refs)
        {
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("property", out var p) || p.ValueKind != JsonValueKind.String) continue;
            var name = p.GetString()!;
            var person = r.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "person";
            if (!(person ? personProps : eventProps).Contains(name)) yield return name;
        }
    }

    static string DefaultFrom(ProjectContext project, int days) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone).Date.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static List<string> UnknownSeries(JsonElement query, HashSet<string> events, List<string> actions) =>
        query.TryGetProperty("series", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().SelectMany(i => Unknown(i, events, actions)).ToList()
            : ["(no series)"];

    static List<string> UnknownSteps(JsonElement query, HashSet<string> events, List<string> actions) =>
        query.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().SelectMany(i => Unknown(i, events, actions)).ToList()
            : ["(no steps)"];

    static IEnumerable<string> Unknown(JsonElement item, HashSet<string> events, List<string> actions)
    {
        if (item.ValueKind != JsonValueKind.Object) return ["(invalid item)"];
        if (item.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
            return actions.Contains(a.GetString()!) ? [] : [a.GetString()!];
        return item.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String && events.Contains(e.GetString()!) ? [] : [e.ValueKind == JsonValueKind.String ? e.GetString()! : "(missing event)"];
    }
}
