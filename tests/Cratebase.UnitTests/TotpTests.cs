using Cratebase.Auth;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// RFC 6238 test vectors, the only valid proof that a TOTP implementation is correct. A code that
/// "seems to work" between two calls of the same implementation proves nothing — it must coincide
/// with what the user's authenticator app will produce.
/// </summary>
public class TotpTests
{
    // "12345678901234567890" in ASCII, base32-encoded: the RFC's secret.
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void The_rfc_vectors_are_reproduced(long unixSeconds, string expected)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        Totp.Compute(RfcSecret, moment).ShouldBe(expected);
    }

    [Fact]
    public void A_code_is_accepted_within_its_window()
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var code = Totp.Compute(RfcSecret, moment);

        Totp.Verify(code, RfcSecret, moment).ShouldBeTrue();
    }

    [Fact]
    public void The_tolerance_covers_one_window_on_either_side()
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        // Late entry: the previous window's code must still pass.
        var previous = Totp.Compute(RfcSecret, moment, windowOffset: -1);
        var next = Totp.Compute(RfcSecret, moment, windowOffset: 1);

        Totp.Verify(previous, RfcSecret, moment).ShouldBeTrue();
        Totp.Verify(next, RfcSecret, moment).ShouldBeTrue();

        // Two windows apart: refused. Widening it would multiply the brute-force surface.
        var distant = Totp.Compute(RfcSecret, moment, windowOffset: 3);
        Totp.Verify(distant, RfcSecret, moment).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void A_malformed_code_is_rejected(string? code) =>
        Totp.Verify(code, RfcSecret, DateTimeOffset.UnixEpoch).ShouldBeFalse();

    [Fact]
    public void Two_secrets_are_distinct()
    {
        var first = Totp.NewSecret();
        var second = Totp.NewSecret();

        first.ShouldNotBe(second);
        first.Length.ShouldBe(32);
        first.ShouldAllBe(character => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(character, StringComparison.Ordinal));
    }

    [Fact]
    public void The_enrollment_uri_carries_the_expected_parameters()
    {
        var uri = Totp.ProvisioningUri(RfcSecret, "member@example.com", "Cratebase");

        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain($"secret={RfcSecret}");
        uri.ShouldContain("algorithm=SHA1");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
        uri.ShouldContain("issuer=Cratebase");
    }

    [Fact]
    public void An_invalid_secret_is_rejected() =>
        Should.Throw<ArgumentException>(() => Totp.Compute("not base32 !", DateTimeOffset.UnixEpoch));
}
