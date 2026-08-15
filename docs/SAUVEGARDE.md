# Sauvegarde et extraction des données

Plan proposé. Rien de ce qui suit n'est implémenté.

> **Ce document ne revient pas sur le §2.9 de [`CONCEPTION.md`](../CONCEPTION.md).** Ce qui y est
> écarté, c'est la **sauvegarde physique** : zipper le fichier SQLite, ou appeler `VACUUM INTO` d'un
> côté et `pg_dump` de l'autre. Un bouton dont le contenu change de nature selon le moteur n'est pas
> un bouton portable, et il violerait R1. Ce qui est proposé ici est autre chose : un **export
> logique**, qui passe par le modèle de collections et ne connaît aucun moteur. Il est portable par
> construction — c'est même la seule forme d'archive qu'on puisse restaurer sur l'autre moteur.

## 1. Ce que l'archive contient

Un fichier `.crate`, qui est une archive ZIP :

```
manifest.json                     version du format, origine, empreintes
collections.json                  définitions des collections — le schéma est une donnée (R5)
settings.json                     réglages d'instance, sans aucun secret
records/<collection>.jsonl        un enregistrement par ligne
files/<collection>/<id>/<nom>     contenu des champs fichier
```

**JSON Lines et non SQL.** Un `INSERT` appartient à un dialecte ; un objet JSON n'appartient à
personne. Le format ligne à ligne permet en outre d'écrire et de relire en flux : une collection de
dix millions de lignes ne tient pas en mémoire, et n'a aucune raison d'y tenir.

**ZIP et non répertoire.** Un artefact unique se télécharge, se déplace et se vérifie d'un seul
geste. L'entrée `manifest.json` est écrite en dernier mais placée en tête : une archive tronquée est
alors détectable sans la lire entièrement.

### Ce qui est exclu, et pourquoi

| Écarté | Raison |
| --- | --- |
| `_logs` | Journal d'exploitation, pas donnée applicative. Le restaurer réécrirait l'histoire d'une autre instance. Récupérable par option explicite. |
| `_authTokens` | Restaurer des sessions ouvertes sur une autre instance est une faille, pas une commodité. |
| Vignettes | Dérivées des originaux, régénérables. Les archiver, c'est archiver un cache. |
| Secrets d'hôte | Chaîne de connexion, clés S3, secrets OAuth2 : ils vivent en configuration d'hôte, précisément pour qu'aucune archive ne puisse les divulguer. |

### Ce qui est inclus bien que sensible

Les **empreintes de mots de passe** et les **secrets TOTP** sont dans l'archive. Une sauvegarde dont
la restauration laisse tous les comptes incapables de se connecter n'est pas une sauvegarde. En
contrepartie, le manifeste porte `containsSecrets: true`, la console l'annonce avant le
téléchargement, et l'option `--sans-comptes` produit une archive diffusable.

## 2. Cohérence

Un export qui lit collection après collection sans précaution capture un état qui n'a jamais existé :
la commande écrite entre la lecture de `clients` et celle de `commandes` référence un client absent
de l'archive.

L'export entier se déroule donc dans **une seule transaction de lecture**, ouverte au début et
fermée à la fin — instantané WAL côté SQLite, `REPEATABLE READ` côté PostgreSQL. Les deux sont
exprimés par l'abstraction de transaction existante : aucun SQL de dialecte n'entre dans
`Cratebase.Backup`.

Les fichiers, eux, sont hors transaction. Un objet supprimé pendant l'export est signalé comme
manquant dans le manifeste plutôt que d'interrompre l'archive — et surtout plutôt que d'être passé
sous silence.

## 3. Pagination

Par curseur sur l'identifiant, jamais par `OFFSET` :

```
WHERE id > @dernier ORDER BY id LIMIT 500
```

Les identifiants sont des UUIDv7, donc triables et monotones. Un `OFFSET` sur une table en écriture
saute des lignes ou en répète — silencieusement, et d'autant plus que la table est grande. La forme
par curseur passe par `ISqlDialect.LimitOffset`, déjà couvert par la suite de conformité.

## 4. Restauration

Trois modes, choisis explicitement :

