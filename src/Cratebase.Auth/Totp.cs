using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cratebase.Auth;

/// <summary>
/// Time-based one-time passwords (RFC 6238).
/// </summary>
/// <remarks>
/// Compatible with Google Authenticator, Aegis, 1Password, Bitwarden: HMAC-SHA1, six digits,
/// thirty-second window. These are the parameters authenticator apps assume by default; deviating
/// from them would silently make enrollment incompatible with half of them.
/// </remarks>
public static class Totp
{
    /// <summary>Duration of a window, in seconds.</summary>
    public const int PeriodSeconds = 30;

    /// <summary>Number of digits in the code.</summary>
    public const int Digits = 6;

    /// <summary>
    /// Number of tolerance windows on each side.
    /// </summary>
    /// <remarks>
    /// Just one: it absorbs clock drift and typing time. Widening it further would multiply, by as
    /// much, the surface of a brute-force attack over an already-narrow six-digit space.
    /// </remarks>
    public const int ToleranceWindows = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Produces a shared secret, base32-encoded.</summary>
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

    /// <summary>Builds the <c>otpauth://</c> URI to present as a QR code.</summary>
    public static string ProvisioningUri(string secret, string account, string issuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var label = Uri.EscapeDataString($"{issuer}:{account}");

        return $"otpauth://totp/{label}?secret={secret}" +
               $"&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>Computes the code for a given window.</summary>
    public static string Compute(string secret, DateTimeOffset moment, int windowOffset = 0)
    {
        var key = FromBase32(secret);
        var counter = moment.ToUnixTimeSeconds() / PeriodSeconds + windowOffset;

        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buffer, counter);

        Span<byte> hash = stackalloc byte[20];

        // HMAC-SHA1 is flagged as weak by analyzers, and generally is. Here, it's mandated by RFC
        // 6238: Google Authenticator, Aegis, and the like can only produce this, and the rare apps
        // that accept SHA-256 do so as an option. Choosing a "better" algorithm would make
        // enrollment incompatible with nearly the entire installed base, for no real gain — the
        // secret is never transmitted, and the code expires in 30 seconds.
#pragma warning disable CA5350 // Algorithm mandated by RFC 6238.
        HMACSHA1.HashData(key, buffer, hash);
#pragma warning restore CA5350

        // Dynamic truncation, as described by the RFC: the last nibble designates the offset.
        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits)).ToString(
            CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    /// <summary>Verifies a code, tolerance included.</summary>
    public static bool Verify(string? code, string secret, DateTimeOffset moment)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits)
        {
            return false;
        }

        for (var offset = -ToleranceWindows; offset <= ToleranceWindows; offset++)
        {
            var expected = Compute(secret, moment, offset);

            // Constant-time comparison: a naive comparison leaks the number of correct digits,
            // reducing a brute force from 10^6 to 60 attempts.
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
                throw new ArgumentException($"\"{value}\" is not a valid base32 secret.", nameof(value));
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
