namespace ReapDeep.Migrations;

public static class StarRocksUsers
{
    sealed record User(string Name, string PasswordVariable, string[] Grants, string? ResourceGroup);

    static readonly User[] Users =
    [
        new("reapdeep_query", "STARROCKS_QUERY_PASSWORD", ["GRANT SELECT ON ALL TABLES IN DATABASE analytics TO USER {0}"], null),
        new("reapdeep_jobs", "STARROCKS_JOBS_PASSWORD",
            ["GRANT SELECT, DELETE ON ALL TABLES IN DATABASE analytics TO USER {0}", "GRANT INSERT ON TABLE analytics.cohort_members TO USER {0}"],
            "CREATE RESOURCE GROUP IF NOT EXISTS rg_jobs TO (user='reapdeep_jobs') WITH ('cpu_weight'='2', 'mem_limit'='0.3', 'concurrency_limit'='4')"),
        new("reapdeep_ops", "STARROCKS_OPS_PASSWORD",
            ["GRANT INSERT ON TABLE analytics.events TO USER {0}", "GRANT INSERT ON TABLE analytics.persons TO USER {0}", "GRANT INSERT ON TABLE analytics.person_distinct_ids TO USER {0}"],
            null),
        new("reapdeep_alerts", "STARROCKS_ALERTS_PASSWORD", ["GRANT SELECT ON ALL TABLES IN DATABASE analytics TO USER {0}"],
            "CREATE RESOURCE GROUP IF NOT EXISTS rg_alerts TO (user='reapdeep_alerts') WITH ('cpu_weight'='1', 'mem_limit'='0.2', 'concurrency_limit'='2')"),
    ];

    public static IEnumerable<string> Statements(Func<string, string?> env)
    {
        foreach (var u in Users)
        {
            if (env(u.PasswordVariable) is not { Length: > 0 } password) continue;
            var user = $"'{u.Name}'@'%'";
            var quoted = "'" + password.Replace("\\", "\\\\").Replace("'", "''") + "'";
            yield return $"CREATE USER IF NOT EXISTS {user} IDENTIFIED BY {quoted}";
            yield return $"ALTER USER {user} IDENTIFIED BY {quoted}";
            foreach (var grant in u.Grants) yield return string.Format(grant, user);
            if (u.ResourceGroup is not null) yield return u.ResourceGroup;
        }
    }
}
