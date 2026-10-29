using System.Diagnostics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;
using ReapDeep.QueryApi.Persons;

namespace ReapDeep.QueryApi.Analytics;

public sealed class EventService(StarRocksClient starRocks, PersonRepository persons)
{
    const int MaxPersonDistinctIds = 1000;
    const int FirstColumn = 6;

    public async Task<EventsResponse> ListAsync(ProjectContext project, EventsQuery query, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var range = TimeRange.Parse(query.DateFrom, query.DateTo, project.TimeZone, TimeSpan.FromDays(7));
        var (distinctIds, personScan) = await ResolvePerson(project.ProjectId, query.PersonId, ct);

        var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
        var sql = EventSql.Build(b, query.Event, query.DistinctId, distinctIds, personScan, Math.Clamp(query.Limit ?? 50, 1, 200));
        var events = await starRocks.QueryAsync(sql, r =>
        {
            var columns = new Dictionary<string, string>();
            for (var i = 0; i < ColumnMapper.EventColumnList.Length; i++)
                if (!r.IsDBNull(FirstColumn + i)) columns[ColumnMapper.EventColumnList[i]] = r.GetString(FirstColumn + i);
            return new EventRow(
                r.GetString(0), r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetString(3), r.GetString(4),
                columns, r.IsDBNull(5) ? null : JsonText.Parse(r.GetString(5)));
        }, ct);

        return new EventsResponse(events, sw.Elapsed.TotalMilliseconds);
    }

    async Task<(IReadOnlyList<string>? DistinctIds, string? PersonScan)> ResolvePerson(long projectId, string? personId, CancellationToken ct)
    {
        if (personId is null) return (null, null);
        var ids = Guid.TryParse(personId, out var id) ? await persons.DistinctIdsAsync(projectId, id, MaxPersonDistinctIds + 1, ct) : [];
        return ids.Count switch
        {
            0 => ([personId], null),
            <= MaxPersonDistinctIds => (ids, null),
            _ => (null, personId),
        };
    }
}
