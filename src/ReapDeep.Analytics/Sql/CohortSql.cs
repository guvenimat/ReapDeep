using ReapDeep.Analytics.Cohorts;

namespace ReapDeep.Analytics.Sql;

public static class CohortSql
{
    const string Pdi = "analytics.person_distinct_ids";
    const string Members = "analytics.cohort_members";

    public static SqlQuery Count(long projectId, CohortDefinition definition, DateTime nowUtc)
    {
        var b = Builder(projectId, nowUtc);
        return b.Build($"WITH members AS ({MembersCte(b, projectId, definition, nowUtc)}) SELECT COUNT(*) FROM members");
    }

    public static SqlQuery Materialize(long projectId, long cohortId, long version, CohortDefinition definition, DateTime nowUtc)
    {
        var b = Builder(projectId, nowUtc);
        var members = MembersCte(b, projectId, definition, nowUtc);
        var (project, cohort, ver) = (b.P(projectId), b.P(cohortId), b.P(version));
        var personMatch = $"pdi.project_id = {project} AND pdi.person_id = m.person AND pdi.is_deleted = 0";
        return b.Build(
            $"INSERT INTO {Members} (project_id, cohort_id, version, distinct_id) WITH members AS ({members}) " +
            $"SELECT {project}, {cohort}, {ver}, pdi.distinct_id FROM members m JOIN {Pdi} pdi ON {personMatch} " +
            $"UNION ALL SELECT {project}, {cohort}, {ver}, m.person FROM members m LEFT ANTI JOIN {Pdi} pdi ON {personMatch}");
    }

    public static SqlQuery InsertStatic(long projectId, long cohortId, long version, IReadOnlyList<string> distinctIds)
    {
        Ensure.That(distinctIds.Count > 0, "No distinct_ids to insert");
        var b = Builder(projectId, DateTime.UtcNow);
        var (project, cohort, ver) = (b.P(projectId), b.P(cohortId), b.P(version));
        var rows = distinctIds.Select(d => $"({project}, {cohort}, {ver}, {b.P(d)})");
        return b.Build($"INSERT INTO {Members} (project_id, cohort_id, version, distinct_id) VALUES {string.Join(", ", rows)}");
    }

    public static SqlQuery ExpandStatic(long projectId, long cohortId, long version)
    {
        var b = Builder(projectId, DateTime.UtcNow);
        var (project, cohort, ver) = (b.P(projectId), b.P(cohortId), b.P(version));
        return b.Build(
            $"INSERT INTO {Members} (project_id, cohort_id, version, distinct_id) " +
            $"SELECT {project}, {cohort}, {ver}, other.distinct_id FROM {Members} cm " +
            $"JOIN {Pdi} pdi ON cm.project_id = pdi.project_id AND cm.distinct_id = pdi.distinct_id AND pdi.is_deleted = 0 " +
            $"JOIN {Pdi} other ON other.project_id = {project} AND other.person_id = pdi.person_id AND other.is_deleted = 0 " +
            $"WHERE cm.project_id = {project} AND cm.cohort_id = {cohort} AND cm.version = {ver}");
    }

    public static SqlQuery PersonCount(long projectId, long cohortId, long version)
    {
        var b = Builder(projectId, DateTime.UtcNow);
        return b.Build($"SELECT COUNT(DISTINCT {PersonOfMember}) FROM {MemberRows(b, projectId, cohortId, version)}");
    }

    public static SqlQuery PersonsPage(long projectId, long cohortId, long version, string? after, int limit)
    {
        var b = Builder(projectId, DateTime.UtcNow);
        var from = MemberRows(b, projectId, cohortId, version);
        var cursor = after is null ? "" : $" AND {PersonOfMember} > {b.P(after)}";
        return b.Build($"SELECT DISTINCT {PersonOfMember} AS person FROM {from}{cursor} ORDER BY person LIMIT {limit}");
    }

    public static SqlQuery DeleteVersionsUpTo(long projectId, long cohortId, long version)
    {
        var b = Builder(projectId, DateTime.UtcNow);
        return b.Build($"DELETE FROM {Members} WHERE project_id = {b.P(projectId)} AND cohort_id = {b.P(cohortId)} AND version <= {b.P(version)}");
    }

    public static SqlQuery DeleteVersion(long projectId, long cohortId, long version)
    {
        var b = Builder(projectId, DateTime.UtcNow);
        return b.Build($"DELETE FROM {Members} WHERE project_id = {b.P(projectId)} AND cohort_id = {b.P(cohortId)} AND version = {b.P(version)}");
    }

    public static SqlQuery DeleteCohort(long projectId, long cohortId)
    {
        var b = Builder(projectId, DateTime.UtcNow);
        return b.Build($"DELETE FROM {Members} WHERE project_id = {b.P(projectId)} AND cohort_id = {b.P(cohortId)}");
    }

    public static SqlQuery DeleteDistinctIds(long projectId, IReadOnlyList<string> distinctIds)
    {
        var b = Builder(projectId, DateTime.UtcNow);
        return b.Build($"DELETE FROM {Members} WHERE project_id = {b.P(projectId)} AND distinct_id IN ({string.Join(", ", distinctIds.Select(d => b.P(d)))})");
    }

    const string PersonOfMember = "COALESCE(pdi.person_id, cm.distinct_id)";

    static string MemberRows(SqlBuilder b, long projectId, long cohortId, long version) =>
        $"{Members} cm LEFT JOIN {Pdi} pdi ON cm.project_id = pdi.project_id AND cm.distinct_id = pdi.distinct_id AND pdi.is_deleted = 0 " +
        $"WHERE cm.project_id = {b.P(projectId)} AND cm.cohort_id = {b.P(cohortId)} AND cm.version = {b.P(version)}";

