import type {
  AccessRules,
  Collection,
  CollectionIndex,
  CollectionKind,
  FieldType,
  RuleAction,
} from '../api'
import { supportsMultiple } from '../lib/fields'

/**
 * Diagnostic d'une collection, calculé côté client.
 *
 * Tout ce qui est nécessaire est déjà chargé — la définition de la collection —, donc rien de ceci
 * ne demande d'endpoint dédié. Le diagnostic porte sur le **brouillon** en cours d'édition et non
 * sur la version enregistrée : c'est en écrivant le schéma qu'on veut savoir ce qu'on est en train
 * de casser, pas après.
 */

/* ------------------------------------------------------------------- Règles */

export type RuleState = 'locked' | 'open' | 'conditional'

const READ_ACTIONS: RuleAction[] = ['list', 'view']
const WRITE_ACTIONS: RuleAction[] = ['create', 'update', 'delete']
const ADMIN_ACTIONS: RuleAction[] = ['manage']

export const RULE_LABELS: Record<RuleAction, string> = {
  list: 'Lister',
  view: 'Consulter',
  create: 'Créer',
  update: 'Modifier',
  delete: 'Supprimer',
  manage: 'Gérer',
}

/**
 * Actions qui existent réellement sur ce type de collection.
 *
 * `manage` — qui peut changer le mot de passe ou l'adresse d'un autre compte — n'a de sens que sur
 * une collection d'authentification. La compter sur une collection de données gonflerait le
 * dénominateur d'une action qui n'est jamais évaluée.
 */
export function applicableActions(kind: CollectionKind): RuleAction[] {
  return kind === 'Auth'
    ? [...READ_ACTIONS, ...WRITE_ACTIONS, ...ADMIN_ACTIONS]
    : [...READ_ACTIONS, ...WRITE_ACTIONS]
}

export function ruleState(value: string | null | undefined): RuleState {
  if (value === null || value === undefined) return 'locked'

  return value === '' ? 'open' : 'conditional'
}

export interface RulesReport {
  /** Actions évaluées sur cette collection. Sert de dénominateur aux compteurs. */
  applicable: RuleAction[]
  locked: RuleAction[]
  conditional: RuleAction[]
  /** Ouvertes à tous, visiteurs anonymes compris. */
  open: RuleAction[]
  /** Sous-ensemble de `open` qui laisse écrire ou administrer : le cas le plus grave. */
  openWrites: RuleAction[]
  /** Sous-ensemble de `open` qui laisse seulement lire. */
  openReads: RuleAction[]
}

export function analyseRules(kind: CollectionKind, rules: AccessRules): RulesReport {
  const applicable = applicableActions(kind)
  const by = (state: RuleState) => applicable.filter((action) => ruleState(rules[action]) === state)
  const open = by('open')

  return {
    applicable,
    locked: by('locked'),
    conditional: by('conditional'),
    open,
    // `manage` est rangé avec les écritures : accorder à un anonyme le droit de changer le mot de
    // passe d'un compte est au moins aussi grave que de le laisser écrire une ligne.
    openWrites: open.filter((action) => [...WRITE_ACTIONS, ...ADMIN_ACTIONS].includes(action)),
    openReads: open.filter((action) => READ_ACTIONS.includes(action)),
  }
}

/* -------------------------------------------------------------------- Index */

export type IssueTone = 'danger' | 'warning'

export interface IndexIssue {
  id: string
  tone: IssueTone
  title: string
  detail: string
}

/** Champ tel que le diagnostic a besoin de le connaître, brouillon ou champ système. */
export interface HealthField {
  name: string
  type: FieldType
  multiple: boolean
}

const normalise = (names: string[]) =>
  [...names].map((name) => name.trim().toLowerCase()).sort().join(' + ')

/**
 * Anomalies d'indexation.
 *
 * Aucune n'empêche d'enregistrer : ce sont des pièges silencieux — une requête qui balaye toute la
 * table, un index qui ne servira jamais — que rien dans l'écran ne signalerait autrement.
 */
