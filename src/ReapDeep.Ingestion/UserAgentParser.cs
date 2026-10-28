namespace ReapDeep.Ingestion;

public static class UserAgentParser
{
    public readonly record struct Result(string? Browser, string? BrowserVersion, string? Os, string? OsVersion, string? DeviceType);

    public static Result Parse(string? ua)
    {
        if (string.IsNullOrEmpty(ua)) return default;

        var (browser, browserVersion) = ParseBrowser(ua);
        var (os, osVersion) = ParseOs(ua);
        return new Result(browser, browserVersion, os, osVersion, ParseDevice(ua, os));
    }

    static (string?, string?) ParseBrowser(string ua)
    {
        ReadOnlySpan<(string Token, string Name)> rules =
        [
            ("Edg/", "Microsoft Edge"), ("EdgiOS/", "Microsoft Edge"), ("EdgA/", "Microsoft Edge"),
            ("OPR/", "Opera"), ("YaBrowser/", "Yandex"), ("SamsungBrowser/", "Samsung Internet"),
            ("FxiOS/", "Firefox"), ("Firefox/", "Firefox"),
            ("CriOS/", "Chrome"), ("Chrome/", "Chrome"),
            ("Version/", "Safari"),
        ];

        foreach (var (token, name) in rules)
        {
            var i = ua.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) continue;
            if (name == "Safari" && !ua.Contains("Safari/", StringComparison.Ordinal)) continue;
            return (name, MajorMinor(ua.AsSpan(i + token.Length)));
        }
        return (null, null);
    }

    static (string?, string?) ParseOs(string ua)
    {
        if (ua.Contains("Windows NT", StringComparison.Ordinal))
        {
            var v = After(ua, "Windows NT ");
            return ("Windows", v switch { "10.0" => "10", "6.3" => "8.1", "6.2" => "8", "6.1" => "7", _ => v });
        }
        if (ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal))
            return ("iOS", After(ua, "OS ")?.Replace('_', '.'));
        if (ua.Contains("Android", StringComparison.Ordinal))
            return ("Android", After(ua, "Android "));
        if (ua.Contains("Mac OS X", StringComparison.Ordinal))
            return ("macOS", After(ua, "Mac OS X ")?.Replace('_', '.'));
        if (ua.Contains("CrOS", StringComparison.Ordinal)) return ("Chrome OS", null);
        if (ua.Contains("Linux", StringComparison.Ordinal)) return ("Linux", null);
        return (null, null);
    }

    static string ParseDevice(string ua, string? os)
    {
        if (ua.Contains("iPad", StringComparison.Ordinal) || ua.Contains("Tablet", StringComparison.Ordinal)) return "Tablet";
        if (os == "Android") return ua.Contains("Mobile", StringComparison.Ordinal) ? "Mobile" : "Tablet";
        if (os == "iOS" || ua.Contains("Mobile", StringComparison.Ordinal)) return "Mobile";
        return "Desktop";
    }

    static string? MajorMinor(ReadOnlySpan<char> s)
    {
        var end = 0;
        var dots = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.'))
        {
            if (s[end] == '.' && ++dots == 2) break;
            end++;
        }
        return end == 0 ? null : s[..end].TrimEnd('.').ToString();
    }

    static string? After(string ua, string token)
    {
        var i = ua.IndexOf(token, StringComparison.Ordinal);
        if (i < 0) return null;
        var s = ua.AsSpan(i + token.Length);
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.' || s[end] == '_')) end++;
        return end == 0 ? null : s[..end].ToString();
    }
}
