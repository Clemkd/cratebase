using System.Diagnostics;
using Cratebase.Auth;
using Shouldly;

namespace Cratebase.UnitTests;

public class PasswordHasherTests
{
    [Fact]
    public void Un_mot_de_passe_se_verifie_contre_son_condensat()
    {
        var hash = PasswordHasher.Hash("motdepasse-solide");

        PasswordHasher.Verify("motdepasse-solide", hash).ShouldBeTrue();
        PasswordHasher.Verify("motdepasse-solidE", hash).ShouldBeFalse();
        PasswordHasher.Verify("", hash).ShouldBeFalse();
        PasswordHasher.Verify(null, hash).ShouldBeFalse();
    }

    [Fact]
    public void Deux_condensats_du_meme_mot_de_passe_different()
    {
        // Sel aléatoire : sans lui, deux comptes ayant le même mot de passe se reconnaissent au
        // simple examen de la table.
        var first = PasswordHasher.Hash("motdepasse-solide");
        var second = PasswordHasher.Hash("motdepasse-solide");

        first.ShouldNotBe(second);
        PasswordHasher.Verify("motdepasse-solide", first).ShouldBeTrue();
        PasswordHasher.Verify("motdepasse-solide", second).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas du base64 !")]
    [InlineData("YWJj")]
    public void Un_condensat_illisible_ne_valide_jamais(string? hash) =>
        PasswordHasher.Verify("motdepasse-solide", hash).ShouldBeFalse();

    [Fact]
    public void Le_cout_est_stocke_avec_le_condensat()
    {
        // C'est ce qui permet d'augmenter le coût demain sans rendre invérifiables les mots de
        // passe d'hier.
        PasswordHasher.NeedsRehash(PasswordHasher.Hash("motdepasse-solide")).ShouldBeFalse();
        PasswordHasher.NeedsRehash(null).ShouldBeTrue();
        PasswordHasher.NeedsRehash("YWJj").ShouldBeTrue();
    }

    [Fact]
    public void Le_cout_de_derivation_est_perceptible()
    {
        // Un hachage instantané est un hachage trop faible. On ne mesure pas une valeur cible —
        // ce serait instable en intégration continue — seulement qu'un coût existe.
        var stopwatch = Stopwatch.StartNew();
        PasswordHasher.Hash("motdepasse-solide");
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThan(5);
    }
}
