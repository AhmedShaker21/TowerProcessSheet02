using System.Security.Claims;
using System.Security.Cryptography;

namespace TowerProcessSheet.Services;

/// <summary>PBKDF2 (SHA-256) password hashing, no external dependencies.</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16, KeySize = 32, Iterations = 100_000;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int iters)) return false;
        byte[] salt = Convert.FromBase64String(parts[1]);
        byte[] key = Convert.FromBase64String(parts[2]);
        byte[] test = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, key.Length);
        return CryptographicOperations.FixedTimeEquals(test, key);
    }
}

public static class ClaimsExtensions
{
    public static int GetUserId(this ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;

    public static bool IsAdmin(this ClaimsPrincipal user)
        => user.IsInRole("Admin");
}
