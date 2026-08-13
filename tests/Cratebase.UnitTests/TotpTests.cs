using Cratebase.Auth;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Vecteurs de test de la RFC 6238, seule preuve valable qu'une implémentation TOTP est correcte.
/// Un code qui « a l'air de marcher » entre deux appels de la même implémentation ne prouve rien —
/// il faut qu'il coïncide avec ce que produira l'application d'authentification de l'utilisateur.
/// </summary>
public class TotpTests
{
    // « 12345678901234567890 » en ASCII, encodé en base32 : le secret de la RFC.
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Les_vecteurs_de_la_rfc_sont_reproduits(long unixSeconds, string expected)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        Totp.Compute(RfcSecret, moment).ShouldBe(expected);
    }

    [Fact]
    public void Un_code_est_accepte_dans_sa_fenetre()
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var code = Totp.Compute(RfcSecret, moment);

        Totp.Verify(code, RfcSecret, moment).ShouldBeTrue();
    }

    [Fact]
    public void La_tolerance_couvre_une_fenetre_de_part_et_dautre()
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(1111111109);

        // Saisie tardive : le code de la fenêtre précédente doit encore passer.
        var previous = Totp.Compute(RfcSecret, moment, windowOffset: -1);
        var next = Totp.Compute(RfcSecret, moment, windowOffset: 1);

        Totp.Verify(previous, RfcSecret, moment).ShouldBeTrue();
        Totp.Verify(next, RfcSecret, moment).ShouldBeTrue();

        // Deux fenêtres d'écart : refusé. Élargir multiplierait la surface de force brute.
        var distant = Totp.Compute(RfcSecret, moment, windowOffset: 3);
        Totp.Verify(distant, RfcSecret, moment).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Un_code_malforme_est_refuse(string? code) =>
        Totp.Verify(code, RfcSecret, DateTimeOffset.UnixEpoch).ShouldBeFalse();

    [Fact]
    public void Deux_secrets_sont_distincts()
    {
        var first = Totp.NewSecret();
        var second = Totp.NewSecret();

        first.ShouldNotBe(second);
        first.Length.ShouldBe(32);
        first.ShouldAllBe(character => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(character, StringComparison.Ordinal));
    }

    [Fact]
    public void Luri_dinscription_porte_les_parametres_attendus()
    {
        var uri = Totp.ProvisioningUri(RfcSecret, "membre@exemple.fr", "Cratebase");

        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain($"secret={RfcSecret}");
        uri.ShouldContain("algorithm=SHA1");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
        uri.ShouldContain("issuer=Cratebase");
    }

    [Fact]
    public void Un_secret_invalide_est_rejete() =>
        Should.Throw<ArgumentException>(() => Totp.Compute("pas du base32 !", DateTimeOffset.UnixEpoch));
}
