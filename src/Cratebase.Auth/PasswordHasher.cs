using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cratebase.Auth;

/// <summary>
/// Hachage des mots de passe : PBKDF2-HMAC-SHA512.
/// </summary>
/// <remarks>
/// <para>
/// Format du condensat, encodé en base64 : <c>[version:1][itérations:4][sel:16][clé:32]</c>. La
/// version et le nombre d'itérations sont <b>stockés avec le condensat</b>, et non lus dans la
/// configuration : sans cela, augmenter le coût demain rendrait invérifiables tous les mots de
/// passe d'hier.
/// </para>
/// <para>
/// La comparaison est à temps constant. Une comparaison naïve laisse fuir la longueur du préfixe
/// correct, ce qui suffit à reconstruire un condensat octet par octet.
/// </para>
/// </remarks>
public static class PasswordHasher
{
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>Nombre d'itérations appliqué aux nouveaux condensats.</summary>
    public const int Iterations = 210_000;

    /// <summary>Longueur minimale exigée d'un mot de passe.</summary>
    public const int MinimumLength = 10;

    /// <summary>Produit le condensat d'un mot de passe.</summary>
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

    /// <summary>Vérifie un mot de passe contre un condensat.</summary>
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
    /// Indique si un condensat gagnerait à être recalculé, le coût de référence ayant augmenté.
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