    static SqlBuilder Builder(long projectId, DateTime nowUtc) => new(projectId, nowUtc.AddDays(-CohortRules.MaxWithinDays), nowUtc);

    static string MembersCte(SqlBuilder b, long projectId, CohortDefinition definition, DateTime nowUtc)
    {
        var normalized = CohortRules.Normalize(definition);
        var project = b.P(projectId);
        var now = b.P(nowUtc);
        return string.Join(" UNION ", normalized.Groups!.Select(g => Group(b, project, now, nowUtc, g.Conditions!)));
    }

    static string Group(SqlBuilder b, string project, string now, DateTime nowUtc, List<CohortCondition> conditions)
    {
        var props = conditions.Where(c => c.Type == ConditionTypes.PersonProperty)
            .Select(c => b.Condition(new Filter("person", c.Property!, c.Op!, c.Value)))
            .ToList();
        var behavioral = conditions.Where(c => c.Type is ConditionTypes.Performed or ConditionTypes.NotPerformed).ToList();
        var rfm = conditions.Where(c => c.Type == ConditionTypes.RfmSegment).ToList();
        const string alivePerson = "p.is_deleted = 0";

        if (rfm.Count > 0)
            return RfmGroup(b, project, now, nowUtc, rfm, behavioral, props);

        if (behavioral.Count == 0)
            return $"SELECT p.person_id AS person FROM analytics.persons p WHERE p.project_id = {project} AND {alivePerson} AND {string.Join(" AND ", props)}";

        var perPerson = PerPerson(b, project, now, nowUtc, behavioral);
        if (behavioral.Any(c => c.Type == ConditionTypes.Performed))
        {
            var where = behavioral.Select((c, k) => CountCondition(b, c, $"x.c{k}")).Concat(props);
            var persons = props.Count == 0 ? "" : $" JOIN analytics.persons p ON p.project_id = {project} AND p.person_id = x.person AND {alivePerson}";
            return $"SELECT x.person FROM ({perPerson}) x{persons} WHERE {string.Join(" AND ", where)}";
        }

        var absent = behavioral.Select((c, k) => CountCondition(b, c, $"COALESCE(x.c{k}, 0)"));
        return $"SELECT p.person_id AS person FROM analytics.persons p LEFT JOIN ({perPerson}) x ON x.person = p.person_id " +
               $"WHERE p.project_id = {project} AND {alivePerson} AND {string.Join(" AND ", props.Concat(absent))}";
    }

    static string RfmGroup(SqlBuilder b, string project, string now, DateTime nowUtc, List<CohortCondition> rfm,
        List<CohortCondition> behavioral, List<string> props)
    {
        var population = rfm.Select(c => RfmSql.Members(b, project, c.Event!, c.WithinDays!.Value, c.Segments!, nowUtc))
            .Aggregate((acc, next) => $"SELECT g.person FROM ({acc}) g LEFT SEMI JOIN ({next}) n ON n.person = g.person");
        if (behavioral.Count == 0 && props.Count == 0) return population;

        var counts = behavioral.Count == 0 ? "" : $" LEFT JOIN ({PerPerson(b, project, now, nowUtc, behavioral)}) x ON x.person = r.person";
        var persons = props.Count == 0 ? "" : $" JOIN analytics.persons p ON p.project_id = {project} AND p.person_id = r.person AND p.is_deleted = 0";
        var where = behavioral.Select((c, k) => CountCondition(b, c, $"COALESCE(x.c{k}, 0)")).Concat(props);
        return $"SELECT r.person FROM ({population}) r{counts}{persons} WHERE {string.Join(" AND ", where)}";
    }

    static string PerPerson(SqlBuilder b, string project, string now, DateTime nowUtc, List<CohortCondition> behavioral)
    {
        var counts = behavioral.Select((c, k) =>
        {
            var parts = new List<string> { $"e.event = {b.P(c.Event!)}", $"e.timestamp >= {b.P(nowUtc.AddDays(-c.WithinDays!.Value))}" };
            parts.AddRange((c.Filters ?? []).Select(b.Condition));
            return $"SUM(IF({string.Join(" AND ", parts)}, 1, 0)) AS c{k}";
        });
        var since = b.P(nowUtc.AddDays(-behavioral.Max(c => c.WithinDays!.Value)));
        var events = string.Join(", ", behavioral.Select(c => c.Event!).Distinct().Select(e => b.P(e)));
        var agg = $"SELECT e.project_id, e.distinct_id, {string.Join(", ", counts)} FROM analytics.events e " +
                  $"WHERE e.project_id = {project} AND e.timestamp >= {since} AND e.timestamp < {now} AND e.event IN ({events}) GROUP BY 1, 2";
        var sums = string.Join(", ", behavioral.Select((_, k) => $"SUM(a.c{k}) AS c{k}"));
        return $"SELECT COALESCE(pdi.person_id, a.distinct_id) AS person, {sums} FROM ({agg}) a " +
               $"LEFT JOIN {Pdi} pdi ON a.project_id = pdi.project_id AND a.distinct_id = pdi.distinct_id AND pdi.is_deleted = 0 GROUP BY 1";
    }

    static string CountCondition(SqlBuilder b, CohortCondition c, string column) => c.Type == ConditionTypes.NotPerformed
        ? $"{column} = 0"
        : c.CountOp switch
        {
            "eq" => $"{column} = {b.P(c.Count!.Value)}",
            "lte" => $"{column} BETWEEN 1 AND {b.P(c.Count!.Value)}",
            _ => $"{column} >= {b.P(c.Count!.Value)}",
        };
}
