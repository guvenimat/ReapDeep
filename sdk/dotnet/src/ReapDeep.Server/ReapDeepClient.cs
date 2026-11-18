using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ReapDeep.Server;

public sealed class ReapDeepClient : IAsyncDisposable
{
    public const int MaxImportBatch = 5000;
    const int MaxExposures = 50_000;
    static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    readonly ReapDeepOptions _options;
    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly Uri _batchUrl;
    readonly Uri _flagsUrl;
    readonly ConcurrentQueue<ServerEvent> _queue = new();
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly SemaphoreSlim _flushLock = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<string, string> _exposed = new(StringComparer.Ordinal);
    readonly Task _flushLoop;
    readonly bool _background;
    Task? _flagLoop;
    int _queued;
    long _dropped;
    volatile Dictionary<string, LocalFlag>? _flags;

    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public ReapDeepClient(ReapDeepOptions options, HttpClient? httpClient = null) : this(options, httpClient, background: true)
    {
    }

    internal ReapDeepClient(ReapDeepOptions options, HttpClient? httpClient, bool background)
    {
        if (string.IsNullOrWhiteSpace(options.ServerKey)) throw new ArgumentException("ServerKey is required", nameof(options));
        if (options.BatchSize is < 1 or > MaxImportBatch) throw new ArgumentException($"BatchSize must be between 1 and {MaxImportBatch}", nameof(options));
        if (options.MaxQueueSize < options.BatchSize) throw new ArgumentException("MaxQueueSize must be at least BatchSize", nameof(options));
        if (options.MaxAttempts < 1) throw new ArgumentException("MaxAttempts must be at least 1", nameof(options));
        _options = options;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = httpClient is null;
        _batchUrl = new Uri(options.IngestionUrl.TrimEnd('/') + "/batch");
        _flagsUrl = new Uri(options.RecommenderUrl.TrimEnd('/') + "/flags/definitions");
        _background = background;
        _flushLoop = background ? Task.Run(FlushLoopAsync) : Task.CompletedTask;
    }

    public int QueuedEvents => Volatile.Read(ref _queued);

    public long DroppedEvents => Interlocked.Read(ref _dropped);

    public bool FlagsLoaded => _flags is not null;

    public void Capture(string eventName, string distinctId, IReadOnlyDictionary<string, object?>? properties = null,
        DateTimeOffset? timestamp = null, string? eventId = null)
    {
        if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("eventName is required", nameof(eventName));
        if (string.IsNullOrEmpty(distinctId)) throw new ArgumentException("distinctId is required", nameof(distinctId));
        if (Interlocked.Increment(ref _queued) > _options.MaxQueueSize)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(new ServerEvent(eventName, distinctId, properties, timestamp ?? DateTimeOffset.UtcNow, eventId ?? NewEventId()));
        if (Volatile.Read(ref _queued) >= _options.BatchSize && _wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public void Identify(string distinctId, string? anonymousId = null, IReadOnlyDictionary<string, object?>? set = null)
    {
        var props = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(anonymousId) && anonymousId != distinctId) props["$anon_distinct_id"] = anonymousId;
        if (set is { Count: > 0 }) props["$set"] = set;
        Capture("$identify", distinctId, props);
    }

    public void SetPersonProperties(string distinctId, IReadOnlyDictionary<string, object?>? set = null,
        IReadOnlyDictionary<string, object?>? setOnce = null, IEnumerable<string>? unset = null)
    {
        var props = new Dictionary<string, object?>();
        if (set is { Count: > 0 }) props["$set"] = set;
        if (setOnce is { Count: > 0 }) props["$set_once"] = setOnce;
        if (unset?.ToList() is { Count: > 0 } names) props["$unset"] = names;
        if (props.Count > 0) Capture("$set", distinctId, props);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _flushLock.WaitAsync(ct);
        try
        {
            var failures = new List<Exception>();
            while (!_queue.IsEmpty)
            {
                var batch = new List<ServerEvent>(_options.BatchSize);
                while (batch.Count < _options.BatchSize && _queue.TryDequeue(out var e)) batch.Add(e);
                Interlocked.Add(ref _queued, -batch.Count);
                try
                {
                    await SendAsync(batch, ct);
                }
                catch (ReapDeepException ex)
                {
                    Interlocked.Add(ref _dropped, batch.Count);
                    failures.Add(ex);
                }
            }
            if (failures.Count > 0)
                throw new ReapDeepException($"{failures.Count} batch(es) could not be sent and were dropped: {failures[0].Message}",
                    (failures[0] as ReapDeepException)?.StatusCode, failures[0]);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<ServerEvent> events, CancellationToken ct = default)
    {
        var total = ImportResult.None;
        var chunk = new List<ServerEvent>(MaxImportBatch);
        foreach (var e in events)
        {
            chunk.Add(e.EventId is null ? e with { EventId = NewEventId() } : e);
            if (chunk.Count < MaxImportBatch) continue;
            total = total.Add(await SendAsync(chunk, ct));
            chunk.Clear();
        }
        if (chunk.Count > 0) total = total.Add(await SendAsync(chunk, ct));
        return total;
    }

    public async Task LoadFlagsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _flagsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServerKey);
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ReapDeepException($"Loading feature flags failed: {(int)res.StatusCode} {Short(body)}", (int)res.StatusCode);
        _flags = LocalFlag.ParseDefinitions(body).ToDictionary(f => f.Key, StringComparer.Ordinal);
        if (_background && _flagLoop is null && Interlocked.CompareExchange(ref _flagLoop, Task.CompletedTask, null) is null) _flagLoop = Task.Run(FlagLoopAsync);
    }

