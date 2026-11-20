using System.Net;

namespace ReapDeep.Recommender.Infrastructure;

public static class RateLimitRules
{
    public static IReadOnlyList<IPNetwork> Networks(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(IPNetwork.Parse)
            .ToList();

    public static bool IsExempt(IPAddress? address, IReadOnlyList<IPNetwork> exempt) =>
        address is not null && exempt.Any(n => n.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
}
