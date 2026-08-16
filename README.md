# Cratebase

**Français** · [English](./README.en.md)

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![React](https://img.shields.io/badge/React-19-61DAFB?logo=react&logoColor=white)
![SQLite](https://img.shields.io/badge/SQLite-embarqué-003B57?logo=sqlite&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-supporté-4169E1?logo=postgresql&logoColor=white)
![Statut](https://img.shields.io/badge/statut-développement%20actif-orange)

Backend applicatif open source en **ASP.NET Core 10**, livré en un seul conteneur : collections
définies à l'exécution, authentification complète, fichiers, temps réel et console
d'administration React — le tout utilisable comme serveur autonome ou comme librairie dans une
application existante.

## Pourquoi Cratebase

Cratebase reprend le modèle de [PocketBase](https://pocketbase.io), backend « tout-en-un » très
apprécié pour démarrer vite. Mais PocketBase est indissociable de SQLite : le jour où
l'application dépasse ce qu'un fichier encaisse, il n'y a pas de migration, il y a une réécriture.

**Cratebase existe pour que ce jour-là ne coûte rien.** SQLite au démarrage, PostgreSQL à
l'échelle, disque local ou S3 pour les fichiers — le même code applicatif tourne sur les deux,
prouvé par une suite de tests exécutée sur chaque combinaison. Le détail de cette architecture et
la correspondance complète avec PocketBase sont dans [`CONCEPTION.md`](./CONCEPTION.md).

> [!WARNING]
> **Développement actif.** Les paquets publiés (`dotnet new cratebase`, `@cratebase/client`) ne
> sont pas encore disponibles. La sauvegarde physique depuis la console n'est pas prévue (elle
> serait spécifique à chaque moteur) ; l'export logique est planifié — voir
> [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md) et [`docs/MIGRATION.md`](./docs/MIGRATION.md).
> Tout le reste décrit ci-dessous existe et fonctionne.

## Sommaire

- [Fonctionnalités](#fonctionnalités)
- [Démarrage rapide](#démarrage-rapide)
- [La console d'administration](#la-console-dadministration)
- [Utiliser Cratebase comme librairie](#utiliser-cratebase-comme-librairie)
- [Configuration](#configuration)
- [Conteneur Docker](#conteneur-docker)
- [Tests](#tests)
- [Structure du projet](#structure-du-projet)

## Fonctionnalités

- **Collections dynamiques** — définies à l'exécution, CRUD automatique et règles d'accès par
  collection, sans code généré.
- **SQLite ou PostgreSQL**, au choix par simple chaîne de connexion.
- **Authentification** — comptes locaux, jetons révocables, rôles et dérogations, OAuth2 (Google,
  Facebook, Microsoft, GitHub), double authentification TOTP.
- **Fichiers** — disque local ou S3 (MinIO, R2, B2), vignettes générées, fichiers protégés.
- **Temps réel** — flux SSE par collection, règles d'accès réévaluées à chaque diffusion.
- **Console d'administration React** — collections, fichiers, journaux, réglages d'instance.
- **API REST** utilisable depuis n'importe quel client.
- **Librairie ou serveur** — s'ajoute à une application ASP.NET Core existante ou tourne seule.

## Démarrage rapide

```bash
dotnet run --project src/Cratebase.App --urls http://localhost:8090
```

L'API et la console sont servies sur http://localhost:8090. Pour travailler sur la console avec
rechargement à chaud, dans un second terminal :

```bash
cd web/admin && npm install && npm run dev
```

Le premier super-admin est créé au démarrage **uniquement si la base n'en contient aucun** :

```bash
Cratebase__Superuser__Email=admin@exemple.fr \
Cratebase__Superuser__Password=un-mot-de-passe-solide \
dotnet run --project src/Cratebase.App
```

En développement, `appsettings.Development.json` en pose déjà un (`admin@cratebase.local`).

> [!CAUTION]
> Ne jamais écrire de mot de passe dans un fichier versionné en production : toujours passer par
> les variables d'environnement.

## La console d'administration

Quatre destinations, dans une colonne repliable en rail d'icônes :

- **Collections** — enregistrements, comptes, schéma et règles d'accès de chaque collection.
- **Fichiers** — inventaire du magasin : ce qui est stocké, quel enregistrement le référence, et
  ce qui ne sert plus. Les objets orphelins sont isolables et supprimables.
- **Journaux** — requêtes servies, refus d'accès et évènements d'administration, avec filtres et
  histogramme cliquable pour affiner la période.
- **Administration** — aperçu de l'instance, paramètres, stockage, super-admins, fournisseurs
  d'identité.

**Administration → Stockage** éprouve la configuration du magasin de fichiers (écriture,
relecture, URL signée, suppression) sans jamais l'écrire : ce qui porte un secret reste en
configuration d'hôte.

Deux garanties tenues par le moteur, pas par l'interface :

- le **dernier super-admin** ne peut pas être supprimé ;
- **changer un mot de passe ferme toutes les sessions ouvertes** de ce compte, y compris celles
  d'un voleur.

## Utiliser Cratebase comme librairie

Cratebase s'ajoute à une application ASP.NET Core existante, sans renoncer à ses propres
endpoints :

```csharp
builder.AddCratebase(o => o.UseSqlite("Data Source=./data/cratebase.db"));

var app = builder.Build();

app.UseCratebaseAuthentication();    // résout le jeton — avant tout endpoint
app.MapCratebase();                  // /api/collections, /api/me, /api/health, la console
app.MapGet("/api/rapport", ...);     // vos endpoints, même ICurrentUser, même base

await app.Services.InitializeCratebaseAsync();
await app.RunAsync();
```

Pour intervenir sur les écritures, un point d'extension unique :

```csharp
builder.Services.AddSingleton<IRecordMutationHook, MonCrochet>();
```

L'ordre est imposé : **validation → crochets → règle de création**. Un crochet voit donc des
données déjà validées, et la règle d'accès voit l'enregistrement tel qu'il sera écrit.

## Configuration

Tout passe par la configuration ASP.NET Core, donc par variables d'environnement en production
(double souligné pour la hiérarchie, ex. `Cratebase__Superuser__Email`).

| Clé | Effet |
| --- | --- |
| `ConnectionStrings:Postgres` | Bascule sur PostgreSQL. Sans elle, SQLite. |
| `Cratebase:DataDirectory` | Racine des données : base et fichiers. |
| `Cratebase:Superuser:Email` / `Password` | Premier super-admin, si la base n'en a aucun. |
| `Cratebase:S3:Bucket` / `AccessKey` / `SecretKey` / `Endpoint` | Bascule sur S3. Sans elles, disque local. |
| `Cratebase:S3:PublicEndpoint` | Hôte vu par le navigateur, s'il diffère de celui vu par l'API. |
| `Cratebase:OAuth2:{google\|facebook\|microsoft\|github}:ClientId` / `ClientSecret` | Active un fournisseur externe. |

Le reste (nom de l'instance, URL publique, journalisation, rétention) se règle depuis la console
**Administration → Paramètres** et vit dans la base : modifiable à chaud, sans redéploiement.

> [!WARNING]
> `PublicEndpoint` n'est pas une commodité. Une URL présignée est signée *pour un hôte donné* : si
> l'API signe pour `http://minio:9000` et que le navigateur appelle
> `https://fichiers.exemple.fr`, **tous les liens sont rejetés**. C'est le piège le plus coûteux
> du stockage objet.

## Conteneur Docker

```bash
docker compose up --build
```

Une image, un volume. Passer à PostgreSQL revient à décommenter le service dans `compose.yaml` et
à poser `ConnectionStrings__Postgres` — l'image ne change pas.

## Tests

```bash
dotnet test Cratebase.slnx
```

191 tests unitaires : dialectes, langage de filtre, planificateur de schéma, journal, réglages.

```bash
pwsh tests/smoke.ps1
```

145 assertions contre une instance en cours d'exécution, chemin nominal **et** chemins hostiles :
injection, champ hors schéma, tri non autorisé, règle verrouillée, élévation de privilèges à
l'inscription, rejeu d'un défi TOTP, traversée de répertoire.

```bash
pwsh tests/postgres.ps1   # même suite, rejouée sur PostgreSQL 18
pwsh tests/minio.ps1      # même suite, rejouée sur MinIO
```

Docker requis pour ces deux derniers scripts.

## Structure du projet

```text
src/
  Cratebase.Core            abstractions — zéro dépendance externe
  Cratebase.Expressions     langage de filtre — produit un arbre, jamais du SQL
  Cratebase.Data{,.Sqlite,.Postgres}
                             dialecte SQL, compilation, transactions
  Cratebase.Schema          collections dynamiques, DDL, migrations
  Cratebase.Records         moteur CRUD, règles, crochets de mutation
  Cratebase.Auth            comptes, jetons révocables, RBAC, OAuth2, TOTP
  Cratebase.Admin           journal des requêtes, réglages d'instance
  Cratebase.Storage{,.S3}   IObjectStore, disque local, vignettes SkiaSharp
  Cratebase.Server          câblage DI et endpoints — AddCratebase() / MapCratebase()
  Cratebase.App             l'application exécutable
web/admin/                   console d'administration React-TS
tests/                       tests unitaires, smoke.ps1, postgres.ps1, minio.ps1
CONCEPTION.md                conception détaillée et correspondance avec PocketBase
```

## Documentation complémentaire

- [`CONCEPTION.md`](./CONCEPTION.md) — architecture détaillée, arbitrages et catalogue des
  défaillances silencieuses évitées.
- [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md) — export logique (planifié).
- [`docs/MIGRATION.md`](./docs/MIGRATION.md) — déplacer une instance déjà peuplée.
