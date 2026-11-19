using ReapDeep.QueryApi.Auth;

namespace ReapDeep.UnitTests;

public class AuthTokenTests
{
    [Theory]
    [InlineData(" Ada@Example.com ", "ada@example.com")]
    [InlineData("İSTANBUL@Example.com", "istanbul@example.com")]
    [InlineData("ırmak@Example.com", "irmak@example.com")]
    public void Email_normalization_folds_turkish_letters(string input, string expected) =>
        Assert.Equal(expected, SessionService.NormalizeEmail(input));

    [Fact]
    public void New_tokens_are_random_url_safe_and_well_formed()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => AuthTokens.NewToken()).ToList();
        Assert.Equal(50, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.Equal(43, t.Length);
            Assert.True(AuthTokens.IsWellFormed(t));
            Assert.DoesNotContain('=', t);
        });
    }

    [Fact]
    public void Only_the_sha256_of_a_token_is_stored()
    {
        var token = AuthTokens.NewToken();
        var hash = AuthTokens.Hash(token);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, AuthTokens.Hash(token));
        Assert.NotEqual(hash, AuthTokens.Hash(AuthTokens.NewToken()));
        Assert.DoesNotContain(token, hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345'; DROP")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789 with space")]
    public void Malformed_tokens_are_rejected_before_any_lookup(string? token) => Assert.False(AuthTokens.IsWellFormed(token));

    [Fact]
    public void Overlong_tokens_are_rejected() => Assert.False(AuthTokens.IsWellFormed(new string('a', AuthTokens.MaxLength + 1)));

    [Theory]
    [InlineData("http://localhost:3000", AuthTokens.Invite, "http://localhost:3000/#/invite?token=abc-_1")]
    [InlineData("https://app.example.com/", AuthTokens.Reset, "https://app.example.com/#/reset?token=abc-_1")]
    public void Links_open_the_dashboard_route_for_the_purpose(string publicUrl, string purpose, string expected) =>
        Assert.Equal(expected, AuthTokens.Link(publicUrl, purpose, "abc-_1"));

    [Fact]
    public void Invitations_last_a_week_and_resets_an_hour()
    {
        Assert.Equal(TimeSpan.FromDays(7), AuthTokens.Lifetime(AuthTokens.Invite));
        Assert.Equal(TimeSpan.FromHours(1), AuthTokens.Lifetime(AuthTokens.Reset));
    }

    [Fact]
    public void Emails_carry_the_link_and_its_expiry()
    {
        var invite = AuthTokens.InviteEmail("Ada", "Demo Org", "http://localhost:3000/#/invite?token=t");
        Assert.Equal("You are invited to Demo Org on ReapDeep", invite.Subject);
        Assert.Contains("Ada invited you to Demo Org", invite.Text);
        Assert.Contains("http://localhost:3000/#/invite?token=t", invite.Text);
        Assert.Contains("expires in 7 days", invite.Text);

        var reset = AuthTokens.ResetEmail("http://localhost:3000/#/reset?token=t");
        Assert.Contains("http://localhost:3000/#/reset?token=t", reset.Text);
        Assert.Contains("expires in 60 minutes", reset.Text);
    }
}
