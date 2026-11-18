using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Alerts;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Catalog;
using ReapDeep.QueryApi.Experiments;
using ReapDeep.QueryApi.Infrastructure;
using ReapDeep.QueryApi.Metadata;

namespace ReapDeep.QueryApi.Ai;

public sealed class DigestService(WebAnalyticsService web, RfmService rfm, CatalogService catalog, ExperimentRepository experiments, MetadataRepository metadata,
    AiGateway ai, DigestRepository notes, EmailSender email, IConfiguration cfg, ILogger<DigestService> log)
{
    public async Task<(AiAnswer Answer, long NoteId)> WriteAsync(ProjectContext project, DigestPeriod period, string trigger, CancellationToken ct)
    {
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

        var days = (period.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).DayNumber - period.From.DayNumber + 1;
        var meta = await metadata.GetAsync(project.ProjectId, ct);
        var data = new JsonObject
        {
            ["period"] = $"{period.Label}: {period.FromText} to {period.ToText ?? "now"} ({project.TimeZoneId})",
            ["web"] = await Part("web", () => web.RunAsync(project, new WebAnalyticsRequest(period.FromText, period.ToText, "day", null, 5, false), ct),
                r => JsonSerializer.SerializeToNode(r with { TookMs = 0, Series = [] }, AppJsonContext.Default.WebAnalyticsResponse)),
            ["rfm_segments"] = await Part("rfm", () => rfm.RunAsync(project, new RfmRequest(90, null, null, null, false), ct),
                r => new JsonArray(r.Segments.Where(s => s.Persons > 0).Select(s => (JsonNode?)new JsonObject
                {
                    ["segment"] = s.Name, ["persons"] = s.Persons, ["share"] = Math.Round(s.Share, 3), ["revenue_share"] = Math.Round(s.RevenueShare, 3),
                }).ToArray())),
            ["recommendations_last_days"] = days <= 7 ? 7 : 30,
            ["recommendations"] = await Part("recommendations", () => catalog.PerformanceAsync(project, days <= 7 ? 7 : 30, ct),
                r => JsonSerializer.SerializeToNode(r.Rows, AppJsonContext.Default.ListRecommendationPerformance)),
            ["experiments"] = new JsonArray((await experiments.ListAsync(project.ProjectId, ct)).Take(20).Select(x => (JsonNode?)new JsonObject
            {
                ["name"] = x.Name, ["status"] = x.Status, ["flag"] = x.FlagKey, ["goal"] = x.Metric.Event ?? x.Metric.Kind,
            }).ToArray()),
            ["events"] = new JsonArray(meta.Events.OrderByDescending(e => e.LastSeenAt).Take(80).Select(e => (JsonNode?)e.Name).ToArray()),
        };
        var reply = await ai.AskAsync(project, AiFeatures.Digest, AiTasks.Digest, $$"""
            Task: write a strategy note for the store team covering {{period.Label}}, from these numbers: traffic and its sources, devices,
            top pages, customer segments (RFM), how recommendations perform, and the experiments that exist. Findings name the biggest
            changes and risks; the recommendation is the single most valuable thing to do next. Also propose up to 3 A/B experiments that
            are not already running: each must be a change on the website that a feature flag can switch between a control and a test
            version (a page, a component, a message, an offer shown on the site); campaigns sent outside the site, such as emails, are not
            experiments here.
            JSON schema of your reply: {"summary": string, "findings": [string, at most 5], "recommendation": string, "caveats": [string, at most 3],
            "next_steps": [string, at most 3], "experiments": [{"name": string, "hypothesis": string, "goal_event": one name from data.events,
            "flag_key": short lowercase key with letters, digits and "-"}]}.
            """, data, null, ct);
        var answer = AiParse.Answer(reply, withExperiments: true);
        var known = meta.Events.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        answer = answer with { Experiments = answer.Experiments?.Where(i => known.Contains(i.GoalEvent)).ToList() };
        var id = await notes.SaveNoteAsync(project.ProjectId, trigger, period.FromText, period.ToText ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone)).ToString("yyyy-MM-dd"),
            answer, ct);
        return (answer, id);
    }

    public async Task<string?> EmailAsync(string projectName, DigestPeriod period, AiAnswer answer, string[] to, long noteId, CancellationToken ct)
    {
        if (to.Length == 0) return null;
        if (!email.Configured) return "No SMTP server is configured; the note was saved without e-mail";
        var link = $"{(cfg["Auth:PublicUrl"] ?? "http://localhost:3000").TrimEnd('/')}/#/ai";
        var text = new StringBuilder()
            .AppendLine(answer.Summary).AppendLine();
        if (answer.Findings.Count > 0) { text.AppendLine("Findings:"); foreach (var f in answer.Findings) text.AppendLine("- " + f); text.AppendLine(); }
        if (answer.Recommendation is { } r) text.AppendLine("Recommendation: " + r).AppendLine();
        if (answer.Experiments is { Count: > 0 } ideas) { text.AppendLine("Experiment ideas:"); foreach (var i in ideas) text.AppendLine($"- {i.Name}: {i.Hypothesis} (goal: {i.GoalEvent})"); text.AppendLine(); }
        if (answer.Caveats.Count > 0) { text.AppendLine("Caveats:"); foreach (var c in answer.Caveats) text.AppendLine("- " + c); text.AppendLine(); }
        text.AppendLine($"Written by {answer.Model} from aggregated numbers; check the figures before acting on them.").AppendLine($"Open in ReapDeep: {link}");
        var result = await email.SendAsync(to, new EmailMessage($"Strategy note for {projectName}: {period.FromText} to {period.ToText ?? "today"}", text.ToString()), "ReapDeep", ct);
        if (result.Error is not null) return result.Error;
        await notes.MarkEmailedAsync(noteId, to, ct);
        return null;
    }
}

