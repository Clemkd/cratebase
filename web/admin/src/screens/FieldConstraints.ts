import type { FieldOptions, FieldType } from '../api'
import { supportsMultiple } from '../lib/fields'

/**
 * Constraints on a field, described once and for all.
 *
 * The schema editor displays one row per field: type-specific settings therefore can no longer
 * sprawl across a form. They become chips placed in a cell, added from a list that only offers
 * what the type admits — a maximum length on a boolean makes no sense and must not even appear.
 *
 * Each descriptor knows how to read, write, and clear its constraint: the rest of the screen
 * manipulates descriptors, never the keys of `FieldOptions` one by one, which avoids missing a
 * case on a type change.
 */

/** What's being edited in the chip: determines the rendered control. */
export type ConstraintKind = 'number' | 'text' | 'list' | 'flag' | 'collection'

/** Portion of the draft a constraint can modify. */
export interface ConstraintTarget {
  maxSelect: number
  options: FieldOptions
}

export interface ConstraintDescriptor {
  /** Unique identifier: two constraints can target the same key on different types. */
  id: string
  label: string
  /**
   * Short label for the chip, in the fields table.
   *
   * The add list has the full width of a flyout and can afford to be explicit; the chip, on the
   * other hand, shares a cell with four others and must fit on the same line.
   */
  short?: string
  kind: ConstraintKind
  types: FieldType[]
  /** Is the constraint set on this field? */
  isSet: (target: ConstraintTarget) => boolean
  /** Value as it's edited, as text. */
  read: (target: ConstraintTarget) => string
  /** Writes an entered value. */
  write: (target: ConstraintTarget, raw: string) => ConstraintTarget
  /** Sets the constraint with its starting value. */
  add: (target: ConstraintTarget) => ConstraintTarget
  /** Removes the constraint. */
  clear: (target: ConstraintTarget) => ConstraintTarget
}

function withOption<K extends keyof FieldOptions>(
  target: ConstraintTarget,
  key: K,
  value: FieldOptions[K],
): ConstraintTarget {
  const options: FieldOptions = { ...target.options }

  options[key] = value

  return { ...target, options }
}

function numberConstraint(
  id: string,
  label: string,
  types: FieldType[],
  key: 'min' | 'max' | 'maxFileSize',
  initial: number,
): ConstraintDescriptor {
  return {
    id,
    label,
    kind: 'number',
    types,
    isSet: ({ options }) => options[key] !== null && options[key] !== undefined,
    read: ({ options }) => (options[key] === null || options[key] === undefined ? '' : String(options[key])),
    // A field cleared mid-typing remains a set constraint, without a value: removing it on the
    // last character deleted would make the chip vanish under the user's fingers.
    write: (target, raw) => withOption(target, key, raw === '' ? null : Number(raw)),
    add: (target) => withOption(target, key, initial),
    clear: (target) => withOption(target, key, null),
  }
}

function textConstraint(
  id: string,
  label: string,
  types: FieldType[],
  key: 'pattern',
): ConstraintDescriptor {
  return {
    id,
    label,
    kind: 'text',
    types,
    isSet: ({ options }) => options[key] !== null && options[key] !== undefined,
    read: ({ options }) => options[key] ?? '',
    write: (target, raw) => withOption(target, key, raw),
    add: (target) => withOption(target, key, ''),
    clear: (target) => withOption(target, key, null),
  }
}

function listConstraint(
  id: string,
  label: string,
  types: FieldType[],
  key: 'values' | 'mimeTypes' | 'thumbSizes',
): ConstraintDescriptor {
  return {
    id,
    label,
    kind: 'list',
    types,
    isSet: ({ options }) => options[key] !== undefined,
    read: ({ options }) => (options[key] ?? []).join(', '),
    write: (target, raw) =>
      withOption(
        target,
        key,
        raw
          .split(',')
          .map((entry) => entry.trim())
          .filter(Boolean),
      ),
    add: (target) => withOption(target, key, []),
    clear: (target) => withOption(target, key, undefined),
  }
}

function flagConstraint(
  id: string,
  label: string,
  types: FieldType[],
  key: 'integerOnly' | 'cascadeDelete' | 'protected' | 'onCreate' | 'onUpdate',
): ConstraintDescriptor {
  return {
    id,
    label,
    kind: 'flag',
    types,
    isSet: ({ options }) => options[key] === true,
    read: () => '',
    write: (target) => target,
    add: (target) => withOption(target, key, true),
    clear: (target) => withOption(target, key, false),
  }
}

const ALL_TYPES: FieldType[] = [
  'Text', 'Editor', 'Number', 'Bool', 'Email', 'Url',
  'Date', 'AutoDate', 'Select', 'File', 'Relation', 'Json', 'GeoPoint',
]

/** Multi-value types, derived from `supportsMultiple` to avoid maintaining two lists. */
const MULTIPLE_TYPES: FieldType[] = ALL_TYPES.filter(supportsMultiple)

/**
 * Catalog of constraints, in the order they're offered.
 *
 * `Relation` carries its target collection here rather than in a dedicated column: it's a field
 * constraint just like the others, and reserving a column for it would leave an empty cell on
 * every row that isn't a relation.
 */
