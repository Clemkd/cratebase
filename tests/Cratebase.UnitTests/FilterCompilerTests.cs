using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Data.Postgres;
using Cratebase.Data.Sqlite;
using Cratebase.Expressions;
using Shouldly;

namespace Cratebase.UnitTests;

public class FilterCompilerTests
{
    private static readonly RecordId AuthId = RecordId.Parse("01922f8e-0000-7000-8000-000000000001");

    private static FilterCompiler Compiler(
        ISqlDialect? dialect = null,
        FilterRequestContext? request = null) =>
        new(dialect ?? SqliteDialect.Instance,
            new FakeResolver(),
            request ?? FilterRequestContext.None,
            FixedClock.Default);

    private static SqlPredicate Compile(
        string expression,
        ISqlDialect? dialect = null,
        FilterRequestContext? request = null) =>
        Compiler(dialect, request).Compile(expression, "t");

    // ── Frontière d'injection ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("inconnu = 1")]
    [InlineData("title.sous = 1")]
    [InlineData("password = 'x'")]
    public void Un_champ_hors_schema_ne_devient_jamais_du_sql(string expression)
    {
        var error = Should.Throw<FilterSyntaxException>(() => Compile(expression));

        error.StatusCode.ShouldBe(400);
    }

    [Fact]
    public void Toute_valeur_devient_un_parametre()
    {
        var predicate = Compile(@"title = 'Robert\'); DROP TABLE posts;--'");

        // La charge hostile est dans les paramètres, pas dans le texte SQL.
        predicate.Sql.ShouldBe("\"t\".\"title\" = @p0");
        predicate.Parameters["p0"].ShouldBe("Robert'); DROP TABLE posts;--");
    }

    [Fact]
    public void Lidiome_dechappement_sql_nexiste_pas_dans_ce_langage()
    {
        // Le doublement de quote de SQL n'est pas un échappement ici : « 'a''b' » se lit comme la
        // chaîne « a » suivie de jetons parasites, et l'expression est rejetée. La charge utile
        // d'injection la plus courante ne franchit donc même pas l'analyse syntaxique.
        Should.Throw<FilterSyntaxException>(() => Compile("title = 'a''); DROP TABLE posts;--'"));
    }

    [Fact]
    public void Les_identifiants_sont_echappes_et_qualifies() =>
        Compile("title = 'x'").Sql.ShouldBe("\"t\".\"title\" = @p0");

    // ── Composition : la règle ne peut qu'ajouter ──────────────────────────────────────────────

    [Fact]
    public void Une_regle_et_un_filtre_se_composent_en_conjonction()
    {
        var compiler = Compiler();
        var rule = compiler.Compile("owner = 'u1'", "t", "r");
        var filter = compiler.Compile("title ~ 'actu'", "t", "f");

        var combined = rule.And(filter);

        combined.Sql.ShouldBe("(\"t\".\"owner\" = @r0) AND (\"t\".\"title\" LIKE @f0 ESCAPE '\\')");
        combined.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Composer_deux_predicats_de_meme_prefixe_est_une_erreur_de_programmation()
    {
        var compiler = Compiler();
        var a = compiler.Compile("owner = 'u1'", "t");
        var b = compiler.Compile("title = 'x'", "t");

        // Le garde-fou : sans lui, la fusion écraserait silencieusement un paramètre et la règle
        // s'appliquerait avec la valeur du filtre.
        Should.Throw<InvalidOperationException>(() => a.And(b));
    }

    [Fact]
    public void Un_predicat_sans_contrainte_seffece_dans_la_composition()
    {
        var compiler = Compiler();
        var rule = compiler.Compile((string?)null, "t", "r");
        var filter = compiler.Compile("title = 'x'", "t", "f");

        rule.IsUnconstrained.ShouldBeTrue();
        rule.And(filter).Sql.ShouldBe("\"t\".\"title\" = @f0");
    }

    [Fact]
    public void Le_predicat_de_refus_nadmet_aucune_ligne() =>
        SqlPredicate.Denied.Sql.ShouldBe("1 = 0");

    // ── Repli à la compilation ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Lidiome_dauthentification_se_replie_selon_lappelant()
    {
        var anonymous = Compile("@request.auth.id != ''");
        anonymous.Sql.ShouldBe("1 = 0");

        var authenticated = Compile(
            "@request.auth.id != ''",
            request: new FilterRequestContext { Auth = new FakeUser(AuthId) });

        authenticated.Sql.ShouldBe("1 = 1");
    }

    [Fact]
    public void Un_champ_de_lenregistrement_dauth_est_resolu_en_parametre()
    {
        var predicate = Compile(
            "owner = @request.auth.id",
            request: new FilterRequestContext { Auth = new FakeUser(AuthId) });

        predicate.Sql.ShouldBe("\"t\".\"owner\" = @p0");
        predicate.Parameters["p0"].ShouldBe(AuthId.ToString());
    }

    [Fact]
    public void Un_champ_libre_de_lenregistrement_dauth_est_atteignable()
    {
        var predicate = Compile(
            "@request.auth.role = 'editor'",
            request: new FilterRequestContext { Auth = new FakeUser(AuthId) });

        predicate.Sql.ShouldBe("1 = 1");
    }

    [Fact]
    public void Le_verbe_et_le_contexte_de_requete_sont_resolus()
    {
        var request = new FilterRequestContext { Method = "POST", Context = "oauth2" };

        Compile("@request.method = 'POST'", request: request).Sql.ShouldBe("1 = 1");
        Compile("@request.context = 'default'", request: request).Sql.ShouldBe("1 = 0");
    }

    [Fact]
    public void Le_modificateur_isset_reflete_ce_qui_a_ete_soumis()
    {
        var request = new FilterRequestContext
        {
            Body = new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "x" },
        };

        Compile("@request.body.title:isset = true", request: request).Sql.ShouldBe("1 = 1");
        Compile("@request.body.views:isset = true", request: request).Sql.ShouldBe("1 = 0");
    }

