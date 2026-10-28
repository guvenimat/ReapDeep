using System.Globalization;
using System.Text;

namespace ReapDeep.LoadTest;

sealed class Options
{
    readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (key is not null) o._values[key] = null;
                key = a[2..];
            }
            else if (key is not null)
            {
                o._values[key] = a;
                key = null;
            }
        }
        if (key is not null) o._values[key] = null;
        return o;
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required");
    public int Int(string name, int fallback) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public long Long(string name, long fallback) => Get(name) is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : fallback;
}

static class Stats
{
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    public static string Ms(double v) => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
    public static string N(double v) => v.ToString("#,0", CultureInfo.InvariantCulture);
    public static string F(double v, string format = "0.0") => v.ToString(format, CultureInfo.InvariantCulture);
}

static class Report
{
    public static string? Path { get; set; }

    public static void Heading(string text) => Write($"\n### {text}\n");

    public static void Line(string text) => Write(text);

    public static void Table(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append("| ").AppendJoin(" | ", headers).AppendLine(" |");
        sb.Append('|').AppendJoin("|", headers.Select(_ => "---")).AppendLine("|");
        foreach (var row in rows) sb.Append("| ").AppendJoin(" | ", row).AppendLine(" |");
        Write(sb.ToString());
    }

    static void Write(string text)
    {
        Console.WriteLine(text);
        if (Path is not null) File.AppendAllText(Path, text + Environment.NewLine, new UTF8Encoding(false));
    }
}

static class Rng
{
    public static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