/**
 * Chip abbreviations, indexed by constraint.
 *
 * Gathered here rather than passed to each factory: they're labels, not an aspect of behavior,
 * and keeping them side by side is the only way to check at a glance that they stay distinct
 * from one another.
 */
const SHORT_LABELS: Record<string, string> = {
  textMin: 'Min length',
  textMax: 'Max length',
  numberMin: 'Min',
  numberMax: 'Max',
  integerOnly: 'Integers',
  values: 'Values',
  cascade: 'Cascade',
  fileSize: 'Max size',
  mime: 'MIME',
  thumbs: 'Thumbnails',
  protected: 'Protected',
  onCreate: 'On create',
  onUpdate: 'On update',
  maxSelect: 'Max',
}

/** Label for the chip: the short form if it exists, the full label otherwise. */
export function constraintShort(constraint: ConstraintDescriptor): string {
  return constraint.short ?? constraint.label
}

const CATALOGUE: ConstraintDescriptor[] = [
  numberConstraint('textMin', 'Min length', ['Text', 'Editor'], 'min', 1),
  numberConstraint('textMax', 'Max length', ['Text', 'Editor'], 'max', 255),
  textConstraint('pattern', 'Pattern', ['Text'], 'pattern'),

  numberConstraint('numberMin', 'Min value', ['Number'], 'min', 0),
  numberConstraint('numberMax', 'Max value', ['Number'], 'max', 100),
  flagConstraint('integerOnly', 'Integers only', ['Number'], 'integerOnly'),

  listConstraint('values', 'Allowed values', ['Select'], 'values'),

  {
    id: 'target',
    label: 'Target',
    kind: 'collection',
    types: ['Relation'],
    isSet: ({ options }) => options.targetCollection !== null && options.targetCollection !== undefined,
    read: ({ options }) => options.targetCollection ?? '',
    write: (target, raw) => withOption(target, 'targetCollection', raw),
    add: (target) => withOption(target, 'targetCollection', ''),
    clear: (target) => withOption(target, 'targetCollection', null),
  },
  flagConstraint('cascade', 'Cascade delete', ['Relation'], 'cascadeDelete'),

  numberConstraint('fileSize', 'Max size (bytes)', ['File'], 'maxFileSize', 5_242_880),
  listConstraint('mime', 'MIME types', ['File'], 'mimeTypes'),
  listConstraint('thumbs', 'Thumbnails', ['File'], 'thumbSizes'),
  flagConstraint('protected', 'Protected file', ['File'], 'protected'),

  flagConstraint('onCreate', 'Set on create', ['AutoDate'], 'onCreate'),
  flagConstraint('onUpdate', 'Set on update', ['AutoDate'], 'onUpdate'),

  {
    id: 'maxSelect',
    label: 'Max values',
    kind: 'number',
    types: MULTIPLE_TYPES,
    // `maxSelect` of 1 isn't a constraint but the absence of one: it's the simple field, the
    // default case for every type.
    isSet: ({ maxSelect }) => maxSelect > 1,
    read: ({ maxSelect }) => String(maxSelect),
    write: (target, raw) => ({ ...target, maxSelect: Math.max(1, Number(raw) || 1) }),
    add: (target) => ({ ...target, maxSelect: 2 }),
    clear: (target) => ({ ...target, maxSelect: 1 }),
  },
]

export const CONSTRAINTS: ConstraintDescriptor[] = CATALOGUE.map((constraint) => ({
  ...constraint,
  short: SHORT_LABELS[constraint.id],
}))

/** Constraints this field type admits. */
export function constraintsFor(type: FieldType): ConstraintDescriptor[] {
  return CONSTRAINTS.filter((constraint) => constraint.types.includes(type))
}

/** Constraints that are both set and applicable, in catalog order. */
export function activeConstraints(type: FieldType, target: ConstraintTarget): ConstraintDescriptor[] {
  return constraintsFor(type).filter((constraint) => constraint.isSet(target))
}

/**
 * Removes constraints the new type doesn't admit.
 *
 * Also returns the list of what was removed: the change must not be silent, otherwise switching
 * back and forth between two types would erase settings without leaving a trace.
 */
export function retype(
  target: ConstraintTarget,
  previous: FieldType,
  next: FieldType,
): { target: ConstraintTarget; dropped: string[] } {
  const admitted = new Set(constraintsFor(next).map((constraint) => constraint.id))
  const dropped: string[] = []

  let result = target

  for (const constraint of constraintsFor(previous)) {
    if (admitted.has(constraint.id) || !constraint.isSet(result)) continue

    result = constraint.clear(result)
    dropped.push(constraint.label)
  }

  // A relation without a target isn't savable: the chip is added by default so the choice is
  // visible instead of being demanded by a server error.
  if (next === 'Relation' && result.options.targetCollection === undefined) {
    result = withOption(result, 'targetCollection', '')
  }

  return { target: result, dropped }
}

/** Constraint rendered as one line, for system fields that aren't editable. */
export function describeConstraint(
  constraint: ConstraintDescriptor,
  target: ConstraintTarget,
): string {
  const label = constraintShort(constraint)

  if (constraint.kind === 'flag') return label

  const value = constraint.read(target)

  return value === '' ? label : `${label}: ${value}`
}
