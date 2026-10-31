using System.Reflection;
using System.Text;

namespace ReapDeep.Migrations;

public sealed record Migration(string Target, string Version, string Name, string Sql);

public static class MigrationCatalog
{
    public const string Postgres = "postgres";
    public const string StarRocks = "starrocks";

    public static IReadOnlyList<Migration> Load(Assembly assembly) =>
        [.. assembly.GetManifestResourceNames()
            .Select(name => Parse(name, Read(assembly, name)))
            .OfType<Migration>()
            .OrderBy(m => m.Target, StringComparer.Ordinal)
            .ThenBy(m => m.Version, StringComparer.Ordinal)];

    public static Migration? Parse(string resource, string sql)
    {
        var slash = resource.IndexOf('/');
        if (slash < 0 || !resource.EndsWith(".sql", StringComparison.Ordinal)) return null;
        var target = resource[..slash] switch { "Postgres" => Postgres, "StarRocks" => StarRocks, _ => null };
        var file = resource[(slash + 1)..^4];
        var underscore = file.IndexOf('_');
        if (target is null || underscore != 4 || !file[..4].All(char.IsAsciiDigit)) return null;
        return new Migration(target, file[..4], file[5..], sql);
    }

    public static IReadOnlyList<string> Statements(string sql)
    {
        var withoutComments = string.Join('\n', sql.Replace("\r\n", "\n").Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        var statements = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var escaped = false;
        foreach (var c in withoutComments)
        {
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"' or '`')
            {
                quote = c;
            }
            else if (c == ';')
            {
                if (current.ToString().Trim() is { Length: > 0 } statement) statements.Add(statement);
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.ToString().Trim() is { Length: > 0 } last) statements.Add(last);
        return statements;
    }

    public static IReadOnlyList<Migration> Pending(IEnumerable<Migration> all, IReadOnlySet<string> applied, string target) =>
        [.. all.Where(m => m.Target == target && !applied.Contains(m.Version))];

    static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
