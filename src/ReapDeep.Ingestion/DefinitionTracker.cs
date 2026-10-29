using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace ReapDeep.Ingestion;

public sealed class DefinitionTracker(NpgsqlDataSource db, ILogger<DefinitionTracker> log) : BackgroundService
{
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    static readonly long RefreshAfterMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    const int MaxPerProjectScope = 10_000;
    const int MaxNameLength = 200;

    public readonly record struct Key(long ProjectId, byte Scope, string Name);
    readonly record struct Pending(Key Key, string? Type, string? Column);

    const byte EventScope = 0;
    const byte EventPropertyScope = 1;
    const byte PersonPropertyScope = 2;

    readonly ConcurrentDictionary<Key, long> _reported = new();
    readonly ConcurrentDictionary<(long, byte), StrongBox> _counts = new();
    readonly ConcurrentQueue<Pending> _pending = new();

    sealed class StrongBox { public int Value; }

    public void Observe(long projectId, string eventName, Dictionary<string, JsonElement>? props)
    {
        var now = Environment.TickCount64;
        Touch(new Key(projectId, EventScope, eventName), default, now);
        if (props is null) return;
        foreach (var (name, value) in props)
        {
            Touch(new Key(projectId, EventPropertyScope, name), value, now);
            if (name == "$set" && value.ValueKind == JsonValueKind.Object)
                foreach (var p in value.EnumerateObject())
                    Touch(new Key(projectId, PersonPropertyScope, p.Name), p.Value, now);
        }
    }

    void Touch(Key key, JsonElement value, long now)
    {
        if (_reported.TryGetValue(key, out var last))
        {
            if (now - last < RefreshAfterMs || !_reported.TryUpdate(key, now, last)) return;
        }
        else
        {
            if (key.Name.Length is 0 or > MaxNameLength) return;
            var count = _counts.GetOrAdd((key.ProjectId, key.Scope), _ => new StrongBox());
            if (Volatile.Read(ref count.Value) >= MaxPerProjectScope || !_reported.TryAdd(key, now)) return;
            if (Interlocked.Increment(ref count.Value) == MaxPerProjectScope)
                log.LogWarning("Project {Project}: definition limit of {Limit} reached for scope {Scope}; new names are not recorded", key.ProjectId, MaxPerProjectScope, key.Scope);
        }
        _pending.Enqueue(new Pending(key, key.Scope == EventScope ? null : TypeOf(value),
            key.Scope == EventPropertyScope ? EventMessage.ColumnFor(key.Name) : null));
    }

    static string? TypeOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.String => LooksLikeDate(v.GetString()) ? "datetime" : "string",
        _ => null,
    };

    static bool LooksLikeDate(string? s) =>
        s is { Length: >= 10 and <= 35 } && char.IsAsciiDigit(s[0]) && s[4] == '-' && s[7] == '-' &&
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(FlushInterval, ct); }
            catch (OperationCanceledException) { }
            await Flush();
        }
    }

    async Task Flush()
    {
        var batch = new Dictionary<Key, Pending>();
        while (batch.Count < 50_000 && _pending.TryDequeue(out var p)) batch[p.Key] = p;
        if (batch.Count == 0) return;

        var now = DateTime.UtcNow;
        var events = batch.Values.Where(p => p.Key.Scope == EventScope).ToList();
        var props = batch.Values.Where(p => p.Key.Scope != EventScope).ToList();
        try
        {
            await using var conn = await db.OpenConnectionAsync();
            await using var b = new NpgsqlBatch(conn);
            if (events.Count > 0)
            {
                var cmd = new NpgsqlBatchCommand("""
                    INSERT INTO event_definitions (project_id, name, first_seen_at, last_seen_at)
                    SELECT p, n, @t, @t FROM unnest(@p, @n) AS u(p, n)
                    WHERE EXISTS (SELECT 1 FROM projects WHERE id = u.p)
                    ON CONFLICT (project_id, name) DO UPDATE SET last_seen_at = GREATEST(event_definitions.last_seen_at, EXCLUDED.last_seen_at)
                    """);
                cmd.Parameters.AddWithValue("t", now);
                cmd.Parameters.AddWithValue("p", events.Select(e => e.Key.ProjectId).ToArray());
                cmd.Parameters.AddWithValue("n", events.Select(e => e.Key.Name).ToArray());
                b.BatchCommands.Add(cmd);
            }
            if (props.Count > 0)
            {
                var cmd = new NpgsqlBatchCommand("""
                    INSERT INTO property_definitions (project_id, scope, name, property_type, column_name, last_seen_at)
                    SELECT p, s, n, ty, c, @t FROM unnest(@p, @s, @n, @ty, @c) AS u(p, s, n, ty, c)
                    WHERE EXISTS (SELECT 1 FROM projects WHERE id = u.p)
                    ON CONFLICT (project_id, scope, name) DO UPDATE SET
                        last_seen_at = GREATEST(property_definitions.last_seen_at, EXCLUDED.last_seen_at),
                        property_type = COALESCE(property_definitions.property_type, EXCLUDED.property_type),
                        column_name = COALESCE(property_definitions.column_name, EXCLUDED.column_name)
                    """);
                cmd.Parameters.AddWithValue("t", now);
                cmd.Parameters.AddWithValue("p", props.Select(e => e.Key.ProjectId).ToArray());
                cmd.Parameters.AddWithValue("s", props.Select(e => e.Key.Scope == PersonPropertyScope ? "person" : "event").ToArray());
                cmd.Parameters.AddWithValue("n", props.Select(e => e.Key.Name).ToArray());
                cmd.Parameters.AddWithValue("ty", props.Select(e => e.Type).ToArray());
                cmd.Parameters.AddWithValue("c", props.Select(e => e.Column).ToArray());
                b.BatchCommands.Add(cmd);
            }
            await b.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to write {Count} event/property definitions; they are retried when seen again", batch.Count);
            foreach (var key in batch.Keys)
                if (_reported.TryRemove(key, out _) && _counts.TryGetValue((key.ProjectId, key.Scope), out var count))
                    Interlocked.Decrement(ref count.Value);
        }
    }
}