    public FlagValue? GetFlag(string key, string distinctId, IReadOnlyDictionary<string, object?>? personProperties = null)
    {
        if (_flags is not { } flags || !flags.TryGetValue(key, out var flag)) return null;
        var value = flag.Evaluate(distinctId, FlagProperties(personProperties));
        if (_options.SendFlagExposures) Expose(key, distinctId, value);
        return value;
    }

    public bool IsEnabled(string key, string distinctId, IReadOnlyDictionary<string, object?>? personProperties = null) =>
        GetFlag(key, distinctId, personProperties)?.Enabled == true;

    public IReadOnlyDictionary<string, FlagValue> GetAllFlags(string distinctId, IReadOnlyDictionary<string, object?>? personProperties = null)
    {
        if (_flags is not { } flags) return new Dictionary<string, FlagValue>();
        var props = FlagProperties(personProperties);
        return flags.Values.ToDictionary(f => f.Key, f => f.Evaluate(distinctId, props), StringComparer.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try
        {
            await _flushLoop;
            if (_flagLoop is { } flagLoop) await flagLoop;
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            if (_ownsHttp) _http.Dispose();
            _stop.Dispose();
        }
    }

    internal async Task<ImportResult> SendAsync(IReadOnlyList<ServerEvent> events, CancellationToken ct)
    {
        var body = EventJson.Batch(events);
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? res = null;
            Exception? error = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _batchUrl) { Content = new ByteArrayContent(body) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServerKey);
                res = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex)
            {
                error = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                error = ex;
            }

            using (res)
            {
                if (res is { IsSuccessStatusCode: true }) return ParseResult(await res.Content.ReadAsStringAsync(ct));
                if (res is not null && !IsTransient(res.StatusCode))
                    throw new ReapDeepException($"Ingestion rejected the batch: {(int)res.StatusCode} {Short(await res.Content.ReadAsStringAsync(ct))}", (int)res.StatusCode);
                if (attempt >= _options.MaxAttempts)
                    throw new ReapDeepException($"Ingestion did not accept the batch after {attempt} attempts: {(res is null ? error!.Message : ((int)res.StatusCode).ToString())}",
                        res is null ? null : (int)res.StatusCode, error);
                await Delay(RetryDelay(res, attempt), ct);
            }
        }
    }

    internal static TimeSpan RetryDelay(HttpResponseMessage? res, int attempt)
    {
        var retryAfter = res?.Headers.RetryAfter;
        var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)));
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryDelay ? MaxRetryDelay : wait;
    }

    static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    static ImportResult ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ImportResult(root.GetProperty("accepted").GetInt32(), root.GetProperty("rejected").GetInt32(), root.GetProperty("duplicates").GetInt32());
    }

    static Dictionary<string, string> FlagProperties(IReadOnlyDictionary<string, object?>? properties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is null) return result;
        foreach (var (name, value) in properties)
            if (value is not null && (value is string || value is not IEnumerable)) result[name] = EventJson.FlagText(value);
        return result;
    }

    void Expose(string key, string distinctId, FlagValue value)
    {
        var response = value.Variant ?? (value.Enabled ? "true" : "false");
        var cacheKey = key + "\n" + distinctId;
        if (_exposed.TryGetValue(cacheKey, out var previous) && previous == response) return;
        if (_exposed.Count >= MaxExposures) _exposed.Clear();
        _exposed[cacheKey] = response;
        Capture("$feature_flag_called", distinctId, new Dictionary<string, object?>
        {
            ["$feature_flag"] = key,
            ["$feature_flag_response"] = value.Variant is { } variant ? variant : value.Enabled,
        });
    }

    async Task FlushLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(_options.FlushInterval, _stop.Token);
                await FlushAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }
    }

    async Task FlagLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Delay(_options.FlagRefreshInterval, _stop.Token);
                await LoadFlagsAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }
    }

    void Report(Exception ex)
    {
        try
        {
            _options.OnError?.Invoke(ex);
        }
        catch
        {
        }
    }

    static string NewEventId() => Guid.NewGuid().ToString("N");

    static string Short(string text) => text.Length <= 300 ? text : text[..300];
}