    [Fact]
    public void Le_modificateur_changed_compare_au_precedent_etat()
    {
        var request = new FilterRequestContext
        {
            Body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "nouveau",
                ["views"] = 3d,
            },
            Original = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "ancien",
                ["views"] = 3d,
            },
        };

        Compile("@request.body.title:changed = true", request: request).Sql.ShouldBe("1 = 1");
        Compile("@request.body.views:changed = true", request: request).Sql.ShouldBe("1 = 0");
    }

    // ── Macros de date ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Une_macro_devient_un_parametre_pas_une_fonction_du_moteur()
    {
        var predicate = Compile("releasedAt < @now");

        // Le point qui distingue Cratebase de PocketBase : aucun strftime() dans le SQL produit.
        predicate.Sql.ShouldBe("\"t\".\"released_at\" < @p0");
        predicate.Parameters["p0"].ShouldBe("2026-08-13T14:05:09.123Z");
    }

    [Theory]
    [InlineData("@todayStart", "2026-08-13T00:00:00.000Z")]
    [InlineData("@todayEnd", "2026-08-13T23:59:59.999Z")]
    [InlineData("@monthStart", "2026-08-01T00:00:00.000Z")]
    [InlineData("@monthEnd", "2026-08-31T23:59:59.999Z")]
    [InlineData("@yearStart", "2026-01-01T00:00:00.000Z")]
    [InlineData("@yearEnd", "2026-12-31T23:59:59.999Z")]
    [InlineData("@yesterday", "2026-08-12T14:05:09.123Z")]
    [InlineData("@tomorrow", "2026-08-14T14:05:09.123Z")]
    public void Les_bornes_temporelles_sont_exactes(string macro, string expected) =>
        Compile($"releasedAt < {macro}").Parameters["p0"].ShouldBe(expected);

    [Fact]
    public void Une_macro_inconnue_est_refusee() =>
        Should.Throw<FilterSyntaxException>(() => Compile("releasedAt < @jamais"));

    // ── Normalisation par type ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Un_booleen_est_normalise_en_0_1_sur_sqlite() =>
        Compile("published = true").Parameters["p0"].ShouldBe(1L);

    [Fact]
    public void Un_booleen_reste_booleen_sur_postgresql() =>
        Compile("published = true", PostgresDialect.Instance).Parameters["p0"].ShouldBe(true);

    [Fact]
    public void Une_date_litterale_est_ramenee_a_la_forme_canonique() =>
        Compile("releasedAt > '2026-08-13'").Parameters["p0"].ShouldBe("2026-08-13T00:00:00.000Z");

    [Fact]
    public void Une_date_litterale_devient_un_instant_sur_postgresql() =>
        Compile("releasedAt > '2026-08-13'", PostgresDialect.Instance)
            .Parameters["p0"]
            .ShouldBe(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));

    // ── Opérateur « contient » ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Loperande_est_enrobee_de_jokers()
    {
        var predicate = Compile("title ~ 'actu'");

        predicate.Sql.ShouldBe("\"t\".\"title\" LIKE @p0 ESCAPE '\\'");
        predicate.Parameters["p0"].ShouldBe("%actu%");
    }

    [Fact]
    public void Les_jokers_ecrits_par_lutilisateur_sont_echappes() =>
        Compile("title ~ '100%_x'").Parameters["p0"].ShouldBe(@"%100\%\_x%");

    [Fact]
    public void Postgresql_utilise_ilike_pour_saligner_sur_sqlite() =>
        Compile("title ~ 'actu'", PostgresDialect.Instance)
            .Sql.ShouldBe("\"t\".\"title\" ILIKE @p0 ESCAPE '\\'");

    [Fact]
    public void La_negation_de_contient_est_rendue() =>
        Compile("title !~ 'actu'").Sql.ShouldStartWith("NOT (");

    [Fact]
    public void Contient_exige_une_valeur_a_droite() =>
        Should.Throw<FilterSyntaxException>(() => Compile("title ~ owner"));

    // ── Champs multi-valués ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Par_defaut_un_champ_multivalue_exige_que_tous_les_elements_satisfassent() =>
        Compile("tags = 'actu'").Sql.ShouldStartWith("NOT EXISTS (SELECT 1 FROM json_each(");

    [Fact]
    public void Le_prefixe_interrogatif_bascule_en_au_moins_un() =>
        Compile("tags ?= 'actu'").Sql.ShouldStartWith("EXISTS (SELECT 1 FROM json_each(");

    [Fact]
    public void Postgresql_deplie_le_tableau_json_de_la_meme_facon() =>
        Compile("tags ?= 'actu'", PostgresDialect.Instance)
            .Sql.ShouldStartWith("EXISTS (SELECT 1 FROM jsonb_array_elements_text(");

    [Fact]
    public void Le_modificateur_length_compte_les_elements()
    {
        var predicate = Compile("tags:length > 2");

        predicate.Sql.ShouldBe("coalesce(json_array_length(\"t\".\"tags\"), 0) > @p0");
        predicate.Parameters["p0"].ShouldBe(2d);
    }

    // ── Divers ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void La_comparaison_a_null_devient_is_null()
    {
        Compile("title = null").Sql.ShouldBe("\"t\".\"title\" IS NULL");
        Compile("title != null").Sql.ShouldBe("\"t\".\"title\" IS NOT NULL");
    }

    [Fact]
    public void Une_valeur_a_gauche_fait_pivoter_loperateur() =>
        Compile("100 < views").Sql.ShouldBe("\"t\".\"views\" > @p0");

    [Fact]
    public void Le_modificateur_lower_compare_en_minuscules()
    {
        var predicate = Compile("title:lower = 'Actu'");

        predicate.Sql.ShouldBe("lower(\"t\".\"title\") = @p0");
        predicate.Parameters["p0"].ShouldBe("actu");
    }

    [Fact]
    public void Les_connecteurs_logiques_sont_parenthèses() =>
        Compile("title = 'a' && (views > 1 || published = true)")
            .Sql.ShouldBe(
                "(\"t\".\"title\" = @p0 AND (\"t\".\"views\" > @p1 OR \"t\".\"published\" = @p2))");

    [Fact]
    public void Deux_champs_se_comparent_entre_eux() =>
        Compile("title = owner").Sql.ShouldBe("\"t\".\"title\" = \"t\".\"owner\"");

    [Fact]
    public void Les_jointures_de_collection_sont_refusees_explicitement()
    {
        var error = Should.Throw<FilterSyntaxException>(
            () => Compile("@collection.membres.user = 'x'"));

        error.Message.ShouldContain("@collection");
    }

    [Fact]
    public void Une_fonction_non_prise_en_charge_est_refusee_clairement()
    {
        var error = Should.Throw<FilterSyntaxException>(() => Compile("geoDistance(1,2,3,4) < 10"));

        error.Message.ShouldContain("geoDistance");
    }
}
