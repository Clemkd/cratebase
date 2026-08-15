# Cratebase — conception

Un « PocketBase en ASP.NET Core » : backend applicatif complet, livré en **un seul conteneur**,
avec collections définies à l'exécution, CRUD automatique, règles d'accès par collection,
authentification, fichiers, temps réel et console d'administration.

Document de conception. Voisin de [`STACK-CONCEPTION.md`](../STACK-CONCEPTION.md) — dont il reprend
la doctrine et le catalogue des défaillances silencieuses — mais **produit différent** : Forge
expose des entités typées déclarées à la compilation, Cratebase expose des collections déclarées à
l'exécution. Les deux peuvent coexister ; ils ne répondent pas à la même question.

**Nom.** `Cratebase` — libre sur NuGet et npm, quasi absent de GitHub, même construction que
PocketBase (contenant + base). Tous les identifiants de ce document en dérivent
(`Cratebase.Core`, `@cratebase/client`). Un `find`/`replace` suffit à en changer tant que rien
n'est publié.

---

## 1. La contrainte première : grandir sans changer de stack

C'est l'exigence structurante, celle qui prime sur toutes les autres et qui décide de la plupart des
arbitrages qui suivent.

> Quand le projet prend de l'ampleur, **rien n'est à remplacer**. On sort SQLite pour PostgreSQL, le
> disque local pour S3, le bus mémoire pour `LISTEN`/`NOTIFY`. Le code applicatif, les collections,
> les règles, les clients TypeScript : inchangés.

C'est précisément ce que PocketBase **ne** sait pas faire. PocketBase est indissociable de SQLite :
son langage de filtre expose `strftime()`, ses règles reposent sur `json_extract`, son moteur écrit
du SQL SQLite en dur. Le jour où l'application dépasse ce que SQLite encaisse, il n'y a pas de
migration — il y a une réécriture. **C'est le défaut que Cratebase existe pour corriger**, et c'est
ce qui justifie de l'écrire plutôt que d'utiliser PocketBase.

### 1.1 Les quatre axes de croissance

| Axe | Départ (un conteneur) | Cible (ampleur) | Ce qui rend la bascule indolore |
| --- | --- | --- | --- |
| **Base** | SQLite (fichier) | PostgreSQL (externe) | `ISqlDialect` + suite de conformité exécutée sur les deux |
| **Fichiers** | disque local | S3 / MinIO / R2 | `IObjectStore`, URLs signées des deux côtés |
| **Temps réel** | bus mémoire | `LISTEN`/`NOTIFY` | `IRealtimeTransport`, canaux nommés identiques |
| **Instances** | 1 | N derrière un répartiteur | aucun état en mémoire de processus qui fasse autorité |

### 1.2 Les cinq règles qui en découlent, et qu'on ne négocie pas

**R1 — Aucun SQL de dialecte hors des paquets `Cratebase.Data.*`.**
Le moteur de requêtes ne produit jamais de chaîne SQL : il produit un arbre. Le dialecte le compile.
Un `grep` de `SELECT `/`json_extract`/`strftime` hors de `Data.Sqlite`/`Data.Postgres` doit revenir
vide, et un test d'architecture le vérifie.

**R2 — Le langage de filtre n'expose aucune fonction du moteur.**
PocketBase expose `strftime(...)` : c'est une fuite d'implémentation qui interdit la migration.
Cratebase expose des fonctions **logiques** (`dateTrunc`, `distance`, `lower`, `length`) que chaque
dialecte traduit. La grammaire est la même partout, le SQL produit ne l'est pas.

**R3 — Sémantique identique, prouvée par exécution, pas par intention.**
Tri des chaînes, casse du `LIKE`, comparaison de dates, `NULL` vs chaîne vide, arrondi des flottants :
tout cela diverge entre SQLite et PostgreSQL, **silencieusement**. Une suite de conformité unique
tourne sur les deux moteurs et échoue à la moindre divergence. Sans elle, la promesse d'évolutivité
est un vœu — c'est la leçon du §2.2 de `STACK-CONCEPTION.md`, appliquée ici au cas le plus exposé.

**R4 — Pas d'hypothèse d'écrivain unique.**
SQLite en WAL n'admet qu'un écrivain ; PostgreSQL en admet beaucoup. Le code est écrit pour le
second et **contraint** pour le premier : transactions explicites et courtes, reprise sur
`SQLITE_BUSY`, aucun `last_insert_rowid`, aucun compteur en mémoire de processus, identifiants
générés côté application.

**R5 — Le schéma est une donnée, donc il est portable.**
Les collections sont décrites dans des tables système. Migrer de moteur, c'est relire cette
description et régénérer le DDL pour la cible, puis pomper les lignes. C'est l'avantage structurel du
modèle à collections dynamiques, et il faut le tenir : `cratebase migrate-provider` est un outil
livré et testé, pas une note de documentation.

---

## 2. Correspondance PocketBase → Cratebase

Relevé exhaustif des fonctionnalités de PocketBase, avec ce que Cratebase en fait.

### 2.1 Collections

| PocketBase | Cratebase | Note |
| --- | --- | --- |
| Collection `base` | ✅ identique | table physique réelle, pas un blob JSON |
| Collection `auth` | ✅ identique | plusieurs collections d'auth possibles |
| Collection `view` | ✅ | mais définie par une **requête Cratebase**, pas par du SQL brut — sinon R1 tombe |
| Champs système `id`, `created`, `updated` | ✅ | `id` = UUIDv7 (triable, standard) au lieu des 15 caractères aléatoires |
| Champs système d'auth (`email`, `emailVisibility`, `verified`, `password`, `tokenKey`) | ✅ identique | |
| Index déclarés par collection | ✅ | y compris uniques et partiels |
| Suppression en cascade des relations | ✅ | appliquée par l'application **et** par la clé étrangère |

### 2.2 Types de champs

