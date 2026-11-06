using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReapDeep.Ingestion.Capture;
using ReapDeep.Migrations;

namespace ReapDeep.UnitTests;

public partial class ContractTests
{
    static readonly string Root = FindRoot();
    static readonly IReadOnlyList<Migration> Migrations = MigrationCatalog.Load(typeof(MigrationCatalog).Assembly);

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReapDeep.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    static string Source(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path]));

    static (string[] Columns, string[] Paths) RoutineLoad(string name)
    {
        var sql = string.Join("\n", Migrations.Where(m => m.Target == MigrationCatalog.StarRocks).Select(m => m.Sql));
        var statement = MigrationCatalog.Statements(sql).Single(s => s.StartsWith($"CREATE ROUTINE LOAD analytics.{name} ", StringComparison.Ordinal));
        var columns = ColumnsPattern().Match(statement).Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var paths = JsonPathPattern().Matches(statement).Select(m => m.Groups[1].Value).ToArray();
        return (columns, paths);
    }

    static string[] TableColumns(string table)
    {
        var sql = string.Join("\n", Migrations.Where(m => m.Target == MigrationCatalog.StarRocks).Select(m => m.Sql));
        var create = MigrationCatalog.Statements(sql).Single(s => s.StartsWith($"CREATE TABLE IF NOT EXISTS {table} (", StringComparison.Ordinal));
        return [.. create.Split('\n').Select(l => TableColumnPattern().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value)];
    }

    static HashSet<string> OutboxKeys(string source, string topic)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in Regex.Matches(source, $@"'{topic}',[^\n]*jsonb_build_object\((?<body>[\s\S]*?)\)\s*\n\s*FROM"))
            foreach (Match key in OutboxKeyPattern().Matches(block.Groups["body"].Value)) keys.Add(key.Groups[1].Value);
        return keys;
    }

    [Fact]
    public void Every_event_column_StarRocks_loads_is_written_by_ingestion()
    {
        var (columns, paths) = RoutineLoad("events_from_kafka");
        Assert.Equal(columns.Length, paths.Length);
        Assert.Equal(columns, paths);
        Assert.Equal(TableColumns("events").Order(), columns.Order());

        var props = new Dictionary<string, JsonElement> { ["$current_url"] = JsonSerializer.SerializeToElement("https://example.com/a") };
        var message = EventMessage.Build(1, "e", "d", "id", DateTime.UnixEpoch, DateTime.UnixEpoch, "127.0.0.1", default, props);
        using var doc = JsonDocument.Parse(message);
        var written = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(paths.Order(), written.Order());
    }

    [Fact]
    public void Ingestion_never_writes_more_bytes_than_a_StarRocks_column_holds()
    {
        var sql = string.Join('\n', Migrations.Where(m => m.Target == MigrationCatalog.StarRocks).Select(m => m.Sql));
        var create = MigrationCatalog.Statements(sql).Single(s => s.StartsWith("CREATE TABLE IF NOT EXISTS events (", StringComparison.Ordinal));
        var sizes = VarcharPattern().Matches(create).ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(EventMessage.MaxIdBytes, sizes["event"]);
        Assert.Equal(EventMessage.MaxIdBytes, sizes["distinct_id"]);
        Assert.Equal(CaptureEndpoint.MaxEventIdBytes, sizes["event_id"]);
        Assert.All(EventMessage.TextColumns, c => Assert.True(c.MaxBytes <= sizes[c.Column], $"{c.Column}: ingestion writes up to {c.MaxBytes} bytes, StarRocks holds {sizes[c.Column]}"));
    }

    [Theory]
    [InlineData("persons_from_kafka", "persons", "person_updates")]
    [InlineData("person_distinct_ids_from_kafka", "person_distinct_ids", "person_distinct_id_updates")]
    public void Person_outbox_payloads_match_what_StarRocks_loads(string job, string table, string topic)
    {
        var (columns, paths) = RoutineLoad(job);
        Assert.Equal(columns, paths);
        Assert.Subset(TableColumns(table).ToHashSet(), columns.ToHashSet());
        foreach (var file in new[] { Source("src", "ReapDeep.PersonWorker", "Identity", "PersonTransaction.cs"), Source("src", "ReapDeep.PersonWorker", "Deletion", "DeletionBatch.cs"),
            Source("src", "ReapDeep.PersonWorker", "Identity", "PersonResync.cs") })
        {
            var keys = OutboxKeys(file, topic);
            Assert.NotEmpty(keys);
            Assert.Equal(paths.Order(), keys.Order());
        }
    }

    [Fact]
    public void The_SDK_sends_only_fields_the_ingestion_API_reads()
    {
        static HashSet<string> JsonNames(Type t) =>
            [.. t.GetProperties().Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)];
        var transport = Source("sdk", "js", "src", "transport.ts");
        var queued = Regex.Match(transport, @"interface QueuedEvent \{(?<body>[^}]*)\}").Groups["body"].Value;
        var eventFields = TsFields(queued);
        var bodyFields = Regex.Match(transport, @"JSON\.stringify\(\{(?<body>[^}]*)\}\)").Groups["body"].Value
            .Split(',').Select(f => f.Split(':')[0].Trim()).ToHashSet();
        Assert.NotEmpty(eventFields);
        Assert.Subset(JsonNames(typeof(CaptureEvent)), eventFields);
        Assert.Subset(JsonNames(typeof(CaptureRequest)), bodyFields);
    }

    [Fact]
    public void Dashboard_types_only_use_fields_the_Query_API_has()
    {
        var types = Source("dashboard", "src", "api", "types.ts");
        var api = typeof(ReapDeep.QueryApi.Infrastructure.StarRocksErrors).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace?.StartsWith("ReapDeep.QueryApi", StringComparison.Ordinal) == true)
            .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var checkedTypes = 0;
        var problems = new List<string>();
        foreach (Match m in InterfacePattern().Matches(types))
        {
            var name = m.Groups["name"].Value;
            if (!api.TryGetValue(name, out var type)) continue;
            checkedTypes++;
            var properties = AllProperties(type).Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var field in TsFields(Body(types, m.Index + m.Length)))
                if (!properties.Contains(field)) problems.Add($"{name}.{field}");
        }
        Assert.True(checkedTypes >= 15, $"only {checkedTypes} dashboard types matched a Query API contract");
        Assert.Empty(problems);
    }

    static IEnumerable<PropertyInfo> AllProperties(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

    static string Body(string source, int openBrace)
    {
        var depth = 1;
        var i = openBrace;
        for (; i < source.Length && depth > 0; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}') depth--;
        }
        return source[openBrace..(i - 1)];
    }

    static HashSet<string> TsFields(string body)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= body.Length; i++)
        {
            var c = i < body.Length ? body[i] : ';';
            if (c is '{' or '(' or '[' or '<') depth++;
            else if (c is '}' or ')' or ']' or '>') depth--;
            else if (depth == 0 && c is ';' or '\n')
            {
                if (FieldPattern().Match(body[start..i]) is { Success: true } f) fields.Add(f.Groups[1].Value);
                start = i + 1;
            }
        }
        return fields;
    }

    [GeneratedRegex(@"^\s{4}([a-z_]+)\s+VARCHAR\((\d+)\)", RegexOptions.Multiline)]
    private static partial Regex VarcharPattern();

    [GeneratedRegex(@"COLUMNS \(([^)]*)\)")]
    private static partial Regex ColumnsPattern();

    [GeneratedRegex(@"\\""\$\.([a-z_]+)\\""")]
    private static partial Regex JsonPathPattern();

    [GeneratedRegex(@"^\s{4}([a-z_]+)\s+[A-Z]+")]
    private static partial Regex TableColumnPattern();

    [GeneratedRegex(@"'([a-z_]+)',\s*[^,']")]
    private static partial Regex OutboxKeyPattern();

    [GeneratedRegex(@"export interface (?<name>[A-Za-z]+)(?:<[^>]*>)?(?: extends [^{]+)? \{")]
    private static partial Regex InterfacePattern();

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\??\s*:")]
    private static partial Regex FieldPattern();
}
