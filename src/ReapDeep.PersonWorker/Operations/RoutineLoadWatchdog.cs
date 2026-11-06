using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MySqlConnector;

namespace ReapDeep.PersonWorker.Operations;

public sealed class RoutineLoadWatchdog : BackgroundService
{
    static readonly Meter Meter = new("ReapDeep.PersonWorker");
    static readonly Counter<long> Resumes = Meter.CreateCounter<long>("reapdeep.routine_load.resumes", "{resume}", "Paused routine loads resumed automatically");

    readonly MySqlDataSource? _db;
    readonly TimeSpan _interval;
    readonly ILogger<RoutineLoadWatchdog> _log;
    readonly ConcurrentDictionary<string, RoutineLoadSnapshot> _last = new();
    readonly Dictionary<string, DateTime> _resumedAt = [];
    readonly HashSet<string> _blocked = [];

    public RoutineLoadWatchdog(IConfiguration cfg, ILogger<RoutineLoadWatchdog> log)
    {
        _log = log;
        _interval = TimeSpan.FromSeconds(Math.Max(5, cfg.GetValue("RoutineLoad:CheckSeconds", 30)));
        var connection = cfg.GetConnectionString("StarRocksOps");
        if (string.IsNullOrWhiteSpace(connection))
        {
            log.LogWarning("ConnectionStrings:StarRocksOps is not set; routine loads are not watched");
            return;
        }
        _db = new MySqlDataSource(connection);
        Meter.CreateObservableGauge("reapdeep.routine_load.running", () => _last.Values.Select(s =>
            new Measurement<int>(s.State is "RUNNING" or "NEED_SCHEDULE" ? 1 : 0, new KeyValuePair<string, object?>("load_job", s.Name))), "{job}", "1 while the routine load job runs");
        Meter.CreateObservableGauge("reapdeep.routine_load.blocked", () => _last.Keys.Select(name =>
            new Measurement<int>(_blocked.Contains(name) ? 1 : 0, new KeyValuePair<string, object?>("load_job", name))), "{job}", "1 while the job is stopped by a message it cannot load");
        Meter.CreateObservableCounter("reapdeep.routine_load.loaded_rows", () => _last.Values.Select(s =>
            new Measurement<long>(s.LoadedRows, new KeyValuePair<string, object?>("load_job", s.Name))), "{row}", "Rows loaded from Kafka into StarRocks");
        Meter.CreateObservableCounter("reapdeep.routine_load.aborted_tasks", () => _last.Values.Select(s =>
            new Measurement<long>(s.AbortedTasks, new KeyValuePair<string, object?>("load_job", s.Name))), "{task}", "Aborted load tasks");
        Meter.CreateObservableCounter("reapdeep.routine_load.error_rows", () => _last.Values.Select(s =>
            new Measurement<long>(s.ErrorRows, new KeyValuePair<string, object?>("load_job", s.Name))), "{row}", "Rows StarRocks filtered out because it could not load them");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_db is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(ct);
            }
            catch (Exception ex) when (ex is MySqlException or TimeoutException)
            {
                _log.LogWarning("Could not read the routine load state: {Message}", ex.Message);
            }
            await Task.Delay(_interval, ct);
        }
    }

    async Task CheckAsync(CancellationToken ct)
    {
        await using var conn = await _db!.OpenConnectionAsync(ct);
        var snapshots = new List<RoutineLoadSnapshot>();
        await using (var cmd = new MySqlCommand("SHOW ROUTINE LOAD FROM analytics", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            string Text(string column) => reader.GetOrdinal(column) is var i && !reader.IsDBNull(i) ? reader.GetString(i) : "";
            while (await reader.ReadAsync(ct))
            {
                var (committed, aborted, loaded, errors) = RoutineLoadRules.ParseStatistic(Text("Statistic"));
                snapshots.Add(new RoutineLoadSnapshot(Text("Name"), Text("State"), committed, aborted, loaded, errors, Text("ReasonOfStateChanged"), Text("OtherMsg"),
                    Text("ErrorLogUrls")));
            }
        }

        var now = DateTime.UtcNow;
        foreach (var current in snapshots)
        {
            var previous = _last.GetValueOrDefault(current.Name);
            if (RoutineLoadRules.NewErrorRows(previous, current) is > 0 and var filtered)
                _log.LogWarning("Routine load {Job} filtered out {Rows} rows it could not load ({Total} in total); they are not in StarRocks. Error log: {ErrorLog}",
                    current.Name, filtered, current.ErrorRows, current.ErrorLogUrls);
            var action = RoutineLoadRules.Decide(previous, current, _resumedAt.TryGetValue(current.Name, out var at) ? at : null, now);
            switch (action)
            {
                case RoutineLoadAction.Resume:
                    _log.LogWarning("Routine load {Job} is paused ({Reason}); resuming it", current.Name, current.Reason);
                    await using (var resume = new MySqlCommand($"RESUME ROUTINE LOAD FOR analytics.`{current.Name.Replace("`", "")}`", conn))
                        await resume.ExecuteNonQueryAsync(ct);
                    _resumedAt[current.Name] = now;
                    Resumes.Add(1, new KeyValuePair<string, object?>("load_job", current.Name));
                    _blocked.Remove(current.Name);
                    break;
                case RoutineLoadAction.ReportBlocked:
                    if (_blocked.Add(current.Name))
                        _log.LogError("Routine load {Job} is stopped by a message it cannot load: {Message} {Reason}. See SHOW ROUTINE LOAD and the README for how to skip it",
                            current.Name, current.OtherMessage, current.Reason);
                    break;
                default:
                    if (current.CommittedTasks != previous?.CommittedTasks && _blocked.Remove(current.Name))
                        _log.LogInformation("Routine load {Job} is loading again", current.Name);
                    break;
            }
            _last[current.Name] = current;
        }
    }

    public override void Dispose()
    {
        _db?.Dispose();
        base.Dispose();
    }
}
