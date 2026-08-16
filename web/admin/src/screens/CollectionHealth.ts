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
 * Diagnostic for a collection, computed client-side.
 *
 * Everything needed is already loaded — the collection's definition —, so none of this requires
 * a dedicated endpoint. The diagnostic covers the **draft** currently being edited, not the saved
 * version: it's while writing the schema that you want to know what you're about to break, not
 * after.
 */

/* ------------------------------------------------------------------- Rules */

export type RuleState = 'locked' | 'open' | 'conditional'

const READ_ACTIONS: RuleAction[] = ['list', 'view']
const WRITE_ACTIONS: RuleAction[] = ['create', 'update', 'delete']
const ADMIN_ACTIONS: RuleAction[] = ['manage']

export const RULE_LABELS: Record<RuleAction, string> = {
  list: 'List',
  view: 'View',
  create: 'Create',
  update: 'Update',
  delete: 'Delete',
  manage: 'Manage',
}

/**
 * Actions that actually exist on this type of collection.
 *
 * `manage` — which can change another account's password or email — only makes sense on an
 * authentication collection. Counting it on a data collection would inflate the denominator with
 * an action that's never evaluated.
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
  /** Actions evaluated on this collection. Serves as the denominator for the counters. */
  applicable: RuleAction[]
  locked: RuleAction[]
  conditional: RuleAction[]
  /** Open to everyone, anonymous visitors included. */
  open: RuleAction[]
  /** Subset of `open` that allows writing or administering: the most severe case. */
  openWrites: RuleAction[]
  /** Subset of `open` that only allows reading. */
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
    // `manage` is grouped with writes: granting an anonymous user the right to change an
    // account's password is at least as severe as letting them write a row.
    openWrites: open.filter((action) => [...WRITE_ACTIONS, ...ADMIN_ACTIONS].includes(action)),
    openReads: open.filter((action) => READ_ACTIONS.includes(action)),
  }
}

/* -------------------------------------------------------------------- Indexes */

export type IssueTone = 'danger' | 'warning'

export interface IndexIssue {
  id: string
  tone: IssueTone
  title: string
  detail: string
}

/** A field as the diagnostic needs to know it, draft or system field. */
export interface HealthField {
  name: string
  type: FieldType
  multiple: boolean
}

const normalise = (names: string[]) =>
  [...names].map((name) => name.trim().toLowerCase()).sort().join(' + ')

/**
 * Indexing anomalies.
 *
 * None of them prevent saving: they're silent traps — a query scanning the whole table, an index
 * that will never be used — that nothing in the screen would otherwise flag.
 */
export function analyseIndexes({
  indexes,
  savedIndexes,
  fields,
  kind,
  collectionName,
  isNew,
}: {
  /** Indexes from the draft, excluding system indexes. */
  indexes: CollectionIndex[]
  /** Indexes as saved, system indexes included. */
  savedIndexes: CollectionIndex[]
  /** All fields of the collection, system fields included. */
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
          id: `unknown-${index.name}-${name}`,
          tone: 'danger',
          title: 'Index on a nonexistent field',
          detail: `Index "${index.name}" targets "${name}", which isn't in the schema. Creating the index will fail.`,
        })
        continue
      }

      if (field.multiple) {
        issues.push({
          id: `multivalue-${index.name}-${name}`,
          tone: 'warning',
          title: 'Index on a multi-value field',
          detail: `"${name}" is stored as JSON: index "${index.name}" won't be used by an equality comparison, only by a full scan.`,
        })
      }
    }

    if (index.fields.length === 0) {
      issues.push({
        id: `empty-${index.name}`,
        tone: 'danger',
        title: 'Index with no field',
        detail: `Index "${index.name}" targets no column.`,
      })
      continue
    }

    const signature = normalise(index.fields)
    const previous = seen.get(signature)

    if (previous) {
      issues.push({
        id: `duplicate-${index.name}`,
        tone: 'warning',
        title: 'Duplicate index',
        detail: `"${index.name}" and "${previous}" target the same set of fields (${index.fields.join(', ')}). The second one adds nothing and costs on every write.`,
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
      title: 'Relation without an index',
      detail: `Relation field "${field.name}" isn't indexed anywhere: every filter on this relation scans the whole table.`,
    })
  }

  // A auth collection's system indexes are reapplied by the engine on every write. Their absence
  // on an already-saved collection signals a database modified by hand.
  if (kind === 'Auth' && !isNew) {
    const present = new Set(savedIndexes.map((index) => index.name))

    for (const suffix of ['email', 'token_key']) {
      const expected = `idx_${collectionName}_${suffix}`

      if (present.has(expected)) continue

      issues.push({
        id: `system-${suffix}`,
        tone: 'danger',
        title: 'Missing system authentication index',
        detail: `"${expected}" is absent: the uniqueness of "${suffix}" is no longer guaranteed by the database.`,
      })
    }
  }

  return issues
}

/* ------------------------------------------------------------------ Summary */

/** Marker carried by a collection in the navigation column. */
export interface CollectionAlert {
  /** Highest severity present on the collection. */
  tone: IssueTone
  /** Total number of flagged items, across all severities. */
  count: number
  /** What the count covers, for hover. */
  reason: string
}

/**
 * What's critical about a collection, summarized in a single marker.
 *
 * <b>The number counts everything the diagnostic flags; the color carries the highest
 * severity.</b> The two don't read the same way, and that's deliberate: the number must match
 * what the "Rules" and "Indexes" tabs report once the collection is open — a badge that says 3 in
 * front of two tabs showing 5 casts doubt on all three counters at once. The color, on the other
 * hand, doesn't average out: as long as a single write rule is open to everyone, the marker is
 * red, even surrounded by benign warnings.
 *
 * Computed on the saved definition, never on a draft: the navigation column describes the state
 * of the database, not what an open tab is currently writing.
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
    [rules.open.length, 'rule open to everyone', 'rules open to everyone'] as const,
    [issues.length, 'index issue', 'index issues'] as const,
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

/** Does the field admit multiple values, based on its type and its maximum count? */
export function isMultiple(type: FieldType, maxSelect: number): boolean {
  return supportsMultiple(type) && maxSelect > 1
}
