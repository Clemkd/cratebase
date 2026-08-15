using Cratebase.Admin;
using Cratebase.Core;
using Cratebase.Server;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Les réglages sont écrits par un PATCH partiel. La règle qui compte : ce que la charge ne
/// mentionne pas ne bouge pas — sans quoi un écran qui n'enverrait que le nom de l'instance
/// remettrait la rétention et la collecte d'adresse à leurs valeurs par défaut, en silence.
/// </summary>
public class AdminSettingsTests
{
    private static readonly AppSettings Current = new()
    {
        AppName = "Production",
        AppUrl = "https://exemple.org",
        Logs = new LogSettings
        {
            Enabled = true,
            RetentionDays = 30,
            MinLevel = LogSeverity.Warning,
            LogIp = false,
        },
    };

    [Fact]
    public void Une_charge_vide_ne_change_rien()
    {
        var result = new SettingsRequest().Apply(Current);

        result.ShouldBe(Current);
    }

    [Fact]
    public void Un_champ_fourni_ne_reinitialise_pas_les_autres()
    {
        var result = new SettingsRequest { AppName = "Recette" }.Apply(Current);

        result.AppName.ShouldBe("Recette");
        result.AppUrl.ShouldBe("https://exemple.org");
        result.Logs.RetentionDays.ShouldBe(30);
        result.Logs.MinLevel.ShouldBe(LogSeverity.Warning);
        result.Logs.LogIp.ShouldBeFalse();
    }

    [Fact]
    public void Un_sous_objet_partiel_ne_reinitialise_pas_ses_voisins()
    {
        var result = new SettingsRequest
        {
            Logs = new LogSettingsRequest { RetentionDays = 3 },
        }.Apply(Current);

        result.Logs.RetentionDays.ShouldBe(3);
        result.Logs.Enabled.ShouldBeTrue();
        result.Logs.MinLevel.ShouldBe(LogSeverity.Warning);
        result.Logs.LogIp.ShouldBeFalse();
    }

    [Fact]
    public void Un_booleen_pose_a_faux_est_bien_appliqué()
    {
        // Le piège classique de la fusion partielle : `false ?? défaut` doit rendre false, et non
        // retomber sur la valeur en place parce que le test porterait sur la véracité.
        var result = new SettingsRequest
        {
            Logs = new LogSettingsRequest { Enabled = false },
        }.Apply(Current);

        result.Logs.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Le_nom_est_obligatoire()
    {
        var failure = Should.Throw<CratebaseValidationException>(
            () => (Current with { AppName = "   " }).Validated());

        failure.Errors.ShouldContainKey("appName");
    }

    [Fact]
    public void La_retention_reste_dans_ses_bornes()
    {
        var failure = Should.Throw<CratebaseValidationException>(
            () => (Current with { Logs = Current.Logs with { RetentionDays = 900 } }).Validated());

        failure.Errors.ShouldContainKey("logs.retentionDays");

        // Zéro est licite : c'est la conservation illimitée.
        Should.NotThrow(() => (Current with { Logs = Current.Logs with { RetentionDays = 0 } }).Validated());
    }

    [Theory]
    [InlineData("exemple.org")]
    [InlineData("ftp://exemple.org")]
    [InlineData("javascript:alert(1)")]
    public void Une_url_publique_doit_etre_absolue_et_http(string url)
    {
        var failure = Should.Throw<CratebaseValidationException>(
            () => (Current with { AppUrl = url }).Validated());

        failure.Errors.ShouldContainKey("appUrl");
    }

    [Fact]
    public void Le_nom_et_l_url_sont_normalises()
    {
        var result = (Current with { AppName = "  Production  ", AppUrl = "https://exemple.org/" })
            .Validated();

        result.AppName.ShouldBe("Production");

        // La barre finale est retirée : sans cela, une URL composée par concaténation produirait
        // « https://exemple.org//api ».
        result.AppUrl.ShouldBe("https://exemple.org");
    }

    [Fact]
    public void Une_url_vide_reste_acceptee()
    {
        Should.NotThrow(() => (Current with { AppUrl = "" }).Validated());
    }
}

/// <summary>Correspondance entre statut HTTP et gravité, et découpage des tranches.</summary>
public class LogSeverityTests
{
    [Theory]
    [InlineData(200, LogSeverity.Info)]
    [InlineData(304, LogSeverity.Info)]
    [InlineData(400, LogSeverity.Warning)]
    [InlineData(401, LogSeverity.Warning)]
    [InlineData(404, LogSeverity.Warning)]
    [InlineData(500, LogSeverity.Error)]
    [InlineData(503, LogSeverity.Error)]
    public void Un_statut_donne_une_gravite(int status, LogSeverity expected)
    {
        LogSeverities.ForStatus(status).ShouldBe(expected);
    }

    [Fact]
    public void Le_filtre_de_niveau_est_inclusif()
    {
        LogSeverities.AtLeast(LogSeverity.Warning)
            .ShouldBe([LogSeverity.Warning, LogSeverity.Error]);

        LogSeverities.AtLeast(LogSeverity.Debug).Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("2026-08-13", "2026-08-13T00:00:00.000Z")]
    [InlineData("2026-08-13T14", "2026-08-13T14:00:00.000Z")]
    [InlineData("2026-08-13T14:05", "2026-08-13T14:05:00.000Z")]
    public void Une_tranche_se_relit_en_instant(string bucket, string expected)
    {
        Timestamp.Normalize(LogBuckets.ToInstant(bucket)).ShouldBe(expected);
    }
}
