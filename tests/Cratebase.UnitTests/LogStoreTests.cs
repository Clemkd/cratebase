using Cratebase.Admin;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Data.Sqlite;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Le journal est la seule table écrite hors du chemin de la requête : ses entrées transitent par
/// un tampon, et tout ce qui s'y perd se perd en silence. D'où des tests qui exercent le cycle
/// complet — dépôt, vidage, relecture — plutôt que les seules requêtes SQL.
/// </summary>
public sealed class LogStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"cratebase-logs-{Guid.NewGuid():N}.db");

    private readonly FixedClock _clock = FixedClock.Default;

    private LogStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connections = new DbConnectionFactory(
            SqliteDialect.Instance,
            SqliteDialect.Instance,
            $"Data Source={_path}");

        _store = new LogStore(connections, _clock);

        await _store.EnsureTableAsync(Token);
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();

        // Les connexions sont fermées après chaque opération, mais le pilote garde un bassin :
        // sans purge, le fichier reste tenu et ne peut pas être supprimé.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);

        return ValueTask.CompletedTask;
    }

    private LogEntry Request(string method, string url, int status, DateTimeOffset? at = null) => new()
    {
        Created = at ?? _clock.UtcNow,
        Level = LogSeverities.ForStatus(status),
        Message = $"{method} {url} → {status}",
        Method = method,
        Url = url,
        Status = status,
        Duration = 12.5,
    };

    [Fact]
    public async Task Une_entree_deposee_est_relue_a_l_identique()
    {
        _store.Record(Request("GET", "/api/collections", 200));

        (await _store.FlushAsync(Token)).ShouldBe(1);

        var page = await _store.QueryAsync(new LogQuery(), Token);
        var entry = page.Items.ShouldHaveSingleItem();

        entry.Method.ShouldBe("GET");
        entry.Url.ShouldBe("/api/collections");
        entry.Status.ShouldBe(200);
        entry.Level.ShouldBe(LogSeverity.Info);
        entry.Duration.ShouldBe(12.5);
        entry.Created.ShouldBe(_clock.UtcNow);
    }

    [Fact]
    public async Task Rien_n_est_ecrit_tant_que_le_tampon_n_est_pas_vide()
    {
        _store.Record(Request("GET", "/api/collections", 200));

        (await _store.CountAsync(Token)).ShouldBe(0);

        await _store.FlushAsync(Token);

        (await _store.CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task Le_filtre_de_niveau_retient_exactement_l_ensemble_demande()
    {
        _store.Record(Request("GET", "/api/ok", 200));
        _store.Record(Request("GET", "/api/absent", 404));
        _store.Record(Request("POST", "/api/casse", 500));

        await _store.FlushAsync(Token);

        var graves = await _store.QueryAsync(
            new LogQuery { Levels = [LogSeverity.Warning, LogSeverity.Error] },
            Token);

        graves.TotalItems.ShouldBe(2);
        graves.Items.Select(entry => entry.Status).ShouldBe([500, 404], ignoreOrder: true);

        var erreurs = await _store.QueryAsync(new LogQuery { Levels = [LogSeverity.Error] }, Token);

        erreurs.Items.ShouldHaveSingleItem().Status.ShouldBe(500);

        // Ce qu'une borne basse ne savait pas dire : les avertissements sans les erreurs.
        var seulsAvertissements = await _store.QueryAsync(
            new LogQuery { Levels = [LogSeverity.Warning] },
            Token);

        seulsAvertissements.Items.ShouldHaveSingleItem().Status.ShouldBe(404);
    }

    [Fact]
    public async Task Un_ensemble_vide_ou_complet_ne_filtre_rien()
    {
        _store.Record(Request("GET", "/api/ok", 200));
        _store.Record(Request("POST", "/api/casse", 500));

        await _store.FlushAsync(Token);

        (await _store.QueryAsync(new LogQuery(), Token)).TotalItems.ShouldBe(2);

        (await _store.QueryAsync(new LogQuery { Levels = LogSeverities.All }, Token))
            .TotalItems.ShouldBe(2);
    }

    [Fact]
    public async Task La_recherche_porte_sur_le_message_et_sur_l_url()
    {
        _store.Record(Request("GET", "/api/collections/posts/records", 200));
        _store.Record(Request("GET", "/api/collections/users/records", 200));

        await _store.FlushAsync(Token);

        var trouve = await _store.QueryAsync(new LogQuery { Search = "posts" }, Token);

        trouve.Items.ShouldHaveSingleItem().Url.ShouldContain("posts");
    }

    [Fact]
    public async Task Les_jokers_de_la_recherche_sont_echappes()
    {
        _store.Record(Request("GET", "/api/collections", 200));

        await _store.FlushAsync(Token);

        // Sans échappement, « %_% » ramènerait toutes les lignes : c'est la façon la plus discrète
        // de faire croire qu'une recherche a trouvé quelque chose.
        var page = await _store.QueryAsync(new LogQuery { Search = "%_%" }, Token);

        page.TotalItems.ShouldBe(0);
    }

    [Fact]
    public async Task La_fenetre_temporelle_borne_la_page_et_l_histogramme()
    {
        var maintenant = _clock.UtcNow;

        _store.Record(Request("GET", "/api/recent", 200, maintenant));
        _store.Record(Request("GET", "/api/ancien", 200, maintenant.AddDays(-3)));

        await _store.FlushAsync(Token);

        var recentes = await _store.QueryAsync(new LogQuery { From = maintenant.AddHours(-1) }, Token);

        recentes.Items.ShouldHaveSingleItem().Url.ShouldBe("/api/recent");

        var tranches = await _store.StatsAsync(
            new LogQuery { From = maintenant.AddHours(-1) },
            LogGranularity.Hour,
            Token);

        tranches.ShouldHaveSingleItem().Count.ShouldBe(1);
    }

    [Fact]
    public async Task L_histogramme_regroupe_par_tranche_et_par_niveau()
    {
        var heure = _clock.UtcNow;

        _store.Record(Request("GET", "/api/a", 200, heure));
        _store.Record(Request("GET", "/api/b", 200, heure.AddMinutes(10)));
        _store.Record(Request("GET", "/api/c", 500, heure.AddMinutes(20)));

        await _store.FlushAsync(Token);

        var tranches = await _store.StatsAsync(new LogQuery(), LogGranularity.Hour, Token);

        tranches.Count.ShouldBe(2);
        tranches.Single(bucket => bucket.Level == LogSeverity.Info).Count.ShouldBe(2);
        tranches.Single(bucket => bucket.Level == LogSeverity.Error).Count.ShouldBe(1);

        // La tranche horaire est le préfixe canonique : mêmes treize caractères des deux côtés.
        tranches[0].Bucket.ShouldBe("2026-08-13T14");
    }

    [Fact]
    public async Task La_purge_supprime_au_dela_de_la_retention()
    {
        _store.Record(Request("GET", "/api/vieux", 200, _clock.UtcNow.AddDays(-10)));
        _store.Record(Request("GET", "/api/frais", 200, _clock.UtcNow));

        await _store.FlushAsync(Token);

        (await _store.PurgeAsync(7, Token)).ShouldBe(1);
        (await _store.CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task Une_retention_nulle_ne_supprime_rien()
    {
        _store.Record(Request("GET", "/api/vieux", 200, _clock.UtcNow.AddDays(-400)));

        await _store.FlushAsync(Token);

        // Zéro veut dire « conserver sans limite ». Le traiter comme une coupure au présent viderait
        // le journal à chaque passage du service d'entretien.
        (await _store.PurgeAsync(0, Token)).ShouldBe(0);
        (await _store.CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task Vider_le_journal_emporte_aussi_le_tampon()
    {
        _store.Record(Request("GET", "/api/ecrit", 200));
        await _store.FlushAsync(Token);

        _store.Record(Request("GET", "/api/en-attente", 200));

        (await _store.ClearAsync(Token)).ShouldBe(1);
        await _store.FlushAsync(Token);

        // Sans purge du tampon, l'entrée en attente réapparaîtrait quelques secondes après un
        // vidage que l'administrateur croit terminé.
        (await _store.CountAsync(Token)).ShouldBe(0);
    }

    [Fact]
    public async Task La_pagination_rend_les_plus_recentes_d_abord()
    {
        for (var index = 0; index < 5; index += 1)
        {
            _store.Record(Request("GET", $"/api/{index}", 200, _clock.UtcNow.AddMinutes(index)));
        }

        await _store.FlushAsync(Token);

        var page = await _store.QueryAsync(new LogQuery { PerPage = 2 }, Token);

        page.TotalItems.ShouldBe(5);
        page.TotalPages.ShouldBe(3);
        page.Items.Select(entry => entry.Url).ShouldBe(["/api/4", "/api/3"]);

        var ascendant = await _store.QueryAsync(new LogQuery { PerPage = 2, Ascending = true }, Token);

        ascendant.Items.Select(entry => entry.Url).ShouldBe(["/api/0", "/api/1"]);
    }

    [Fact]
    public async Task Les_details_libres_survivent_a_l_aller_retour()
    {
        _store.Record(
            LogSeverity.Error,
            "Réglages illisibles",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["exception"] = "JsonException",
                ["tentatives"] = 3,
            });

        await _store.FlushAsync(Token);

        var entry = (await _store.QueryAsync(new LogQuery(), Token)).Items.ShouldHaveSingleItem();

        entry.Message.ShouldBe("Réglages illisibles");
        entry.Data["exception"]?.ToString().ShouldBe("JsonException");
        entry.Data["tentatives"]?.ToString().ShouldBe("3");

        // Une entrée d'application ne répond à aucune requête : ni méthode, ni statut, et surtout
        // pas de NULL qui se propagerait dans les filtres.
        entry.Method.ShouldBe(string.Empty);
        entry.Status.ShouldBe(0);
    }
}
