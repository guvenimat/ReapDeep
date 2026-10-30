using System.Diagnostics;
using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Cohorts;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;
using ReapDeep.QueryApi.Persons;

namespace ReapDeep.QueryApi.Cohorts;

public sealed class CohortService(CohortRepository cohorts, PersonRepository persons, StarRocksClient starRocks)
{
    public const int MaxBreakdownCohorts = 5;
    public const int MaxStaticMembers = 100_000;
    static readonly JsonElement EmptyObject = JsonText.Parse("{}");

    public async Task<CohortPreviewResponse> PreviewAsync(ProjectContext project, CohortDefinition? definition, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var query = CohortSql.Count(project.ProjectId, CohortRules.Normalize(definition), DateTime.UtcNow);
        var count = (await starRocks.QueryAsync(query, r => Convert.ToInt64(r.GetValue(0)), ct)).Single();
        return new CohortPreviewResponse(count, sw.Elapsed.TotalMilliseconds);
    }

    public async Task<CohortPersonsResponse?> PersonsAsync(ProjectContext project, long id, string? after, int limit, CancellationToken ct)
    {
        if (await cohorts.GetAsync(project.ProjectId, id, ct) is not { } cohort) return null;
        if (cohort.ActiveVersion is not { } version) throw new CohortNotReadyException(id, cohort.Row.Name, cohort.Row.Status);

        var keys = await starRocks.QueryAsync(CohortSql.PersonsPage(project.ProjectId, id, version, after, limit), r => r.GetString(0), ct);
        return new CohortPersonsResponse(await DescribeAsync(project, keys, ct), keys.Count == limit ? keys[^1] : null);
    }

    public async Task<List<CohortPerson>> DescribeAsync(ProjectContext project, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var identified = keys.Select(k => Guid.TryParse(k, out var g) ? g : (Guid?)null).OfType<Guid>().ToArray();
        var known = (await persons.GetManyAsync(project.ProjectId, identified, ct)).ToDictionary(p => p.Id);
        return keys.Select(k => known.TryGetValue(k, out var p)
            ? new CohortPerson(p.Id, p.IsIdentified, p.DistinctIds, p.Properties)
            : new CohortPerson(k, false, [k], EmptyObject)).ToList();
    }

    public async Task<IReadOnlyDictionary<long, CohortRef>> ResolveAsync(ProjectContext project, IEnumerable<Filter>? filters, Breakdown? breakdown,
        CancellationToken ct)
    {
        var ids = (filters ?? []).Where(f => f.Scope == "cohort").Select(SqlBuilder.CohortId).ToList();
        if (breakdown?.Scope == "cohort")
        {
            var breakdownIds = breakdown.CohortIds ?? [];
            Ensure.That(breakdownIds.Count is > 0 and <= MaxBreakdownCohorts, $"A cohort breakdown needs 1-{MaxBreakdownCohorts} cohort_ids");
            ids.AddRange(breakdownIds);
        }
        if (ids.Count == 0) return new Dictionary<long, CohortRef>();

        var found = (await cohorts.ReferencesAsync(project.ProjectId, ids.Distinct().ToArray(), ct)).ToDictionary(c => c.Id);
        foreach (var id in ids)
            if (!found.ContainsKey(id)) throw new CohortNotFoundException(id);
        return found;
    }

    public async Task<List<string>> StaticMembersAsync(ProjectContext project, List<string>? members, CancellationToken ct)
    {
        var ids = (members ?? []).Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        Ensure.That(ids.Length is > 0 and <= MaxStaticMembers, $"A static cohort needs 1-{MaxStaticMembers} members");
        Ensure.That(ids.All(m => m.Length <= 200), "Each member id can be at most 200 characters");
        return await cohorts.ResolveDistinctIdsAsync(project.ProjectId, ids, ct);
    }
}
