# Migration de moteur et de stockage

Plan proposé. Rien de ce qui suit n'est implémenté.

Deux bascules, de forme identique et de difficulté très inégale :

- **base** : SQLite → PostgreSQL, et l'inverse ;
- **stockage** : disque local → S3, et l'inverse.

La promesse du projet est que ces bascules sont des changements de configuration. Elle est déjà
tenue pour une instance **vide** : `tests/postgres.ps1` et `tests/minio.ps1` le prouvent à chaque
exécution. Ce qui manque, et que ce plan couvre, c'est le déplacement des données **déjà écrites**.

## 1. Le principe qui décide de tout

La copie ne réutilise pas le format d'archive de [`SAUVEGARDE.md`](./SAUVEGARDE.md) : elle réutilise
son **lecteur** et son **écrivain**. Passer par un fichier intermédiaire imposerait de sérialiser
puis désérialiser des gigaoctets sans nécessité, et ferait vivre deux chemins de copie qui
divergeraient au premier correctif appliqué à un seul.

La migration est donc une lecture par curseur sur la source, branchée sur l'écriture par lots dans
la cible, **dans un même processus qui connaît les deux**.

C'est le point d'architecture à ouvrir : aujourd'hui l'injection de dépendances lie un seul dialecte
et un seul magasin d'objets pour toute l'application. Il faut un `TransferContext` qui porte deux
couples `(fabrique de connexions, dialecte)` — et non une seconde inscription globale, qui rendrait
ambigu le moteur que sert l'API pendant la copie.

## 2. Propriété de sûreté

**La migration n'écrit jamais dans la source.** Jamais, à aucune étape, y compris pour marquer une
progression. Il n'y a donc rien à défaire : abandonner une migration, c'est arrêter le processus et
ne pas changer la configuration.

La progression est écrite dans la **cible**, table `_transfers` — `_migrations` est déjà prise par
l'historique des migrations de schéma :

| Colonne | Contenu |
| --- | --- |
| `id` | identifiant du transfert |
| `collection` | collection en cours |
| `lastId` | dernier identifiant copié |
| `copied` / `total` | progression |
| `state` | `pending`, `running`, `done`, `failed` |
| `error` | message, si `failed` |

Redémarrer reprend à `lastId`. Comme toute écriture est un remplacement par identifiant, rejouer un
lot déjà écrit est sans effet.

## 3. Étapes d'une bascule de base

1. **Préparation.** Lecture du schéma dans la source, création du schéma dans la cible par le
   `SchemaPlanner` existant, dont le DDL est produit par le dialecte de destination. C'est ici que
   R5 se paie : le schéma étant une donnée, il n'y a pas de traduction à écrire.
2. **Contrôle préalable.** La cible doit être vide, ou l'opérateur doit avoir demandé le mode
   « remplacer ». Une cible non vide qu'on remplit par erreur est irrattrapable.
3. **Copie.** Collection par collection, curseur sur `id`, lots de 500, une transaction par lot dans
   la cible. Une transaction unique pour dix millions de lignes ferait grossir le journal jusqu'à
   saturer le disque.
4. **Vérification.** Décompte par collection des deux côtés, plus un échantillon comparé champ à
   champ. **Un écart interdit la bascule** : une migration qui se déclare réussie sur un décompte
   faux est exactement la défaillance que ce projet cherche à ne pas produire.
5. **Bascule.** L'outil ne modifie pas la configuration et ne redémarre rien : il affiche la
   variable à poser (`ConnectionStrings__Postgres`) et s'arrête. Un outil qui bascule tout seul
   bascule un jour au mauvais moment.

## 4. Interruption de service

Deux modes, dont un seul est proposé pour commencer.

### Mode « lecture seule » — proposé

Pendant la copie, l'instance refuse les écritures : un `IRecordMutationHook` répond `503` avec un
en-tête `Retry-After`, et la console affiche un bandeau expliquant pourquoi. Les lectures continuent
d'être servies par la source.

C'est simple, c'est honnête, et la durée est prévisible : quelques minutes pour les volumes que
SQLite justifie d'héberger.

### Mode « sans interruption » — pas gratuit, et il faut le dire

Copier pendant que l'application écrit suppose de rattraper les mutations survenues pendant la
copie. Les créations et les modifications sont rattrapables : `updated` est monotone, une seconde
passe sur `updated > début de la copie` suffit.