public sealed class DigestRepository(NpgsqlDataSource db)
{
    public const int KeptNotes = 50;

    public sealed record NoteRecord(long Id, string Trigger, string PeriodFrom, string PeriodTo, string EmailedTo, DateTime CreatedAt, string Answer);
    public sealed record ScheduleRecord(long ProjectId, string Frequency, int Weekday, int MonthDay, int Hour, string EmailTo, DateTime NextRunAt,
        DateTime? LastRunAt, string? LastError, string Timezone, string ProjectName);

    public async Task<long> SaveNoteAsync(long projectId, string origin, string from, string to, AiAnswer answer, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(answer, AppJsonContext.Default.AiAnswer);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO ai_notes (project_id, trigger, period_from, period_to, answer) VALUES ($1, $2, $3::date, $4::date, $5::jsonb) RETURNING id;
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = origin });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = from });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = to });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = json });
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        await conn.ExecuteAsync("""
            DELETE FROM ai_notes WHERE project_id = @ProjectId AND id NOT IN (SELECT id FROM ai_notes WHERE project_id = @ProjectId ORDER BY created_at DESC LIMIT @Keep)
            """, new { ProjectId = projectId, Keep = KeptNotes });
        return id;
    }

    public async Task MarkEmailedAsync(long noteId, string[] to, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE ai_notes SET emailed_to = @To WHERE id = @Id", new { Id = noteId, To = to });
    }

    public async Task<List<AiNote>> ListNotesAsync(long projectId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<NoteRecord>("""
            SELECT id, "trigger", period_from::text AS period_from, period_to::text AS period_to, array_to_string(emailed_to, ',') AS emailed_to, created_at,
                   answer::text AS answer
            FROM ai_notes WHERE project_id = @ProjectId ORDER BY created_at DESC LIMIT @Limit
            """, new { ProjectId = projectId, Limit = limit });
        return [.. rows.Select(r => new AiNote(r.Id, r.Trigger, r.PeriodFrom, r.PeriodTo, Split(r.EmailedTo), r.CreatedAt,
            JsonSerializer.Deserialize(r.Answer, AppJsonContext.Default.AiAnswer)!))];
    }

    const string ScheduleSelect = """
        SELECT s.project_id, s.frequency, s.weekday::int AS weekday, s.month_day::int AS month_day, s.hour::int AS hour, array_to_string(s.email_to, ',') AS email_to, s.next_run_at, s.last_run_at,
               s.last_error, p.timezone, p.name AS project_name
        FROM ai_digest_schedules s JOIN projects p ON p.id = s.project_id
        """;

    public async Task<ScheduleRecord?> GetScheduleAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ScheduleRecord>(ScheduleSelect + " WHERE s.project_id = @ProjectId", new { ProjectId = projectId });
    }

    public async Task SaveScheduleAsync(long projectId, string frequency, int weekday, int monthDay, int hour, string[] emailTo, DateTime nextRunAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO ai_digest_schedules (project_id, frequency, weekday, month_day, hour, email_to, next_run_at)
            VALUES (@ProjectId, @Frequency, @Weekday::smallint, @MonthDay::smallint, @Hour::smallint, @EmailTo, @NextRunAt)
            ON CONFLICT (project_id) DO UPDATE SET frequency = EXCLUDED.frequency, weekday = EXCLUDED.weekday, month_day = EXCLUDED.month_day,
                hour = EXCLUDED.hour, email_to = EXCLUDED.email_to, next_run_at = EXCLUDED.next_run_at, last_error = NULL, updated_at = now()
            """, new { ProjectId = projectId, Frequency = frequency, Weekday = weekday, MonthDay = monthDay, Hour = hour, EmailTo = emailTo, NextRunAt = nextRunAt });
    }

    public async Task DeleteScheduleAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM ai_digest_schedules WHERE project_id = @ProjectId", new { ProjectId = projectId });
    }

    public async Task<List<ScheduleRecord>> ClaimDueAsync(int limit, DateTime leaseUntil, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var ids = (await conn.QueryAsync<long>("""
            UPDATE ai_digest_schedules SET next_run_at = @LeaseUntil
            WHERE project_id IN (SELECT project_id FROM ai_digest_schedules WHERE next_run_at <= now() ORDER BY next_run_at LIMIT @Limit FOR UPDATE SKIP LOCKED)
            RETURNING project_id
            """, new { Limit = limit, LeaseUntil = leaseUntil })).ToArray();
        if (ids.Length == 0) return [];
        return (await conn.QueryAsync<ScheduleRecord>(ScheduleSelect + " WHERE s.project_id = ANY(@Ids)", new { Ids = ids })).AsList();
    }

    public async Task FinishRunAsync(long projectId, DateTime nextRunAt, string? error, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE ai_digest_schedules SET next_run_at = @NextRunAt, last_run_at = now(), last_error = @Error WHERE project_id = @ProjectId
            """, new { ProjectId = projectId, NextRunAt = nextRunAt, Error = error });
    }

    public static List<string> Split(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

public sealed class DigestJob(DigestRepository schedules, DigestService digests, IConfiguration cfg, ILogger<DigestJob> log) : BackgroundService
{
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(int.TryParse(cfg["Ai:DigestPollSeconds"], out var s) && s > 0 ? s : 60);
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                foreach (var schedule in await schedules.ClaimDueAsync(3, DateTime.UtcNow + Lease, stoppingToken))
                    await RunAsync(schedule, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Digest job failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    async Task RunAsync(DigestRepository.ScheduleRecord s, CancellationToken ct)
    {
        var project = ProjectContext.Create(s.ProjectId, s.Timezone, null, null, log);
        var runDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone));
        var period = DigestRules.PeriodFor(s.Frequency, runDay);
        string? error;
        try
        {
            var (answer, noteId) = await digests.WriteAsync(project, period, "schedule", ct);
            error = await digests.EmailAsync(s.ProjectName, period, answer, [.. DigestRepository.Split(s.EmailTo)], noteId, ct);
            log.LogInformation("Project {Project}: scheduled strategy note written for {From} to {To}", s.ProjectId, period.FromText, period.ToText);
        }
        catch (Exception e) when (e is AiDisabledException or AiUnavailableException or AiQuotaException or ValidationException)
        {
            error = e.Message;
            log.LogWarning("Project {Project}: scheduled strategy note skipped: {Error}", s.ProjectId, e.Message);
        }
        var next = DigestRules.NextRunUtc(s.Frequency, s.Weekday, s.MonthDay, s.Hour, project.TimeZone, DateTime.UtcNow);
        await schedules.FinishRunAsync(s.ProjectId, next, error, ct);
    }
}
