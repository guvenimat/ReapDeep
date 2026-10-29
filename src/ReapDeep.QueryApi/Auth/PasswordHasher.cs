using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace ReapDeep.QueryApi.Auth;

public static class PasswordHasher
{
    public const int MinLength = 8;
    public const int MaxLength = 1024;
    public const string LengthRule = "Password must be 8-1024 characters";

    const string Scheme = "pbkdf2-sha256";
    const int Iterations = 600_000;
    const int SaltBytes = 16;
    const int HashBytes = 32;

    static readonly Lazy<string> Dummy = new(() => Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    public static bool IsValidLength([NotNullWhen(true)] string? password) => password is { Length: >= MinLength and <= MaxLength };

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        var valid = parts is { Length: 4 } && parts[0] == Scheme && int.TryParse(parts[1], out var n) && n > 0;
        if (!valid) parts = Dummy.Value.Split('$');
        var expected = Convert.FromBase64String(parts![3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(parts[2]),
            int.Parse(parts[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && valid;
    }
}
