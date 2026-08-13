using Cratebase.Core;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// La normalisation des dates est le point de portabilité le plus exposé : SQLite compare les
/// instants comme des chaînes, PostgreSQL les compare temporellement. Les deux ne coïncident que
/// si la forme textuelle est strictement canonique.
/// </summary>
public class TimestampTests
{
    [Fact]
    public void La_forme_canonique_a_une_longueur_fixe()
    {
        var value = Timestamp.Normalize(new DateTimeOffset(2026, 8, 13, 14, 5, 9, 123, TimeSpan.Zero));

        value.ShouldBe("2026-08-13T14:05:09.123Z");
        value.Length.ShouldBe(Timestamp.Length);
    }

    [Fact]
    public void Un_instant_a_fuseau_est_ramene_en_utc()
    {
        var paris = new DateTimeOffset(2026, 8, 13, 16, 5, 9, 123, TimeSpan.FromHours(2));

        Timestamp.Normalize(paris).ShouldBe("2026-08-13T14:05:09.123Z");
    }

    [Fact]
    public void Les_zeros_de_tete_sont_conserves()
    {
        // Sans eux, « 2026-8-3T4:05 » trie avant « 2026-12-... » en comparaison textuelle : les
        // filtres de plage deviennent faux sur SQLite, et seulement sur SQLite.
        var value = Timestamp.Normalize(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero));

        value.ShouldBe("2026-01-02T03:04:05.006Z");
    }

    [Fact]
    public void Lordre_lexicographique_suit_lordre_chronologique()
    {
        var instants = new[]
        {
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, 999, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, 1, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, 0, TimeSpan.Zero),
        };

        var canonical = instants.Select(Timestamp.Normalize).ToArray();

        canonical.ShouldBe(canonical.OrderBy(v => v, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("2026-08-13T14:05:09.123Z", "2026-08-13T14:05:09.123Z")]
    [InlineData("2026-08-13T16:05:09.123+02:00", "2026-08-13T14:05:09.123Z")]
    [InlineData("2026-08-13T14:05:09Z", "2026-08-13T14:05:09.000Z")]
    [InlineData("2026-08-13", "2026-08-13T00:00:00.000Z")]
    public void Les_formes_client_admises_sont_ramenees_a_la_forme_canonique(string raw, string expected)
    {
        Timestamp.TryNormalize(raw, out var canonical).ShouldBeTrue();
        canonical.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pas une date")]
    [InlineData("2026-13-45")]
    public void Les_valeurs_non_temporelles_sont_refusees(string? raw) =>
        Timestamp.TryNormalize(raw, out _).ShouldBeFalse();

    [Fact]
    public void Le_cycle_ecriture_lecture_preserve_linstant()
    {
        // 2028 est bissextile : le 29 février est le cas limite qui vaut d'être couvert.
        var origin = new DateTimeOffset(2028, 2, 29, 23, 59, 59, 999, TimeSpan.Zero);

        Timestamp.Parse(Timestamp.Normalize(origin)).ShouldBe(origin);
    }

    [Fact]
    public void Une_date_sans_fuseau_est_interpretee_en_utc()
    {
        var unspecified = new DateTime(2026, 8, 13, 14, 5, 9, DateTimeKind.Unspecified);

        Timestamp.Normalize(unspecified).ShouldBe("2026-08-13T14:05:09.000Z");
    }
}