Tous repris. La colonne « stockage » est le point de portabilité — voir §5.

| Type | Options | SQLite | PostgreSQL |
| --- | --- | --- | --- |
| `text` | min, max, pattern, autogenerate | `TEXT` | `text` |
| `editor` | max (HTML assaini) | `TEXT` | `text` |
| `number` | min, max, entier seul | `REAL` / `INTEGER` | `double precision` / `bigint` |
| `bool` | — | `INTEGER` (0/1) | `boolean` |
| `email` | domaines autorisés/exclus | `TEXT` | `citext`-like via index sur `lower()` |
| `url` | domaines autorisés/exclus | `TEXT` | `text` |
| `date` | min, max | `TEXT` ISO-8601 UTC **normalisé** | `timestamptz` |
| `autodate` | à la création / à la mise à jour | idem `date` | idem `date` |
| `select` | valeurs, maxSelect | `TEXT` / `TEXT` JSON | `text` / `jsonb` |
| `file` | maxSize, maxSelect, types MIME, tailles de vignettes, protégé | `TEXT` / `TEXT` JSON | `text` / `jsonb` |
| `relation` | collection cible, maxSelect, cascade | `TEXT` / `TEXT` JSON | `text` / `jsonb` |
| `json` | maxSize | `TEXT` | `jsonb` |
| `geoPoint` | — | `TEXT` JSON | `jsonb` |

> **Révisé à l'implémentation.** Une version antérieure de ce tableau donnait `text[]` / `uuid[]`
> aux champs multi-valués côté PostgreSQL, et deux colonnes `REAL` au point géographique. Les deux
> ont été abandonnés : un tableau natif est plus rapide, mais il donne **deux représentations
> différentes à la même donnée logique**, donc deux sémantiques à faire coïncider sur le tri, la
> casse et la valeur vide — pour un gain qui n'a pas encore de bénéficiaire. Une seule forme, un
> seul comportement à prouver. De même, un champ vaut exactement une colonne : le point géographique
> sur deux colonnes cassait cette invariante, dont dépend tout le planificateur de schéma.

> ⚠️ **Le `date` est le piège numéro un de la portabilité.** PocketBase compare les dates *en tant
> que chaînes*, ce qui l'oblige à imposer le format RFC-3339 complet dans les filtres. Sur
> PostgreSQL en `timestamptz`, la comparaison est temporelle. Les deux donnent le même résultat
> **uniquement si** la représentation SQLite est normalisée sans exception : UTC, précision fixe,
> zéros de tête. La normalisation est donc faite à l'écriture par le mappeur de type, jamais laissée
> à l'appelant, et la suite de conformité compare les deux moteurs sur un jeu de dates limites.

### 2.3 Règles d'accès (le cœur du modèle)

Les six règles de PocketBase, reprises à l'identique dans leur sémantique :

