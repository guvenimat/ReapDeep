using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReapDeep.QueryApi.Ai;

public sealed record AiOptions(string Provider, string BaseUrl, string Model, string? ApiKey, int DailyCallsPerProject, TimeSpan CacheTtl, int MaxOutputTokens,
    string? Effort = null)
{
    public const string Fake = "fake";
    public const string OpenAiCompatible = "openai";

    public bool Configured => Provider == Fake || !string.IsNullOrWhiteSpace(ApiKey);

    public string Label => Provider == Fake ? "fake model (tests)" : Model;

    public static AiOptions From(IConfiguration cfg)
    {
        var key = cfg["Ai:ApiKey"];
        var provider = (cfg["Ai:Provider"] ?? (string.IsNullOrWhiteSpace(key) ? "" : OpenAiCompatible)).Trim().ToLowerInvariant();
        return new AiOptions(
            provider,
            (cfg["Ai:BaseUrl"] ?? "https://api.deepseek.com").TrimEnd('/'),
            cfg["Ai:Model"] ?? "deepseek-flash",
            key,
            int.TryParse(cfg["Ai:DailyCallsPerProject"], out var calls) && calls > 0 ? calls : 200,
            TimeSpan.FromHours(int.TryParse(cfg["Ai:CacheHours"], out var hours) && hours >= 0 ? hours : 24),
            int.TryParse(cfg["Ai:MaxOutputTokens"], out var tokens) && tokens > 0 ? tokens : 4000,
            string.IsNullOrWhiteSpace(cfg["Ai:Effort"]) ? null : cfg["Ai:Effort"]!.Trim().ToLowerInvariant());
    }
}

public sealed class AiUnavailableException(string message) : Exception(message);

public interface IChatModel
{
    Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct);
}

public sealed class OpenAiCompatibleChatModel(HttpClient http, AiOptions options) : IChatModel
{
    public async Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["temperature"] = 0.2,
            ["max_tokens"] = options.MaxOutputTokens,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["reasoning_effort"] = options.Effort,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        if (options.Effort is null) body.Remove("reasoning_effort");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AiUnavailableException($"The model provider answered {(int)res.StatusCode}");
        using var doc = JsonDocument.Parse(text);
        var choice = doc.RootElement.GetProperty("choices")[0];
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length")
            throw new AiUnavailableException("The model's answer was cut off at the output limit (Ai__MaxOutputTokens); try again or raise the limit");
        return choice.GetProperty("message").GetProperty("content").GetString()
            ?? throw new AiUnavailableException("The model returned an empty answer");
    }
}

public sealed class FakeChatModel : IChatModel
{
    public Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(user);
        var task = doc.RootElement.GetProperty("task").GetString() ?? "";
        var data = doc.RootElement.GetProperty("data");
        return Task.FromResult(task switch
        {
            AiTasks.Query => FakeQuery(doc.RootElement.GetProperty("question").GetString() ?? "", data),
            AiTasks.Digest => """
                {"summary":"[fake] Weekly summary","findings":["[fake] finding"],"recommendation":"[fake] Test a change on the busiest page",
                 "caveats":["[fake] caveat"],"next_steps":["[fake] next step"],
                 "experiments":[{"name":"[fake] Shorter checkout","hypothesis":"Fewer steps raise purchases","goal_event":"purchase","flag_key":"fake-shorter-checkout"}]}
                """,
            _ => $$"""
                {"summary":"[fake] {{task}} with {{data.EnumerateObject().Count()}} data fields","findings":["[fake] finding"],
                 "recommendation":"[fake] recommendation","caveats":["[fake] caveat"],"next_steps":["[fake] next step"]}
                """,
        });
    }

    static string FakeQuery(string question, JsonElement data)
    {
        var events = data.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToList();
        var mentioned = events.Where(e => question.Contains(e, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mentioned.Count >= 2 && question.Contains("funnel", StringComparison.OrdinalIgnoreCase))
            return new JsonObject
            {
                ["kind"] = "funnels",
                ["explanation"] = "[fake] funnel",
                ["query"] = new JsonObject { ["steps"] = new JsonArray(mentioned.Select(e => (JsonNode?)new JsonObject { ["event"] = e }).ToArray()) },
            }.ToJsonString();
        return new JsonObject
        {
            ["kind"] = "trends",
            ["explanation"] = "[fake] trend",
            ["query"] = new JsonObject { ["series"] = new JsonArray(new JsonObject { ["event"] = mentioned.FirstOrDefault() ?? "$pageview", ["metric"] = "total" }) },
        }.ToJsonString();
    }
}
