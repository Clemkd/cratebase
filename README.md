# Cratebase

Backend applicatif complet en ASP.NET Core 10, livré en **un seul conteneur** : collections
définies à l'exécution, CRUD automatique, règles d'accès par collection, console d'administration
React — et, à venir, authentification, fichiers et temps réel.

Inspiré de [PocketBase](https://pocketbase.io), avec une différence qui en justifie l'existence :
**Cratebase se migre au lieu de se remplacer.** SQLite → PostgreSQL, disque local → S3, bus mémoire
→ `LISTEN`/`NOTIFY` : par configuration, sans toucher au code applicatif, aux collections ni aux
clients.

> Conception détaillée : [`CONCEPTION.md`](./CONCEPTION.md).

## État

| Jalon | Contenu | État |
| :-: | --- | :-: |
| 0 | Chaîne complète : solution, SPA servie par ASP.NET Core, SQLite, Docker | ✅ |
| 1 | Types logiques, abstraction de dialecte, **SQLite et PostgreSQL écrits ensemble** | ✅ |
| 2 | Collections dynamiques : DDL, index, renommage préservant les données | ✅ |
| 3 | Langage de filtre : lexeur, parseur, compilateur AST→SQL | ✅ |
| 4 | CRUD : list/view/create/update/delete, tri, pagination, projection | ✅ |
| 5 | Comptes locaux, jetons révocables, RBAC (rôles + dérogations) | ✅ |
| 5b | OAuth2 externes (Google, Facebook, Microsoft, GitHub), MFA/TOTP | ✅ |
| 6 | Fichiers : stockage local **et S3**, vignettes, fichiers protégés | ✅ |
| 7 | Console d'administration React-TS | ✅ |
| 8 | Temps réel : SSE, abonnements | ⏳ hors périmètre initial |
| 9 | Paquets NuGet, `dotnet new cratebase`, `@cratebase/client` | ⏳ |
| 10 | **Bascules PostgreSQL et S3 exercées de bout en bout** | ✅ |

**152 tests unitaires + 91 tests de bout en bout.** Ces 91 assertions passent **à l'identique dans
trois configurations** :

| Configuration | Base | Fichiers | Résultat |
| --- | --- | --- | :-: |
| Départ | SQLite | disque local | 91 / 91 |
| Base à l'échelle | **PostgreSQL 18** | disque local | 91 / 91 |
| Stockage à l'échelle | SQLite | **MinIO (S3)** | 91 / 91 |

C'est la seule preuve qui vaille pour la promesse d'évolutivité : non pas que le code « devrait »
être portable, mais que la même suite passe partout. Le script MinIO vérifie en plus que les octets
sont bien **dans le seau** et qu'aucun repli silencieux sur le disque n'a eu lieu — sans quoi la
suite passerait pour la mauvaise raison.

> **Open.IdentityServer n'est pas intégré**, et c'est un choix documenté. Ce qui motivait ce choix
> — révocation réelle, comptes locaux, Google/Facebook, MFA — est acquis autrement : jetons opaques
> révocables, flot OAuth2 « code d'autorisation » implémenté côté serveur, TOTP conforme RFC 6238.
> Ce qu'il apporterait encore : faire de Cratebase **une autorité OIDC** pour des applications
> tierces qu'on n'écrit pas soi-même. Besoin réel, mais distinct, et qui s'ajoute sans rien défaire.
> Voir `CONCEPTION.md` §2.5.

## Démarrer

```bash
dotnet run --project src/Cratebase.App --urls http://localhost:8090
```

Dans un second terminal, pour la console d'administration en développement :

```bash
cd web/admin && npm install && npm run dev
```

La console est sur http://localhost:5173, l'API sur http://localhost:8090.

### Premier superadministrateur

Il est créé au démarrage **uniquement si la base n'en contient aucun**, depuis la configuration :

```bash
Cratebase__Superuser__Email=admin@exemple.fr Cratebase__Superuser__Password=un-mot-de-passe-solide dotnet run --project src/Cratebase.App
```

En développement, `appsettings.Development.json` en pose déjà un
(`admin@cratebase.local`). **Ne jamais écrire de mot de passe dans un fichier versionné en
production** : passer par les variables d'environnement.

## Vérifier

```bash
dotnet test Cratebase.slnx
```

```bash
pwsh tests/smoke.ps1
```

Le test de bout en bout exige une instance en cours d'exécution. Il couvre le chemin nominal
**et** les chemins hostiles : injection, champ hors schéma, tri non autorisé, suppression hors
périmètre, règle verrouillée, renommage de champ, élévation de privilèges à l'inscription, rejeu
d'un défi de double authentification, traversée de répertoire sur les fichiers.

```bash
pwsh tests/postgres.ps1
```

Démarre PostgreSQL 18 en conteneur, remet la base à neuf, relance l'application dessus et rejoue
**la même suite**. Docker requis. C'est ce script qui garde la promesse honnête : le jour où il
casse, « passer à PostgreSQL » a cessé d'être un changement de configuration.

```bash
pwsh tests/minio.ps1
```

Même chose pour le stockage objet : MinIO en conteneur, la même suite, plus un contrôle que les
octets sont bien dans le seau.

## Configuration

Tout passe par la configuration ASP.NET Core — donc par variables d'environnement en production
(`Cratebase__Superuser__Email`, double souligné pour la hiérarchie).

| Clé | Effet |
| --- | --- |
| `ConnectionStrings:Postgres` | Bascule sur PostgreSQL. Sans elle, SQLite. |
| `Cratebase:DataDirectory` | Racine des données : base, fichiers, sauvegardes. |
| `Cratebase:Superuser:Email` / `Password` | Premier superadministrateur, si la base n'en a aucun. |
| `Cratebase:S3:Bucket` / `AccessKey` / `SecretKey` / `Endpoint` | Bascule sur S3. Sans elles, disque local. |
| `Cratebase:S3:PublicEndpoint` | Hôte vu par le **navigateur**, s'il diffère de celui vu par l'API. |
| `Cratebase:OAuth2:{google\|facebook\|microsoft\|github}:ClientId` / `ClientSecret` | Active un fournisseur externe. |

> ⚠️ `PublicEndpoint` n'est pas une commodité. Une URL présignée est signée *pour un hôte donné* :
> si l'API signe pour `http://minio:9000` et que le navigateur appelle `https://fichiers.exemple.fr`,
> **tous les liens sont rejetés**. C'est le piège le plus coûteux du stockage objet.

## Conteneur

```bash
docker compose up --build
```

Une image, un volume. Passer à PostgreSQL revient à décommenter le service dans `compose.yaml` et à
poser `ConnectionStrings__Postgres` — l'image ne change pas.

## Organisation

```
src/
  Cratebase.Core            abstractions — zéro dépendance externe
  Cratebase.Expressions     langage de filtre — produit un arbre, jamais du SQL
  Cratebase.Data            dialecte SQL, compilation, transactions
    ├── .Sqlite
    └── .Postgres
  Cratebase.Schema          collections dynamiques, DDL, migrations
  Cratebase.Records         moteur CRUD, application des règles, crochets de mutation
  Cratebase.Auth            comptes, mots de passe, jetons révocables, RBAC, OAuth2, TOTP
  Cratebase.Storage         IObjectStore, disque local, vignettes SkiaSharp
    └── .S3                 MinIO, Garage, R2, B2, AWS — double client interne/public
  Cratebase.Server          câblage DI, endpoints — AddCratebase() / MapCratebase()
  Cratebase.App             l'application exécutable
web/admin/                  console d'administration React-TS
tests/                      tests unitaires + smoke.ps1
CONCEPTION.md               le plan
```

## Utiliser Cratebase comme librairie

```csharp
builder.AddCratebase(o => o.UseSqlite("Data Source=./data/cratebase.db"));

var app = builder.Build();

app.UseCratebaseAuthentication();    // résout le jeton — avant tout endpoint
app.MapCratebase();                  // /api/collections, /api/me, /api/health, la SPA
app.MapGet("/api/rapport", ...);     // vos endpoints, même ICurrentUser, même base

await app.Services.InitializeCratebaseAsync();
await app.RunAsync();
```

Pour intervenir sur les écritures, implémenter `IRecordMutationHook` et l'enregistrer :

```csharp
builder.Services.AddSingleton<IRecordMutationHook, MonCrochet>();
```

L'ordre est imposé : **validation, puis crochets, puis règle de création**. Un crochet voit donc des
données déjà validées, et la règle d'accès voit l'enregistrement tel qu'il sera écrit.

## Les règles qu'on ne négocie pas

Détaillées au §1.2 de `CONCEPTION.md` ; en résumé :

1. **Aucun SQL de dialecte** hors de `Cratebase.Data.*`.
2. **Le langage de filtre n'expose aucune fonction du moteur** — c'est ce qui enferme PocketBase
   dans SQLite via `strftime()`. Les macros de date sont résolues côté application et deviennent des
   paramètres.
3. **Sémantique identique prouvée par exécution**, pas par intention.
4. **Pas d'hypothèse d'écrivain unique.**
5. **Le schéma est une donnée**, donc il est portable.