| Règle | Effet d'une violation |
| --- | --- |
| `listRule` | 200 avec liste vide — **la règle est aussi un filtre** |
| `viewRule` | 404 |
| `createRule` | 400 |
| `updateRule` | 404 |
| `deleteRule` | 404 |
| `manageRule` (collections d'auth) | 403 |

Trois états, repris tels quels :

- `null` → **verrouillée** : superadmin seulement, 403 pour tout le reste. C'est le défaut.
- `""` → ouverte à tous, y compris les visiteurs anonymes.
- expression → autorisée si l'expression est vraie.

Les codes de statut différenciés ne sont pas cosmétiques : renvoyer 404 plutôt que 403 sur
`viewRule` évite de divulguer l'existence d'une ligne. C'est repris volontairement.

### 2.4 API des enregistrements

| Point | Cratebase |
| --- | --- |
| `GET /api/collections/{c}/records` | ✅ `page`, `perPage`, `sort`, `filter`, `expand`, `fields`, `skipTotal` |
| `GET /api/collections/{c}/records/{id}` | ✅ |
| `POST /api/collections/{c}/records` | ✅ |
| `PATCH /api/collections/{c}/records/{id}` | ✅ |
| `DELETE /api/collections/{c}/records/{id}` | ✅ |
| `POST /api/batch` | ✅ transactionnel, désactivé par défaut |
| `expand` jusqu'à 6 niveaux, relations inverses | ✅ **borné par configuration**, 2 par défaut |
| `fields` avec `*` et `:excerpt(n, ellipsis)` | ✅ |
| Modificateurs d'écriture `+`, `-`, `+champ` | ✅ sur `number`, `select`, `file`, `relation` |
| Enveloppe `{page, perPage, totalItems, totalPages, items}` | ✅ identique |

### 2.5 Authentification

| Fonctionnalité PocketBase | Cratebase |
| --- | --- |
| Mot de passe (identité = e-mail) | ✅ PBKDF2-SHA512, coût stocké avec le condensat |
| OAuth2 (Google, Facebook, Microsoft, GitHub) | ✅ flot « code d'autorisation » côté serveur, PKCE, préréglages fournis |
| MFA (deux facteurs enchaînés, `mfaId`) | ✅ **TOTP RFC 6238**, que PocketBase n'a pas nativement |
| OTP par e-mail | ⏳ — nécessite `IEmailSender`, non branché |
| Vérification d'e-mail, réinitialisation, changement d'e-mail | ⏳ — même dépendance |
| Usurpation par superadmin (`impersonate`) | ⏳ |
| Clés d'API | ⏳ |
| Jetons JWT HS256, sans session en base | ⚠️ **divergence assumée** — voir ci-dessous |

> **Fournisseurs externes : configuration, et non table.** Le plan initial les prévoyait en base,
> pour les modifier depuis la console. L'implémentation les prend dans la configuration : un secret
> client n'a rien à faire dans une table que la console peut lire, ni dans une sauvegarde qu'on
> copie ailleurs. Ce qui reste exposé par l'API (`GET /auth-methods`) se limite à ce dont l'écran de
> connexion a besoin pour afficher ses boutons — jamais le secret.

> **Divergence assumée : la révocation avant la fédération.** PocketBase émet des JWT HS256 maison,
> sans session : rapide, et **irrévocable** — un jeton volé reste valable jusqu'à son expiration,
> quoi qu'on fasse.
>
> **Ce qui est implémenté : des jetons opaques stockés en base**, dont seul le condensat SHA-256 est
> persisté. Coût : une lecture indexée par requête. Gain : déconnexion réelle, changement de mot de
> passe qui invalide les sessions ouvertes, retrait de droits qui prend effet immédiatement. C'est
> la propriété qui comptait dans le choix d'Open.IdentityServer, et elle est acquise sans lui.
>
> **Ce qu'Open.IdentityServer reste seul à apporter** — et qui justifie de l'ajouter au jalon 5 :
> les clients tiers, la fédération OAuth2 (Google, Facebook) et le protocole OIDC pour des
> applications qu'on n'écrit pas soi-même. Le magasin de jetons actuel n'entre pas en concurrence
> avec lui : il devient le magasin opérationnel de la session locale.
>
> `Open.IdentityServer 2.0` cible `net10.0` et ses magasins sont EF Core, donc agnostiques du
> moteur. Les migrations EF devront être générées **par fournisseur** (assemblages séparés) — coût
> connu, à provisionner.

### 2.6 Fichiers

| PocketBase | Cratebase |
| --- | --- |
| `multipart/form-data` sur create/update | ✅ |
| Nom d'origine assaini + suffixe aléatoire | ✅ |
| `GET /api/files/{collection}/{recordId}/{filename}` | ✅ |
| `?thumb=WxH`, `WxHt/b/f`, `0xH`, `Wx0` | ✅ SkiaSharp, cache sur disque/objet |
| Fichiers protégés + jeton de fichier (~2 min) | ✅ |
| `?download=1` | ✅ |
| Stockage local `pb_data/storage` ou S3 | ✅ `IObjectStore` : `LocalObjectStore` / `S3ObjectStore` |
| Modificateurs `+` / `-` sur champs multi-fichiers | ✅ |

> Pour le local, il n'y a pas d'URL présignée : le jeton de fichier joue le même rôle, et l'API sert
> l'octet. En S3, on bascule sur des URLs présignées — **avec le double client interne/public**
> repris de `instacontent` (§4.1 de `STACK-CONCEPTION.md`), sans quoi tous les liens sont rejetés
> dès que le navigateur et l'API ne voient pas le stockage à la même adresse.

### 2.7 Temps réel

| PocketBase | Cratebase |
| --- | --- |
| `GET /api/realtime` (SSE) + `PB_CONNECT` | ✅ `GET /api/realtime` + événement `connect` |
| `POST /api/realtime` pour poser les abonnements | ✅ |
| Sujets `collection/*` et `collection/{id}` | ✅ |
| `listRule` sur abonnement large, `viewRule` sur enregistrement | ✅ **règle réévaluée à chaque diffusion** |
| Actions `create` / `update` / `delete` | ✅ |
| Options sérialisées (`expand`, en-têtes) | ✅ + `filter` et `fields` |
| Déconnexion des inactifs (5 min) | ✅ + trames de maintien |

Transport enfichable dès l'origine : `InMemory` (défaut, un conteneur) et `Postgres`
(`LISTEN`/`NOTIFY`, N instances). Le canal est nommé identiquement dans les deux cas — c'est ce qui
rend la bascule invisible au client.

### 2.8 Extensibilité — Cratebase comme librairie

C'est l'équivalent du mode « framework » de PocketBase (`pocketbase.New()` + hooks Go). En .NET :

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddCratebase(o =>
{
    o.UseSqlite("Data Source=./data/cratebase.db");   // ou o.UsePostgres(cs)
    o.UseLocalFiles("./data/storage");                 // ou o.UseS3(...)
    o.Realtime.UseInMemory();                          // ou .UsePostgres()
});

// Un hook, typé, sur une collection nommée.
builder.Services.OnRecordCreating("posts", async (ctx, ct) =>
{
    ctx.Record["slug"] = Slug.From(ctx.Record.GetString("title"));
    await ctx.Next(ct);
});

var app = builder.Build();

app.MapCratebase();                 // /api/collections, /api/files, /api/realtime, /api/admin, SPA
app.MapGet("/api/report", ...);     // vos propres endpoints, même ICurrentUser, même autorisation

app.Run();
```

Le catalogue de hooks reprend celui de PocketBase, en trois familles :

| Famille | Hooks |
| --- | --- |
| **Modèle** (dans la transaction) | `OnRecordValidating`, `OnRecordCreating/Created`, `OnRecordUpdating/Updated`, `OnRecordDeleting/Deleted`, + variantes `…Failed` |
| **Requête** (autour de l'API) | `OnRecordsListRequest`, `OnRecordViewRequest`, `OnRecordCreateRequest`, `OnRecordUpdateRequest`, `OnRecordDeleteRequest`, `OnBatchRequest`, `OnFileDownloadRequest`, `OnFileTokenRequest` |
| **Auth** | `OnRecordAuthRequest`, `…WithPassword`, `…WithOAuth2`, `…WithOtp`, `…AuthRefresh`, `OnRecordRequest/ConfirmPasswordReset`, `…Verification`, `…EmailChange` |
| **Application** | `OnBootstrap`, `OnServe`, `OnSettingsReload`, `OnBackupCreate/Restore`, `OnTerminate` |
| **Collections** | `OnCollectionValidating`, `OnCollectionCreating/Created/Updating/Updated/Deleting/Deleted` |
| **Courriel** | `OnMailerSend`, `…RecordPasswordResetSend`, `…RecordVerificationSend`, `…RecordEmailChangeSend`, `…RecordOtpSend`, `…RecordAuthAlertSend` |

Chaîne de responsabilité avec `ctx.Next(ct)` et priorité explicite, comme le `e.Next()` de
PocketBase — un hook peut court-circuiter, transformer, ou envelopper.

### 2.9 Exploitation

| PocketBase | Cratebase |
| --- | --- |
| Sauvegardes ZIP de `pb_data`, locales ou S3 | ❌ **écarté** en sauvegarde physique — remplacé par un export logique, [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md) |
| Journaux de requêtes en base, avec rétention | ✅ livré — table `_logs`, écriture tamponnée, purge horaire |
| Réglages en base (JSON), chiffrement optionnel | ✅ livré — table `_settings`, une ligne JSON ; **aucun secret n'y entre** |
| Tâches planifiées (cron) | ✅ `BackgroundService` + expressions cron |
| SMTP / `sendmail` | ✅ `IEmailSender` (abstraction), impl. SMTP + impl. journal en dev |
| Limitation de débit intégrée | ✅ repris de `Forge.Http` (partition `sub` → IP, 429 + `Retry-After`) |
| Commandes CLI superadmin | ✅ `cratebase superuser create/update`, `migrate`, `migrate-provider` |
| Gestion des superadministrateurs depuis la console | ✅ livré — création, changement de mot de passe, suppression **sauf du dernier compte** |
| Répertoire `pb_data` | `./data` : `cratebase.db`, `storage/`, `backups/`, `migrations/` |

**Pourquoi les sauvegardes sont écartées et non repoussées.** PocketBase sauvegarde en zippant
`pb_data` : c'est possible parce qu'il n'existe qu'un moteur. Ici, la même fonction se scinderait en
`VACUUM INTO` d'un côté et `pg_dump` de l'autre — donc un bouton de console qui marche en
développement et échoue en production, exactement la dépendance au moteur que la règle R1 interdit.
Un `pg_dump` suppose en outre un binaire externe dans l'image, et une politique de rétention hors du
conteneur. La sauvegarde **physique** appartient à l'exploitant de la base, pas à la console : c'est
une décision, pas un manque.

Ce qui reste légitime, et qui est planifié dans [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md), c'est
l'**export logique** : une archive qui passe par le modèle de collections — définitions, lignes,
fichiers — et ne connaît aucun moteur. Elle ne remplace pas la sauvegarde de l'exploitant, dont elle
n'a ni la finesse ni le coût ; elle répond à une autre question, celle de sortir ses données et de
les remettre ailleurs. C'est d'ailleurs la seule forme d'archive qu'on puisse restaurer sur l'autre
moteur. Le déplacement d'une instance déjà peuplée d'un moteur ou d'un stockage vers un autre fait
l'objet d'un plan distinct, [`docs/MIGRATION.md`](./docs/MIGRATION.md).

**Ce que le journal enregistre — et ce qu'il n'enregistre pas.**

- **Écriture tamponnée**, jamais sur le chemin de la requête : un canal borné, vidé par lots toutes
  les trois secondes. Sur SQLite, où les écritures sont sérialisées, journaliser en ligne ferait du
  journal le goulot de l'API qu'il observe. Le canal **perd plutôt que d'attendre**, et les pertes
  sont comptées puis affichées : un journal incomplet qui le dit reste exploitable.
- **Seules les requêtes de l'API.** Les fichiers statiques de la console — une centaine par
  chargement — ne disent rien du moteur.
- **Jamais les `GET` du journal lui-même.** Sans cette exception, chaque rafraîchissement ajouterait
  une ligne en tête de la page qu'on est en train de lire.
- **Les paramètres sensibles sont masqués** : `token`, `password`, `secret`, `code`, `identity`. Le
  jeton de fichier circule dans l'URL — sa durée de vie est de deux minutes, mais l'écrire en clair
  dans une table conservée sept jours annulerait cette précaution.
- **L'adresse d'origine est celle de la connexion**, jamais l'en-tête `X-Forwarded-For`, qui est
  déclaratif donc falsifiable. Derrière un répartiteur, c'est à l'hôte d'installer
  `UseForwardedHeaders`. Sa collecte est désactivable : une adresse IP est une donnée personnelle.
- **Le découpage de l'histogramme n'utilise aucune fonction de date** : la forme canonique des
  instants étant de longueur fixe, ses dix, treize ou seize premiers caractères désignent le jour,
  l'heure ou la minute — à l'identique sur les deux moteurs, sans méthode de dialecte
  supplémentaire à couvrir dans la suite de conformité.
- **Le filtre de niveau retient un ensemble, pas une borne basse.** « Au moins avertissement » se
  dit avec un ensemble ; l'inverse est faux — isoler les 4xx sans les vraies pannes est une demande
  d'exploitation courante, qu'une gravité minimale ne sait pas exprimer. Les noms voyagent séparés
  par des virgules dans un seul `level`, et un nom inconnu est ignoré plutôt que rejeté : une faute
  de frappe dans une adresse recopiée ne doit pas ressembler à une panne.

**Le magasin de fichiers, vu depuis la console.**

- **L'inventaire replace les clés dans le modèle.** Le magasin ne connaît que
  `{collection}/{enregistrement}/{fichier}` ; c'est l'écran qui rend à ces clés une collection, un
  enregistrement et surtout une réponse à « quelqu'un s'en sert-il encore ». Ni un explorateur de
  fichiers ni une console S3 ne savent répondre à cette dernière : il faut la base pour cela.
- **Un fichier encore référencé ne se supprime pas depuis l'inventaire.** Le retirer laisserait
  l'enregistrement pointer vers rien, et rien dans la base ne dirait qui l'a fait. Le retrait passe
  par l'édition de l'enregistrement, qui met la référence à jour dans le même mouvement. Les
  orphelins et les vignettes, eux, se suppriment : les premiers ne servent plus, les secondes se
  régénèrent.
- **La configuration du magasin se lit et s'éprouve, jamais ne s'écrit.** Le seau, le point de
  terminaison et les identifiants viennent de l'hôte, comme le moteur de base. Écrire une clé
  secrète en base la ferait entrer dans toutes les sauvegardes de cette base. La console montre donc
  la configuration en vigueur — la clé d'accès réduite à ses quatre derniers caractères, la clé
  secrète réduite à sa présence — et donne les variables exactes à poser.
- **Le test de connexion écrit vraiment.** Un contrôle qui se contenterait de lister validerait un
  seau sans droit d'écriture. Il écrit un objet témoin, le relit, le décrit, **suit son URL signée**
  puis le supprime. Cette avant-dernière étape est la seule qui détecte un `PublicEndpoint` erroné,
  lequel laisse l'API parfaitement saine et rend tous les liens invalides côté navigateur.
- **L'extraction exclut les vignettes par défaut.** Elles se régénèrent à la demande : les archiver
  revient à archiver un cache, et à doubler le poids de l'archive.

### 2.10 Migrations de schéma

Le point que PocketBase traite bien et qu'il ne faut pas rater : **une collection créée dans la
console de développement doit arriver en production de façon reproductible.**

- Toute mutation de collection produit un **instantané JSON versionné** dans `migrations/`.
- Au démarrage, les instantanés non appliqués sont exécutés (table `_migrations`).
- `cratebase migrate collections` regénère un instantané complet (mode extension ou remplacement).
- Mode `automigrate` en développement uniquement : la console écrit le fichier toute seule.
- Les instantanés sont **indépendants du moteur** — ils décrivent des collections, pas du DDL.
  C'est ce qui permet de rejouer l'historique complet sur une base PostgreSQle vierge.

---

## 3. Ce que Cratebase ajoute à PocketBase

Trois choses, toutes commandées par l'exigence d'évolutivité ou par le besoin exprimé.

**1. RBAC en plus de l'ABAC.**
PocketBase n'a que les règles par collection (ABAC) — les rôles se bricolent avec un champ `select`
et des expressions. Cratebase garde ces règles **et** ajoute le modèle rôles/permissions de
`STACK-CONCEPTION.md` §3.2 : tables `roles`, `role_permissions`, `user_permissions` (dérogations
individuelles), permissions en claims avec jokers à un seul niveau, et claim `perm_version` pour la
fraîcheur. Les deux modèles se composent : la permission garde l'endpoint, la règle garde la ligne.

```
@request.auth.perms ?~ 'posts.moderate' || owner = @request.auth.id
```

**2. Portabilité de moteur, testée.**
Traitée au §1. C'est *la* différence.

**3. Multi-instance dès la conception.**
Aucune autorité en mémoire de processus. PocketBase est mono-processus par construction.

Et une chose retirée volontairement : **les collections `view` en SQL brut**. PocketBase laisse
écrire un `SELECT` arbitraire ; c'est directement contraire à R1 et à R5. Une vue Cratebase est une
requête exprimée dans le même langage que les filtres, donc compilable pour les deux moteurs.

---

## 4. Catalogue des paquets

Règle de découpage reprise de `STACK-CONCEPTION.md` : **un paquet = une dépendance externe assumée.**

```
Cratebase.Core             abstractions, ZÉRO dépendance
                           types logiques, Id (UUIDv7), ICurrentUser, IClock,
                           hiérarchie d'erreurs, PagedResult<T>, IEmailSender

Cratebase.Expressions      DSL de filtre : lexer, parser, AST, analyse sémantique
                           ⚠️ n'émet PAS de SQL — produit un arbre de prédicats
                           ← Core

Cratebase.Data             ISqlDialect, fabrique de connexions, compilateur AST→SQL,
                           politique de transaction et de reprise, migrateur système
                           ← Core, Expressions ; dépendance : Dapper
   ├─ .Sqlite              SqliteDialect                 ← Microsoft.Data.Sqlite
   └─ .Postgres            PostgresDialect               ← Npgsql

Cratebase.Schema           modèle Collection/Field, magasin, planificateur DDL,
                           reconstruction de table SQLite, instantanés de migration
                           ← Data

Cratebase.Records          moteur CRUD : list/view/create/update/delete, expand,
                           projection, batch, validation, hooks
                           ← Schema

Cratebase.Authz            RBAC (rôles/permissions/dérogations) + évaluation des règles ABAC
                           ← Records

Cratebase.Auth             Identity + Open.IdentityServer + OAuth2 externes + MFA/OTP
                           ← Authz ; dépendances : Open.IdentityServer.*, EF Core

Cratebase.Storage          IObjectStore + LocalObjectStore + vignettes
                           ← Core ; dépendance : SkiaSharp
   └─ .S3                  S3ObjectStore, double client interne/public  ← AWSSDK.S3

Cratebase.Realtime         IRealtimeTransport + InMemory + SSE
                           ← Records
   └─ .Postgres            transport LISTEN/NOTIFY   ← Npgsql

Cratebase.Admin            exploitation : journal des requêtes (_logs) et réglages (_settings)
                           ← Data ; dépendance : Dapper
                           ⚠️ ne contient AUCUN endpoint : ce sont des magasins, appelés par
                           Cratebase.Server. Un travailleur de fond doit pouvoir journaliser
                           sans embarquer ASP.NET Core.

Cratebase.Server           hôte : câblage DI, MapCratebase(), service de la SPA, CLI
                           ← tout

Cratebase.Testing          ⭐ suites de conformité livrées — voir §6
```

npm :

```
@cratebase/client   SDK typé (équivalent pocketbase-js) : CRUD, auth, fichiers, temps réel
@cratebase/admin    console d'administration React-TS (embarquée dans Cratebase.Server)
```

---

## 5. Le système de types logiques — le cœur de la portabilité

Une seule règle : **le type logique est l'autorité, la colonne physique est un détail de dialecte.**

```csharp
public interface ITypeMapper
{
    string ColumnType(FieldDefinition field);        // DDL
    object? ToStorage(FieldDefinition f, object? v); // écriture — NORMALISE
    object? FromStorage(FieldDefinition f, object? v); // lecture
}
```

Les trois normalisations non négociables, parce que chacune est une défaillance silencieuse :

| Normalisation | Sans elle |
| --- | --- |
| **Dates** → UTC, ISO-8601, précision fixe, zéros de tête | Les filtres de plage renvoient des résultats différents selon le moteur |
| **Booléens** → 0/1 strict en SQLite | `WHERE actif = true` ne matche pas `'true'`, et personne ne voit rien |
| **Multi-valeurs** → JSON canonique, ordre stable | Le `?=` d'un moteur trouve ce que l'autre rate |

Et deux divergences qu'aucune normalisation ne règle, donc qu'il faut trancher **en faveur du moteur
le plus strict** (PostgreSQL) et contraindre l'autre :

- **Casse du `LIKE`.** SQLite est insensible à la casse en ASCII, PostgreSQL est sensible. Cratebase
  impose la sémantique **insensible** pour l'opérateur `~` : PostgreSQL reçoit `ILIKE`, SQLite reçoit
  `LIKE`. L'opérateur `:lower` reste disponible pour l'égalité insensible.
- **Tri des chaînes.** SQLite trie en binaire, PostgreSQL selon la locale. Les colonnes `text` sont
  créées en `COLLATE "C"` sur PostgreSQL pour aligner sur le comportement binaire, sauf demande
  explicite du contraire par collection.

---

## 6. La suite de conformité — le dispositif qui rend la promesse tenable

Le §2.2 de `STACK-CONCEPTION.md` établit le critère : *défaillance silencieuse → paquet, API scellée,
suite de conformité livrée.* La portabilité de moteur est **la** défaillance silencieuse de ce
projet. Elle a donc son test, et ce test est le livrable central.

`Cratebase.Testing` expose une suite unique, paramétrée par dialecte, exécutée sur SQLite **et** sur
PostgreSQL (Testcontainers) à chaque CI :

```csharp
public class SqliteConformance   : CratebaseDialectConformanceSuite<SqliteFixture> { }
public class PostgresConformance : CratebaseDialectConformanceSuite<PostgresFixture> { }
```

Ce qu'elle couvre :

1. **Chaque type de champ** — aller-retour écriture/lecture, valeurs limites, `null` vs zéro.
2. **Chaque opérateur du DSL** sur chaque type, y compris les variantes `?` multi-valeurs.
3. **Les macros de date** (`@now`, `@todayStart`, `@monthEnd`…) sur les bascules de fuseau et d'année.
4. **Le tri** — stabilité, `NULL` en tête ou en queue, casse.
5. **Les règles d'accès** — la même règle sur les deux moteurs sélectionne exactement le même jeu
   de lignes, dont le cas hostile : `DELETE` d'une ligne hors périmètre doit renvoyer 404 et **ne rien
   détruire** (le bug exact trouvé par le banc d'essai de `STACK-CONCEPTION.md` §2.2).
6. **Le DDL** — création, ajout, suppression, renommage, changement de type de champ, y compris la
   reconstruction de table SQLite ; et l'index unique qui doit refuser le doublon des deux côtés.
7. **La bascule complète** — un jeu de collections et de données est écrit sur SQLite, migré par
   `migrate-provider`, et **toutes les assertions précédentes sont rejouées** sur la base PostgreSQL
   résultante. C'est le test qui vaut promesse.

### 6.1 Ce que l'exécution a effectivement trouvé

La suite de bout en bout (`tests/smoke.ps1`, 91 assertions) a été rejouée sur PostgreSQL 18 par
`tests/postgres.ps1`. Elle passe **intégralement sur les deux moteurs** — mais pas du premier coup.
Quatre défauts de portabilité ont été découverts à cette occasion, tous de la même famille :

| Défaut | Comportement SQLite | Comportement PostgreSQL |
| --- | --- | --- |
| Date écrite en texte dans une colonne de date | accepté | `column is of type timestamptz but expression is of type text` |
| JSON écrit en texte dans une colonne JSON | accepté | `column is of type jsonb but expression is of type text` |
| Paramètre JSON non transtypé | sans objet | refusé — d'où `ISqlDialect.BindParameter` |
| `IN @liste` confié à l'expansion de l'ORM | expansé | non expansé : erreur de syntaxe |

**Ce que ça confirme du plan.** Les quatre venaient de code qui contournait `ToStorage` — la couche
de normalisation, écrite précisément pour cela. Aucun ne venait des dialectes eux-mêmes. C'est
l'argument du §10 vérifié dans les faits : avoir écrit les deux dialectes ensemble a cantonné les
défauts aux endroits qui les court-circuitaient, et un `grep` a suffi à les trouver tous. Écrits à
un an d'écart, ces mêmes défauts se seraient comptés par dizaines et auraient été découverts en
production.

**Ce que ça corrige du plan.** Le §5 disait « le type logique est l'autorité, la colonne physique
est un détail de dialecte ». C'était incomplet : **le type du paramètre est aussi un détail de
dialecte**, et il ne se déduit pas de la valeur. D'où `BindParameter`, qui adapte l'emplacement
réservé — un transtypage SQL — plutôt que de compter sur le pilote. Le pilote ne peut pas décider :
les paramètres circulent dans des objets anonymes dont les propriétés sont typées `object`, et les
mécanismes de typage se résolvent sur le type déclaré, jamais sur la valeur réelle.

---

## 7. Le pipeline d'une requête — l'ordre est la sécurité

L'ordre d'application est le point où les surcouches de ce genre échouent. Il est figé :

```
1.  Authentification            → principal, ou anonyme
2.  Permission (RBAC)           → l'endpoint est-il ouvert à ce rôle ?           403 sinon
3.  Résolution de collection    → 404 si inconnue
4.  Règle d'accès (ABAC)        → compilée en prédicat SQL
5.  Filtre utilisateur          → compilé en prédicat SQL, identifiants en LISTE BLANCHE
6.  Composition                 → prédicat = règle AND filtre        ⚠️ jamais l'inverse
7.  Bornes                      → perPage plafonné, profondeur d'expand plafonnée, tri en liste blanche
8.  Exécution                   → une seule requête paramétrée
9.  Projection (fields)         → après la base, jamais pour élargir
10. Expand                      → chaque relation revérifie la viewRule de SA collection
```

Quatre invariants scellés, chacun avec son test hostile :

- **Un identifiant absent du schéma de la collection est une erreur 400, jamais une interpolation.**
  C'est la frontière d'injection. Le compilateur ne connaît que des champs résolus.
- **La règle ne peut être qu'ajoutée.** L'API du compilateur ne permet pas de produire un `OR` entre
  règle et filtre — c'est un `sealed` sur la méthode de composition, pas une consigne.
- **Écriture : la règle va dans le `WHERE` de l'`UPDATE`/`DELETE`, et le nombre de lignes affectées
  est vérifié.** Zéro ligne → 404. Sans cela, un `DELETE` traverse le périmètre.
- **`createRule` s'évalue sur l'enregistrement proposé**, après application des valeurs par défaut et
  des hooks, jamais sur le corps brut.

---

## 8. Déploiement : un conteneur, puis plusieurs

```
┌─ conteneur unique (départ) ─────────────────────┐
│  ASP.NET Core 10 (Kestrel)                      │
│   ├── SPA React-TS  (MapStaticAssets + fallback)│
│   ├── /api/collections/*   moteur CRUD          │
│   ├── /api/files/*         disque local         │
│   ├── /api/realtime        SSE, bus mémoire     │
│   ├── /api/admin/*         console              │
│   └── /connect/*           autorité OIDC        │
│  volume: /data → cratebase.db, storage/, backups│
└─────────────────────────────────────────────────┘

                     ↓ montée en charge : configuration seule

┌─ N conteneurs ──┐   ┌ PostgreSQL ┐   ┌ MinIO / S3 ┐
│  même image     │──▶│  + NOTIFY  │   │            │
└─────────────────┘   └────────────┘   └────────────┘
```

Le SPA est **servi par ASP.NET Core**, pas par nginx : c'est ce qui tient la promesse « un seul
conteneur ». `MapStaticAssets` de .NET 10 apporte l'empreinte de contenu et la compression au build,
donc on ne perd pas grand-chose face à nginx. La configuration à l'exécution passe par un endpoint
`/config.js` généré par le serveur, remplaçant l'astuce `docker-env.sh` → `/env.js` des projets
existants — plus simple, même effet.

---

## 9. Catalogue des défaillances silencieuses — propre à ce projet

Chacune justifie du code et un test, pas une règle de documentation.

| Défaillance | Ce qu'on voit | Ce qui se passe |
| --- | --- | --- |
| **Champs appariés par nom au lieu de leur identifiant** | rien | ⚠️ *rencontrée* — un renommage devient `DROP` + `ADD`, **la colonne repart vide** |
| **Kestrel lié à `[::]` dans un conteneur sans double adressage** | conteneur sain, journaux normaux | ⚠️ *rencontrée* — rien n'écoute en IPv4, donc le port publié n'aboutit jamais |
| **`/dev/tcp` dans un `HEALTHCHECK`** | conteneur « unhealthy » | ⚠️ *rencontrée* — l'image n'a que `dash`, où cette redirection n'existe pas |
| Projet absent de la liste des `COPY *.csproj` du Dockerfile | erreur tardive et opaque | `project.assets.json not found`, plusieurs étapes après la vraie cause |
| **Tableau écrit dans un champ réputé simple** | rien | ⚠️ *rencontrée* — la conversion en texte stocke littéralement `System.String[]` |
| Type logique refusant le multi-valué qu'on lui demande | rien | la valeur est stockée en JSON et relue en texte brut : le filtre ne trouve plus rien |
| **Écriture contournant `ToStorage`** | rien sur SQLite | ⚠️ *rencontrée ×3* — date en texte dans un `timestamptz`, JSON en texte dans un `jsonb` : accepté par SQLite, refusé par PostgreSQL le jour de la bascule |
| **Expansion automatique de `IN @liste` par l'ORM** | rien sur SQLite | ⚠️ *rencontrée* — non appliquée selon le pilote : le tableau part en paramètre unique et la requête est rejetée |
| **Bulle fermée sur tout `scroll` capturé** | la liste se referme dès qu'on la fait défiler | ⚠️ *rencontrée* — l'écouteur ne distinguait pas le défilement de la bulle de celui de la page ; une liste plus haute que son cadre devenait impossible à parcourir, à la molette comme aux flèches, `scrollIntoView` déclenchant lui-même la fermeture |
| **Histogramme et tableau comptés par deux appels** | un graphique qui annonce plus d'entrées que la table sous lui | ⚠️ *rencontrée* — chaque lecture du journal vide le tampon d'écriture, donc le second appel voit ce que le premier n'avait pas : 19 contre 17. Page et histogramme partent désormais du même appel |
| **Suppression du dernier superadministrateur** | 204, tout va bien | l'instance devient inadministrable : les collections système sont verrouillées et l'amorçage ne recrée un compte que si la configuration en porte un. Aucune règle d'accès ne peut l'empêcher, puisque la garde porte sur le compte qui a le droit de tout faire — d'où un crochet de suppression |
| **Changement de mot de passe qui ne révoque rien** | le mot de passe change, l'utilisateur se croit sauf | ⚠️ *rencontrée* — seule la clé de génération tournait, et rien ne la consulte à la résolution d'un jeton : la session volée restait ouverte, alors que changer son mot de passe est le premier réflexe après un vol |
| **Champ vide envoyé pour un champ non modifié** | « l'adresse est obligatoire » sur un formulaire de mot de passe | ⚠️ *rencontrée* — un formulaire partagé envoyait toujours l'adresse, vide quand il ne la demandait pas ; le moteur comprend « efface l'adresse » et refuse un changement par ailleurs valide |
| **PATCH de réglages désérialisé en objet complet** | l'écran affiche ce qu'on a envoyé | un client n'envoyant que le nom remettrait rétention et collecte d'adresse à leurs valeurs par défaut ; d'où une charge à propriétés facultatives, fusionnée sur l'existant |
| **Rétention nulle traitée comme une coupure au présent** | journal vide après chaque passage du service d'entretien | zéro signifie « conserver sans limite », pas « tout supprimer » |
| **Jeton de fichier écrit tel quel dans le journal** | rien | un jeton de deux minutes devient exploitable pendant toute la durée de rétention |
| Reconstruction de table SQLite avec les clés étrangères actives | rien | ⚠️ le `DROP TABLE` déclenche les cascades : les tables référençantes sont vidées |
| Identifiant de champ interpolé au lieu d'être résolu | rien | injection SQL par `?filter=` |
| Règle composée en `OR` au lieu de `AND` | rien | toute la collection est lisible |
| `DELETE` sans la règle dans le `WHERE` | 204 | ligne d'autrui détruite |
| `expand` sans revérifier la `viewRule` de la cible | données en trop | fuite par relation |
| Date non normalisée en SQLite | rien | filtres de plage faux après migration |
| Booléen stocké en `'true'` | rien | `WHERE actif = 1` ne trouve rien |
| `LIKE` sensible à la casse sur PostgreSQL seulement | rien en dev | la recherche cesse de marcher en production |
| Tri sans liste blanche | rien | l'ordre divulgue des valeurs non lisibles |
| `perPage` non plafonné | lenteur | déni de service par une seule requête |
| Reconstruction de table SQLite perdant un index | rien | unicité perdue, doublons silencieux |
| Bus temps réel en mémoire, 2 instances | rien | la moitié des clients ne reçoit rien |
| Règle temps réel évaluée à l'abonnement seulement | rien | on continue de recevoir après perte de droit |
| Vignette servie sans revérifier le fichier protégé | image visible | contournement de la `viewRule` |
| URL présignée signée pour le mauvais hôte | liens rejetés | navigateur et API ne voient pas S3 pareil |
| Migration de collection non exportée | dev ≠ prod | la collection n'existe pas au déploiement |

---

## 10. Jalons

Chaque jalon est livrable et testé. L'ordre est commandé par le risque : le plus incertain d'abord.

| # | Jalon | Contenu | Sortie |
| :-: | --- | --- | --- |
| **0** | **La chaîne** | Solution, `Directory.Packages.props`, SPA servie par ASP.NET Core, SQLite, Dockerfile mono-conteneur, CI | `docker run` sert une page |
| **1** | **Types + dialecte** | `Core`, `Data`, `SqliteDialect`, migrateur système, **et le `PostgresDialect` dès maintenant** | suite de conformité verte sur les deux moteurs |
| **2** | **Schéma dynamique** | `Schema` : collections, champs, index, DDL, reconstruction SQLite, instantanés de migration | créer une collection par API crée la table |
| **3** | **DSL de filtre** | `Expressions` : lexer, parser, AST, analyse ; compilateur AST→SQL dans `Data` | `?filter=` fonctionne, identiquement sur les deux moteurs |
| **4** | **CRUD** | `Records` : list/view/create/update/delete, `sort`, `expand`, `fields`, batch, hooks | API PocketBase-compatible sur collections `base` |
| **5** | **Auth + Authz** | `Auth` (Identity, OIDC, OAuth2, MFA/OTP), `Authz` (RBAC + règles ABAC câblées au pipeline §7) | règles et permissions appliquées, tests hostiles verts |
| **6** | **Fichiers** | `Storage` local, upload, vignettes, fichiers protégés + jetons | champs `file` opérationnels |
| **7** | **Console** | `@cratebase/admin` : collections, enregistrements, utilisateurs, rôles, réglages, journaux | administration complète sans SQL |
| **8** | **Temps réel** | `Realtime` SSE + `InMemory`, règles réévaluées à la diffusion | abonnements PocketBase-compatibles |
| **9** | **Librairie** | Hooks publics, `MapCratebase()`, paquets NuGet, `dotnet new cratebase`, `@cratebase/client` | un projet tiers l'embarque |
| **10** | **Ampleur** | `Data.Postgres` en production, `Storage.S3`, `Realtime.Postgres`, `migrate-provider` | bascule prouvée par le test §6.7 |

**Le jalon 1 écrit les deux dialectes en même temps.** C'est contre-intuitif — on ne se sert de
PostgreSQL qu'au jalon 10 — mais c'est la seule façon d'empêcher la fuite d'hypothèses SQLite dans
tout le reste. Un dialecte écrit après coup découvre trop tard que dix décisions sont à défaire.
C'est le prix de la promesse d'évolutivité, et il se paye au début ou il se paye dix fois.

---

## 11. Ce qui est hors périmètre

- **Le SQL brut dans les collections `view`** — contraire à R1 et R5 (§3).
- **GraphQL** — le DSL de filtre couvre le besoin ; deux langages d'interrogation, c'est deux
  surfaces d'autorisation.
- **Le multi-tenant** — `STACK-CONCEPTION.md` §6 le traite pour Forge. Ici, une collection avec une
  relation `organization` et une règle d'accès le font, sans mécanique dédiée. À réévaluer si le
  besoin devient structurel.
- **Les fonctions de bord (Edge Functions)** — sans objet : c'est ASP.NET Core en entier.
