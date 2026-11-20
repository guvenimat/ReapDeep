using System.Net;
using ReapDeep.Recommender.Infrastructure;

namespace ReapDeep.UnitTests;

public class RecommenderRateLimitTests
{
    static readonly IReadOnlyList<IPNetwork> TenNetwork = [IPNetwork.Parse("10.0.0.0/8")];

    [Fact]
    public void Address_inside_an_exempt_network_is_exempt() =>
        Assert.True(RateLimitRules.IsExempt(IPAddress.Parse("10.0.0.5"), TenNetwork));

    [Fact]
    public void Address_outside_the_exempt_network_is_not_exempt() =>
        Assert.False(RateLimitRules.IsExempt(IPAddress.Parse("11.0.0.5"), TenNetwork));

    [Fact]
    public void IPv4_mapped_addresses_are_checked_against_the_ipv4_network() =>
        Assert.True(RateLimitRules.IsExempt(IPAddress.Parse("::ffff:10.0.0.5"), TenNetwork));

    [Fact]
    public void Null_address_is_not_exempt() =>
        Assert.False(RateLimitRules.IsExempt(null, TenNetwork));

    [Fact]
    public void Empty_or_blank_csv_yields_no_networks()
    {
        Assert.Empty(RateLimitRules.Networks(null));
        Assert.Empty(RateLimitRules.Networks(""));
        Assert.Empty(RateLimitRules.Networks("  ,  "));
    }

    [Fact]
    public void Csv_with_two_networks_and_spaces_yields_both()
    {
        var networks = RateLimitRules.Networks("10.0.0.0/8, 192.168.1.0/24");
        Assert.Equal(2, networks.Count);
        Assert.True(networks[1].Contains(IPAddress.Parse("192.168.1.7")));
    }
}