**Les suppressions ne le sont pas.** Une ligne supprimée après avoir été copiée n'existe plus nulle
part pour signaler qu'elle a existé : elle survivrait dans la cible, et l'application repartirait
avec des données que son utilisateur croit effacées. Ce n'est pas une imperfection acceptable, c'est
une violation de l'attente la plus élémentaire.

Ce mode exige donc au préalable un registre de suppressions — table `_tombstones`, alimentée par le
crochet de mutation, purgée au-delà d'une fenêtre. C'est un ajout au moteur, pas un détail de
l'outil de migration, et il porte son propre coût sur toutes les suppressions. À traiter comme une
étape distincte, si le besoin se présente.

## 5. Bascule de stockage

Même forme, moins de pièges :

1. Énumérer les objets **depuis les champs fichier des enregistrements**, et non depuis le magasin
   source. Le magasin peut contenir des orphelins d'imports interrompus, qu'il n'y a aucune raison
   de recopier.
2. Copier chaque objet par `IObjectStore.OpenReadAsync` → `PutAsync`, en flux.
3. Vérifier taille et empreinte après écriture.
4. Signaler les objets **référencés mais absents** de la source. Les passer sous silence
   transformerait une base déjà abîmée en base abîmée sur deux stockages.
5. Ne pas copier les vignettes : elles se régénèrent.
6. **Contrôler l'URL publique.** Après une bascule vers S3, signer une URL et la récupérer
   réellement. Une `PublicEndpoint` erronée rend tous les liens invalides, et c'est le piège le plus
   coûteux du stockage objet — il ne doit pas être découvert par un utilisateur.

## 6. Progression et interface

Le job de transfert expose la même forme que les exports :

```
POST /api/transfers          crée un transfert, cible décrite dans le corps
GET  /api/transfers/{id}     état global et par collection
POST /api/transfers/{id}/annuler
```

Écran **Administration → Migration** : cible, contrôle préalable, barre par collection et barre
globale, ligne d'état, et à la fin la variable de configuration à poser. En cas d'échec, la
collection fautive et le message, sans avoir rien basculé.

En ligne de commande pour l'exploitation :

```bash
cratebase transfer --vers postgres --connexion "Host=…" --lecture-seule
cratebase transfer --vers s3 --bucket cratebase --endpoint https://…
```

## 7. Étapes

| # | Contenu | Pourquoi dans cet ordre |
| :-: | --- | --- |
| 1 | `TransferContext` à deux couples moteur/dialecte, sans copie | Le point d'architecture. Tout le reste en dépend. |
| 2 | Copie du schéma vers la cible + contrôle préalable | Vérifie que le `SchemaPlanner` produit bien le DDL de l'autre moteur sur un schéma réel. |
| 3 | Copie des données, reprise par `_transfers`, mode lecture seule | Le cœur. Testable en tuant le processus à mi-parcours. |
| 4 | Vérification par décomptes et échantillon | Sans elle, les trois étapes précédentes ne prouvent rien. |
| 5 | Bascule de stockage | Indépendante ; peut être menée en parallèle. |
| 6 | Écran de migration et commandes | Quand il y a quelque chose à piloter. |
| 7 | `_tombstones` et rattrapage incrémental | Seulement si l'interruption devient inacceptable. |

## 8. Défaillances silencieuses à couvrir

| Défaillance | Symptôme | Parade |
| --- | --- | --- |
| Suppressions perdues en mode sans interruption | Données effacées ressuscitées après bascule | Mode écarté tant que `_tombstones` n'existe pas |
| Cible non vide | Mélange de deux instances, irrattrapable | Contrôle préalable, mode « remplacer » explicite |
| Décomptes non vérifiés | Migration déclarée réussie avec des lignes manquantes | Vérification bloquante avant la bascule |
| Transaction unique sur toute la copie | Journal saturé, échec au bout de plusieurs heures | Une transaction par lot de 500 |
| Reprise qui duplique | Doublons après un redémarrage | Écriture par identifiant, donc rejouable |
| Orphelins du magasin recopiés | Stockage cible gonflé d'objets que rien ne référence | Énumération depuis les enregistrements |
| `PublicEndpoint` erronée après bascule S3 | Tous les liens de fichiers rejetés, API en apparence saine | Contrôle d'URL signée en fin de migration |
| Bascule automatique de la configuration | Instance qui change de moteur sans que personne ne l'ait décidé | L'outil affiche la variable, ne la pose pas |
