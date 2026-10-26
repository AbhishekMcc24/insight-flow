using System.Security.Cryptography;

namespace InsightFlow.Api.Admin;

/// <summary>
/// Salted PBKDF2-SHA256 password hashing from the BCL. The stored value is self-describing
/// (<c>v1.iterations.salt.hash</c>) so the cost can be raised later without invalidating existing hashes.
/// Passwords are never logged or returned from any endpoint.
/// </summary>
public static class PasswordHasher
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        Validate(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts is not ["v1", var iterationText, var saltText, var hashText] || !int.TryParse(iterationText, out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(saltText), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Throws a 400 problem when the password does not meet the policy.</summary>
    public static void Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength || password.Length > MaxLength)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_password", $"A password must be {MinLength}–{MaxLength} characters.");
        }
    }
}
