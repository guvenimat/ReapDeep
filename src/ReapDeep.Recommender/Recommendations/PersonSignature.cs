using System.Security.Cryptography;
using System.Text;

namespace ReapDeep.Recommender.Recommendations;

public enum SignatureCheck { Valid, Invalid, Expired, TooLong }

public static class PersonSignature
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    public static string Sign(string secret, string distinctId, long expires) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{distinctId}.{expires}")));

    public static SignatureCheck Verify(string secret, string distinctId, long expires, string signature, DateTime nowUtc)
    {
        var expected = Encoding.ASCII.GetBytes(Sign(secret, distinctId, expires));
        var given = Encoding.ASCII.GetBytes(signature.ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return SignatureCheck.Invalid;
        var now = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        if (expires <= now) return SignatureCheck.Expired;
        if (expires > now + (long)MaxLifetime.TotalSeconds) return SignatureCheck.TooLong;
        return SignatureCheck.Valid;
    }
}