| Mode | Comportement |
| --- | --- |
| **Refuser** (défaut) | Échoue si une collection de l'archive existe déjà. Le mode le plus sûr est celui qu'on obtient sans rien choisir. |
| **Fusionner** | Écrit chaque enregistrement par son identifiant, en écrasant l'existant. |
| **Remplacer** | Supprime les collections concernées, puis les recrée. Exige une confirmation qui nomme l'instance. |

**Ordre d'écriture.** Les collections sont triées par dépendance de relation. En présence d'un
cycle — qui est licite —, deux passes : les enregistrements sont d'abord écrits sans leurs champs
de relation, puis complétés. Écrire dans un ordre arbitraire ferait échouer les clés étrangères de
façon dépendante du hasard de l'énumération.

**Idempotence.** Toute écriture est un ajout ou un remplacement par identifiant. Reprendre un import
interrompu ne duplique donc rien.

**Vérification avant écriture.** Le manifeste porte une empreinte SHA-256 par entrée. Elles sont
toutes contrôlées avant la première écriture : un import qui échoue à mi-parcours sur une archive
corrompue laisse une base à moitié restaurée, ce qui est pire que pas d'import du tout.

## 5. Là où ça tourne

Une archive de plusieurs gigaoctets ne se produit pas dans le temps d'une requête HTTP. Le travail
est donc un **job**, pas une réponse :

```
POST   /api/exports              crée le job, rend son identifiant
GET    /api/exports/{id}         état, progression par collection, erreur éventuelle
GET    /api/exports/{id}/fichier téléchargement, une fois terminé
DELETE /api/exports/{id}         supprime l'archive
POST   /api/imports              dépose une archive, rend un job
GET    /api/imports/{id}/apercu  ce que l'archive contient, avant de rien écrire
POST   /api/imports/{id}         exécute, mode explicite
```

Les archives vivent sous `DataDirectory/exports`, avec une rétention réglable — sans quoi le disque
se remplit d'archives que personne n'a téléchargées.

Le même moteur est exposé en ligne de commande (`cratebase export`, `cratebase import`) : une
sauvegarde planifiée ne doit pas dépendre d'un navigateur ouvert.

## 6. Étapes

| # | Contenu | Pourquoi dans cet ordre |
| :-: | --- | --- |
| 1 | `manifest.json` + `collections.json`, import en mode « refuser » | Le plus petit export utile : cloner un schéma vers une instance vide. Exerce déjà le format, le job et la restauration. |
| 2 | `records/*.jsonl`, pagination par curseur, transaction de lecture | Le gros du volume et le seul point de cohérence délicat. |
| 3 | `files/**` via `IObjectStore` | Indépendant du moteur de base ; se greffe sans rien défaire. |
| 4 | Écran **Administration → Sauvegardes** : jobs, progression, aperçu d'import | L'interface arrive quand il y a quelque chose à montrer. |
| 5 | `cratebase export` / `cratebase import` | Ce qui rend la sauvegarde planifiable. |

## 7. Défaillances silencieuses à couvrir

À verser au §9 de `CONCEPTION.md` quand ce sera écrit.

| Défaillance | Symptôme | Parade |
| --- | --- | --- |
| Export sans transaction | Archive incohérente entre collections liées ; ne se voit qu'à la restauration, des mois plus tard | Transaction de lecture unique, testée par un export concurrent à des écritures |
| Pagination par `OFFSET` | Lignes sautées ou dupliquées sur une base active | Curseur sur `id`, test avec insertions pendant l'export |
| Import partiel après archive corrompue | Base à moitié restaurée, sans marqueur | Empreintes vérifiées avant la première écriture |
| Ordre d'écriture arbitraire | Échec de clé étrangère aléatoire selon l'ordre d'énumération | Tri topologique, deux passes en cas de cycle |
| Vignettes archivées | Archive gonflée, et vignettes périmées après restauration | Exclues, régénérées à la demande |
| Archive avec empreintes de mots de passe diffusée | Fuite de secrets par un fichier qu'on croyait anodin | `containsSecrets` dans le manifeste, avertissement dans la console, option `--sans-comptes` |
| Rétention absente | Le disque se remplit d'archives jamais téléchargées | Rétention réglable, purge par le service de maintenance existant |