export function analyseIndexes({
  indexes,
  savedIndexes,
  fields,
  kind,
  collectionName,
  isNew,
}: {
  /** Index du brouillon, hors index système. */
  indexes: CollectionIndex[]
  /** Index tels qu'ils sont enregistrés, index système compris. */
  savedIndexes: CollectionIndex[]
  /** Tous les champs de la collection, système compris. */
  fields: HealthField[]
  kind: CollectionKind
  collectionName: string
  isNew: boolean
}): IndexIssue[] {
  const issues: IndexIssue[] = []
  const known = new Map(fields.map((field) => [field.name.toLowerCase(), field]))
  const indexed = new Set(
    [...indexes, ...savedIndexes].flatMap((index) =>
      index.fields.map((name) => name.trim().toLowerCase()),
    ),
  )

  const seen = new Map<string, string>()

  for (const index of indexes) {
    for (const name of index.fields) {
      const field = known.get(name.trim().toLowerCase())

      if (!field) {
        issues.push({
          id: `inconnu-${index.name}-${name}`,
          tone: 'danger',
          title: 'Index sur un champ inexistant',
          detail: `L'index « ${index.name} » porte sur « ${name} », qui ne figure pas dans le schéma. La création de l'index échouera.`,
        })
        continue
      }

      if (field.multiple) {
        issues.push({
          id: `multivalue-${index.name}-${name}`,
          tone: 'warning',
          title: 'Index sur un champ multi-valué',
          detail: `« ${name} » est stocké en JSON : l'index « ${index.name} » ne sera pas utilisé par une comparaison d'égalité, seulement par un parcours complet.`,
        })
      }
    }

    if (index.fields.length === 0) {
      issues.push({
        id: `vide-${index.name}`,
        tone: 'danger',
        title: 'Index sans champ',
        detail: `L'index « ${index.name} » ne désigne aucune colonne.`,
      })
      continue
    }

    const signature = normalise(index.fields)
    const previous = seen.get(signature)

    if (previous) {
      issues.push({
        id: `doublon-${index.name}`,
        tone: 'warning',
        title: 'Index en double',
        detail: `« ${index.name} » et « ${previous} » portent sur le même jeu de champs (${index.fields.join(', ')}). Le second n'apporte rien et coûte à chaque écriture.`,
      })
    } else {
      seen.set(signature, index.name)
    }
  }

  for (const field of fields) {
    if (field.type !== 'Relation' || field.multiple) continue
    if (indexed.has(field.name.toLowerCase())) continue

    issues.push({
      id: `relation-${field.name}`,
      tone: 'warning',
      title: 'Relation sans index',
      detail: `Le champ de relation « ${field.name} » n'est indexé nulle part : chaque filtre sur cette relation balaye toute la table.`,
    })
  }

  // Les index système d'une collection d'auth sont réappliqués par le moteur à chaque écriture.
  // Leur absence sur une collection déjà enregistrée signale une base modifiée à la main.
  if (kind === 'Auth' && !isNew) {
    const present = new Set(savedIndexes.map((index) => index.name))

    for (const suffix of ['email', 'tokenKey']) {
      const expected = `idx_${collectionName}_${suffix}`

      if (present.has(expected)) continue

      issues.push({
        id: `systeme-${suffix}`,
        tone: 'danger',
        title: "Index système d'authentification manquant",
        detail: `« ${expected} » est absent : l'unicité de « ${suffix} » n'est plus garantie par la base.`,
      })
    }
  }

  return issues
}

/* ------------------------------------------------------------------ Synthèse */

/** Repère porté par une collection dans la colonne de navigation. */
export interface CollectionAlert {
  /** Gravité la plus élevée présente sur la collection. */
  tone: IssueTone
  /** Nombre total d'éléments signalés, toutes gravités confondues. */
  count: number
  /** Ce que le décompte recouvre, pour le survol. */
  reason: string
}

/**
 * Ce qu'une collection a de critique, résumé en un seul repère.
 *
 * <b>Le nombre compte tout ce que le diagnostic signale ; la couleur porte la gravité la plus
 * élevée.</b> Les deux ne se lisent pas de la même façon et c'est voulu : le nombre doit coïncider
 * avec ce que les onglets « Règles » et « Index » annoncent une fois la collection ouverte — une
 * pastille qui dit 3 devant deux onglets qui en montrent 5 fait douter des trois compteurs à la
 * fois. La couleur, elle, ne se moyenne pas : tant qu'une seule règle d'écriture est ouverte à
 * tous, le repère est rouge, même entouré d'avertissements bénins.
 *
 * Calculé sur la définition enregistrée, jamais sur un brouillon : la colonne de navigation décrit
 * l'état de la base, pas ce qu'un onglet ouvert est en train d'écrire.
 */
export function collectionAlert(collection: Collection): CollectionAlert | null {
  const rules = analyseRules(collection.kind, collection.rules)
  const issues = analyseIndexes({
    indexes: collection.indexes,
    savedIndexes: collection.indexes,
    fields: collection.fields.map((field) => ({
      name: field.name,
      type: field.type,
      multiple: field.multiple,
    })),
    kind: collection.kind,
    collectionName: collection.name,
    isNew: false,
  })

  const count = rules.open.length + issues.length

  if (count === 0) return null

  const blocking = issues.filter((issue) => issue.tone === 'danger').length

  const reason = [
    [rules.open.length, 'règle ouverte à tous', 'règles ouvertes à tous'] as const,
    [issues.length, 'anomalie d’index', 'anomalies d’index'] as const,
  ]
    .filter(([total]) => total > 0)
    .map(([total, singular, plural]) => `${total} ${total > 1 ? plural : singular}`)
    .join(', ')

  return {
    tone: rules.openWrites.length + blocking > 0 ? 'danger' : 'warning',
    count,
    reason,
  }
}

/** Le champ admet-il plusieurs valeurs, d'après son type et son nombre maximal ? */
export function isMultiple(type: FieldType, maxSelect: number): boolean {
  return supportsMultiple(type) && maxSelect > 1
}
