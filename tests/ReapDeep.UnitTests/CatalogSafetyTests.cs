using ReapDeep.QueryApi.Catalog;

namespace ReapDeep.UnitTests;

public class CatalogSafetyTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("https://shop.example/products/1", true)]
    [InlineData("http://shop.example/p?id=1", true)]
    [InlineData("/products/linen-shirt", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData(@"/\evil.example", false)]
    [InlineData("ftp://shop.example/file", false)]
    public void Item_urls_are_http_addresses_or_site_paths(string url, bool safe) => Assert.Equal(safe, CatalogService.IsSafeUrl(url));
}
