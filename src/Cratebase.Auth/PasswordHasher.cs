using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cratebase.Auth;

/// <summary>
/// Password hashing: PBKDF2-HMAC-SHA512.
/// </summary>
/// <remarks>
/// <para>
/// Digest format, base64-encoded: <c>[version:1][iterations:4][salt:16][key:32]</c>. The version
/// and iteration count are <b>stored with the digest</b>, not read from configuration: without
/// that, raising the cost tomorrow would make every password hashed yesterday unverifiable.
/// </para>
/// <para>
/// The comparison runs in constant time. A naive comparison leaks the length of the correct
/// prefix, which is enough to reconstruct a digest byte by byte.
/// </para>
/// </remarks>
public static class PasswordHasher
{
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>Iteration count applied to new digests.</summary>
    public const int Iterations = 210_000;

    /// <summary>Minimum required password length.</summary>
    public const int MinimumLength = 10;

    /// <summary>Produces a password's digest.</summary>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeySize);

        var payload = new byte[1 + 4 + SaltSize + KeySize];
        payload[0] = Version;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1, 4), Iterations);
        salt.CopyTo(payload.AsSpan(5, SaltSize));
        key.CopyTo(payload.AsSpan(5 + SaltSize, KeySize));

        return Convert.ToBase64String(payload);
    }

    /// <summary>Verifies a password against a digest.</summary>
    public static bool Verify(string? password, string? hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        byte[] payload;

        try
        {
            payload = Convert.FromBase64String(hash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.Length != 1 + 4 + SaltSize + KeySize || payload[0] != Version)
        {
            return false;
        }

        var iterations = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1, 4));

        if (iterations is < 1 or > 5_000_000)
        {
            return false;
        }

        var salt = payload.AsSpan(5, SaltSize).ToArray();
        var expected = payload.AsSpan(5 + SaltSize, KeySize).ToArray();
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, KeySize);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// Indicates whether a digest would benefit from being recomputed, the reference cost having
    /// increased.
    /// </summary>
    public static bool NeedsRehash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return true;
        }

        try
        {
            var payload = Convert.FromBase64String(hash);

            return payload.Length != 1 + 4 + SaltSize + KeySize
                || payload[0] != Version
                || BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1, 4)) < Iterations;
        }
        catch (FormatException)
        {
            return true;
        }
    }
}
