import type { Collection, Field, FieldType, RecordValue } from '../api'

/** Geographic point as the server serializes it (longitude first, as in GeoJSON). */
export interface GeoPoint {
  longitude: number
  latitude: number
}

/**
 * Types offered when creating a field, in the expected order of use.
 *
 * `AutoDate` is included: it's the type of the `created` and `updated` fields, and nothing
 * justifies reserving it for the engine — a "last seen", an "archived on" describe themselves
 * exactly the same way, and writing them by hand in every form amounts to reimplementing what
 * the engine already does.
 */
export const CREATABLE_TYPES: FieldType[] = [
  'Text',
  'Editor',
  'Number',
  'Bool',
  'Email',
  'Url',
  'Date',
  'AutoDate',
  'Select',
  'Relation',
  'Json',
  'GeoPoint',
]

const TYPE_LABELS: Record<FieldType, string> = {
  Text: 'Text',
  Editor: 'Rich text',
  Number: 'Number',
  Bool: 'Boolean',
  Email: 'Email',
  Url: 'URL',
  Date: 'Date',
  AutoDate: 'Auto date',
  Select: 'Select',
  File: 'File',
  Relation: 'Relation',
  Json: 'JSON',
  GeoPoint: 'Geo point',
}

export function typeLabel(type: FieldType): string {
  return TYPE_LABELS[type]
}

/** Does the type support multiple values? Must stay aligned with `FieldTypeInfo.SupportsMultiple`. */
export function supportsMultiple(type: FieldType): boolean {
  return type === 'Text' || type === 'Select' || type === 'File' || type === 'Relation'
}

/**
 * Zero value of a field, aligned with `FieldTypeInfo.ZeroValue`.
 *
 * Only `Json` allows `null`: everywhere else the engine removes the "absent" / "empty"
 * distinction, and a form that reintroduced `null` would produce ambiguous filters.
 */
export function zeroValue(field: Field): unknown {
  if (field.type === 'Json') return null
  if (field.multiple) return []

  switch (field.type) {
    case 'Bool':
      return false
    case 'Number':
      return 0
    case 'GeoPoint':
      return { longitude: 0, latitude: 0 } satisfies GeoPoint
    default:
      return ''
  }
}

/**
 * Is the field editable from a form?
 *
 * The server validator rejects all system fields except `email` and `email_visibility`; the
 * password is handled separately by the auth hook. Offering them for input would give the
 * illusion that they're saved.
 */
export function isEditable(field: Field): boolean {
  if (!field.isSystem) return field.type !== 'File'

  return field.name === 'email' || field.name === 'email_visibility'
}

/** Fields displayable as a column: everything except hidden fields (password, token key). */
export function visibleFields(collection: Collection): Field[] {
  return collection.fields.filter((field) => !field.hidden)
}

/** Fields declared by the user, in schema order. */
export function userFields(collection: Collection): Field[] {
  return collection.fields.filter((field) => !field.isSystem)
}

/** Starting values for a record form. */
export function initialFormValues(collection: Collection, record: RecordValue | null): RecordValue {
  const values: RecordValue = {}

  for (const field of collection.fields) {
    if (!isEditable(field)) continue

    const existing = record?.[field.name]

    values[field.name] = existing === undefined || existing === null
      ? zeroValue(field)
      : existing
  }

  return values
}

/** Coerces a form value into the shape expected by the API. */
export function toPayloadValue(field: Field, value: unknown): unknown {
  if (field.type === 'Number' && typeof value === 'string') {
    return value.trim() === '' ? 0 : Number(value)
  }

  return value
}

/** List of strings, whatever shape was received. */
export function asStringList(value: unknown): string[] {
  if (Array.isArray(value)) return value.map((item) => String(item))
  if (value === null || value === undefined || value === '') return []

  return [String(value)]
}

export function asGeoPoint(value: unknown): GeoPoint {
  if (typeof value === 'object' && value !== null) {
    const point = value as Partial<GeoPoint>

    return {
      longitude: typeof point.longitude === 'number' ? point.longitude : 0,
      latitude: typeof point.latitude === 'number' ? point.latitude : 0,
    }
  }

  return { longitude: 0, latitude: 0 }
}

/** Field most representative of a record, used to label a relation. */
export function labelField(collection: Collection): Field | undefined {
  const candidates = collection.fields.filter(
    (field) => !field.hidden && !field.multiple && (field.type === 'Text' || field.type === 'Email'),
  )

  return candidates.find((field) => !field.isSystem) ?? candidates[0]
}
