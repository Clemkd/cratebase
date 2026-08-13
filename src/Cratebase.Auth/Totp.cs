using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cratebase.Auth;

/// <summary>
/// Mots de passe à usage unique fondés sur le temps (RFC 6238).
/// </summary>
/// <remarks>
/// Compatible avec Google Authenticator, Aegis, 1Password, Bitwarden : HMAC-SHA1, six chiffres,
/// fenêtre de trente secondes. Ces paramètres sont ceux que les applications d'authentification
/// supposent par défaut ; s'en écarter rendrait l'inscription silencieusement incompatible avec la
/// moitié d'entre elles.
/// </remarks>
public static class Totp
{
    /// <summary>Durée d'une fenêtre, en secondes.</summary>
    public const int PeriodSeconds = 30;

    /// <summary>Nombre de chiffres du code.</summary>
    public const int Digits = 6;

    /// <summary>
    /// Nombre de fenêtres de tolérance de part et d'autre.
    /// </summary>
    /// <remarks>
    /// Une seule : elle absorbe la dérive d'horloge et le temps de saisie. Élargir davantage
    /// multiplierait d'autant la surface d'une attaque par force brute sur un espace de six
    /// chiffres, déjà étroit.
    /// </remarks>
    public const int ToleranceWindows = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Produit un secret partagé, encodé en base32.</summary>
    public static string NewSecret(int bytes = 20)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        var builder = new StringBuilder();

        var bits = 0;
        var value = 0;

        foreach (var current in buffer)
        {
            value = (value << 8) | current;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    /// <summary>Construit l'URI <c>otpauth://</c> à présenter en code QR.</summary>
    public static string ProvisioningUri(string secret, string account, string issuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var label = Uri.EscapeDataString($"{issuer}:{account}");

        return $"otpauth://totp/{label}?secret={secret}" +
               $"&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>Calcule le code d'une fenêtre donnée.</summary>
    public static string Compute(string secret, DateTimeOffset moment, int windowOffset = 0)
    {
        var key = FromBase32(secret);
        var counter = moment.ToUnixTimeSeconds() / PeriodSeconds + windowOffset;

        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buffer, counter);

        Span<byte> hash = stackalloc byte[20];

        // HMAC-SHA1 est signalé comme faible par les analyseurs, et il l'est en général. Ici, il
        // est imposé par la RFC 6238 : Google Authenticator, Aegis et consorts ne savent produire
        // que cela, et les rares applications qui acceptent SHA-256 le font en option. Choisir un
        // algorithme « meilleur » rendrait l'inscription incompatible avec la quasi-totalité du
        // parc, sans gain réel — le secret n'est jamais transmis, et le code expire en 30 secondes.
#pragma warning disable CA5350 // Algorithme imposé par la RFC 6238.
        HMACSHA1.HashData(key, buffer, hash);
#pragma warning restore CA5350

        // Troncature dynamique, telle que la RFC la décrit : le dernier quartet désigne l'offset.
        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits)).ToString(
            CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    /// <summary>Vérifie un code, tolérance comprise.</summary>
    public static bool Verify(string? code, string secret, DateTimeOffset moment)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits)
        {
            return false;
        }

        for (var offset = -ToleranceWindows; offset <= ToleranceWindows; offset++)
        {
            var expected = Compute(secret, moment, offset);

            // Comparaison à temps constant : une comparaison naïve laisse fuir le nombre de
            // chiffres corrects, ce qui réduit une force brute de 10^6 à 60 essais.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] FromBase32(string value)
    {
        var cleaned = value.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(cleaned.Length * 5 / 8);

        var bits = 0;
        var accumulator = 0;

        foreach (var current in cleaned)
        {
            var index = Base32Alphabet.IndexOf(current, StringComparison.Ordinal);

            if (index < 0)
            {
                throw new ArgumentException($"« {value} » n'est pas un secret base32 valide.", nameof(value));
            }

            accumulator = (accumulator << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)((accumulator >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
