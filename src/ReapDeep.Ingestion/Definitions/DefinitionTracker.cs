using System.Collections.Concurrent;
using System.Text.Json;
using ReapDeep.Ingestion.Capture;

namespace ReapDeep.Ingestion.Definitions;

public enum DefinitionScope : byte
{
    Event,
    EventProperty,
    PersonProperty,
}

public readonly record struct DefinitionKey(long ProjectId, DefinitionScope Scope, string Name);

public sealed record Definition(DefinitionKey Key, string? Type, string? Column);

public sealed class DefinitionTracker(ILogger<DefinitionTracker> log)
{
    public const int MaxPerProjectScope = 10_000;
    const int MaxNameLength = 200;
    static readonly long RefreshAfterMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    sealed class Counter
    {
        public int Value;
    }

    readonly ConcurrentDictionary<DefinitionKey, long> _reported = new();
    readonly ConcurrentDictionary<(long, DefinitionScope), Counter> _counts = new();
    readonly ConcurrentQueue<Definition> _pending = new();

    public void Observe(long projectId, string eventName, Dictionary<string, JsonElement>? properties)
    {
        var now = Environment.TickCount64;
        Touch(new DefinitionKey(projectId, DefinitionScope.Event, eventName), default, now);
        if (properties is null) return;
        foreach (var (name, value) in properties)
        {
            Touch(new DefinitionKey(projectId, DefinitionScope.EventProperty, name), value, now);
            if (name == "$set" && value.ValueKind == JsonValueKind.Object)
                foreach (var p in value.EnumerateObject())
                    Touch(new DefinitionKey(projectId, DefinitionScope.PersonProperty, p.Name), p.Value, now);
        }
    }

    public List<Definition> Drain(int max)
    {
        var batch = new Dictionary<DefinitionKey, Definition>();
        while (batch.Count < max && _pending.TryDequeue(out var d)) batch[d.Key] = d;
        return batch.Values.ToList();
    }

    public void Forget(IEnumerable<DefinitionKey> keys)
    {
        foreach (var key in keys)
            if (_reported.TryRemove(key, out _) && _counts.TryGetValue((key.ProjectId, key.Scope), out var count))
                Interlocked.Decrement(ref count.Value);
    }

    void Touch(DefinitionKey key, JsonElement value, long now)
    {
        if (_reported.TryGetValue(key, out var last))
        {
            if (now - last < RefreshAfterMs || !_reported.TryUpdate(key, now, last)) return;
        }
        else if (!TryReserve(key, now)) return;

        _pending.Enqueue(new Definition(key,
            key.Scope == DefinitionScope.Event ? null : PropertyTypes.Infer(value),
            key.Scope == DefinitionScope.EventProperty ? EventMessage.ColumnFor(key.Name) : null));
    }

    bool TryReserve(DefinitionKey key, long now)
    {
        if (key.Name.Length is 0 or > MaxNameLength) return false;
        var count = _counts.GetOrAdd((key.ProjectId, key.Scope), _ => new Counter());
        if (Volatile.Read(ref count.Value) >= MaxPerProjectScope || !_reported.TryAdd(key, now)) return false;
        if (Interlocked.Increment(ref count.Value) == MaxPerProjectScope)
            log.LogWarning("Project {Project}: definition limit of {Limit} reached for {Scope}; new names are not recorded",
                key.ProjectId, MaxPerProjectScope, key.Scope);
        return true;
    }
}
