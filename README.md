# Cratebase

**Français** · [English](./README.en.md)

Cratebase est un backend applicatif open source en ASP.NET Core 10, livré en **un seul conteneur**,
qui contient :

- des **collections définies à l'exécution**, avec CRUD automatique et règles d'accès par collection ;
- une base **SQLite embarquée — ou PostgreSQL**, au choix par configuration ;
- l'**authentification** : comptes locaux, jetons révocables, rôles et dérogations, OAuth2 (Google,
  Facebook, Microsoft, GitHub), double authentification TOTP ;
- les **fichiers** sur disque local — ou S3 —, vignettes et fichiers protégés compris ;
- une **console d'administration** React : collections, enregistrements, journaux, réglages ;
- une **API REST** utilisable depuis n'importe quel client.

> Conception détaillée, correspondance avec PocketBase et catalogue des défaillances silencieuses :
> [`CONCEPTION.md`](./CONCEPTION.md).

> ⚠️ **Développement actif.** Le temps réel (SSE) et les paquets publiés (`dotnet new cratebase`,
> `@cratebase/client`) ne sont pas encore là. La sauvegarde **physique** depuis la console est
> écartée et non repoussée : elle serait forcément spécifique au moteur (`VACUUM INTO` d'un côté,
> `pg_dump` de l'autre). L'**export logique**, lui, est planifié —
> [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md), et [`docs/MIGRATION.md`](./docs/MIGRATION.md) pour
> déplacer une instance déjà peuplée. Tout le reste ci-dessus existe.

## Pourquoi pas PocketBase ?

[PocketBase](https://pocketbase.io) est excellent, et Cratebase lui reprend son modèle. La
différence tient en une phrase : **Cratebase se migre, PocketBase se remplace.**

PocketBase est indissociable de SQLite. Le jour où l'application dépasse ce qu'un fichier encaisse —
plusieurs écrivains, plusieurs instances, une base gérée par l'hébergeur — il n'y a pas de
migration : il y a une réécriture, et les clients déjà déployés la subissent.

Cratebase est écrit pour que ce jour-là ne coûte rien :

| Axe | Départ | À l'échelle | Ce qu'on change |
| --- | --- | --- | --- |
| Base | SQLite (fichier) | PostgreSQL | une chaîne de connexion |
| Fichiers | disque local | S3, MinIO, R2, B2 | quatre variables |
| Instances | une | N derrière un répartiteur | rien |

La même suite de bout en bout est jouée sur SQLite comme sur PostgreSQL, sur disque local comme sur
S3.

En contrepartie, PocketBase a ce que Cratebase n'a pas : le temps réel, les sauvegardes depuis la
console, un binaire unique à télécharger et un écosystème de SDK déjà publiés.

## Démarrer

```bash
dotnet run --project src/Cratebase.App --urls http://localhost:8090
```

L'API et la console sont sur http://localhost:8090. Pour travailler sur la console avec le
rechargement à chaud, dans un second terminal :

```bash
cd web/admin && npm install && npm run dev
```

Le premier superadministrateur est créé au démarrage **uniquement si la base n'en contient aucun**,
depuis la configuration :

```bash
Cratebase__Superuser__Email=admin@exemple.fr Cratebase__Superuser__Password=un-mot-de-passe-solide dotnet run --project src/Cratebase.App
```

En développement, `appsettings.Development.json` en pose déjà un (`admin@cratebase.local`). **Ne
jamais écrire de mot de passe dans un fichier versionné en production** : passer par les variables
d'environnement.

## La console

Trois destinations, dans une colonne repliable en rail d'icônes :

- **Collections** — enregistrements, comptes, schéma et règles d'accès de chaque collection.
- **Fichiers** — inventaire du magasin : ce qui est stocké, quel enregistrement le référence, et ce
  qui ne sert plus. Les objets orphelins sont isolables et supprimables ; les fichiers encore
  référencés ne le sont pas depuis là.
- **Journaux** — requêtes servies, refus d'accès et évènements d'administration. Les filtres sont
  posés sous l'en-tête de la colonne qu'ils restreignent ; cliquer une barre de l'histogramme ouvre
  la tranche correspondante et la redécoupe d'un cran plus fin.
- **Administration** — aperçu de l'instance, paramètres, stockage, superadministrateurs,
  fournisseurs d'identité.

**Administration → Stockage** montre la configuration du magasin et l'éprouve — écriture, relecture,
URL signée réellement suivie, suppression — mais ne l'écrit pas : ce qui porte un secret reste en
configuration d'hôte. L'écran donne les variables exactes à poser et dit si elles sont bonnes.

Deux garanties tenues par le moteur et non par l'interface : **le dernier superadministrateur ne
peut pas être supprimé** — sans lui l'instance n'est plus administrable par personne — et **changer
un mot de passe ferme les sessions ouvertes** de ce compte, y compris celles d'un voleur.

## Depuis votre application

Cratebase est aussi une librairie : on l'ajoute à une application ASP.NET Core existante et on
continue d'écrire ses propres endpoints.

```csharp
builder.AddCratebase(o => o.UseSqlite("Data Source=./data/cratebase.db"));

var app = builder.Build();

app.UseCratebaseAuthentication();    // résout le jeton — avant tout endpoint
app.MapCratebase();                  // /api/collections, /api/me, /api/health, la console
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

## Configuration

Tout passe par la configuration ASP.NET Core, donc par variables d'environnement en production
(`Cratebase__Superuser__Email` — double souligné pour la hiérarchie).

| Clé | Effet |
| --- | --- |
| `ConnectionStrings:Postgres` | Bascule sur PostgreSQL. Sans elle, SQLite. |
| `Cratebase:DataDirectory` | Racine des données : base et fichiers. |
| `Cratebase:Superuser:Email` / `Password` | Premier superadministrateur, si la base n'en a aucun. |
| `Cratebase:S3:Bucket` / `AccessKey` / `SecretKey` / `Endpoint` | Bascule sur S3. Sans elles, disque local. |
| `Cratebase:S3:PublicEndpoint` | Hôte vu par le **navigateur**, s'il diffère de celui vu par l'API. |
| `Cratebase:OAuth2:{google\|facebook\|microsoft\|github}:ClientId` / `ClientSecret` | Active un fournisseur externe. |

Le reste — nom de l'instance, URL publique, journalisation, rétention — se règle depuis la console
(**Administration → Paramètres**) et vit dans la base : ce qui porte un secret ou nomme un moteur
reste en configuration d'hôte, le reste est modifiable à chaud.

> ⚠️ `PublicEndpoint` n'est pas une commodité. Une URL présignée est signée *pour un hôte donné* :
> si l'API signe pour `http://minio:9000` et que le navigateur appelle `https://fichiers.exemple.fr`,
> **tous les liens sont rejetés**. C'est le piège le plus coûteux du stockage objet.

## Conteneur

```bash
docker compose up --build
```

Une image, un volume. Passer à PostgreSQL revient à décommenter le service dans `compose.yaml` et à
poser `ConnectionStrings__Postgres` — l'image ne change pas.

## Vérifier

```bash
dotnet test Cratebase.slnx
```

191 tests unitaires : dialectes, langage de filtre, planificateur de schéma, journal, réglages.

```bash
pwsh tests/smoke.ps1
```

124 assertions contre une instance en cours d'exécution. Elles couvrent le chemin nominal **et** les
chemins hostiles : injection, champ hors schéma, tri non autorisé, règle verrouillée, élévation de
privilèges à l'inscription, rejeu d'un défi de double authentification, traversée de répertoire.

```bash
pwsh tests/postgres.ps1
pwsh tests/minio.ps1
```

La même suite, rejouée sur PostgreSQL 18 puis sur MinIO, chacun démarré en conteneur. Docker requis.

<details>
<summary><b>Organisation du dépôt</b></summary>

```
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
web/admin/                  console d'administration React-TS
tests/                      tests unitaires, smoke.ps1, postgres.ps1, minio.ps1
CONCEPTION.md               le plan
```

</details>
