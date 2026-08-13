import type { Collection, Field, FieldType, RecordValue } from '../api'

/** Point géographique tel que le serveur le sérialise (longitude d'abord, comme en GeoJSON). */
export interface GeoPoint {
  longitude: number
  latitude: number
}

/** Types proposables à la création d'un champ, dans l'ordre d'usage attendu. */
export const CREATABLE_TYPES: FieldType[] = [
  'Text',
  'Editor',
  'Number',
  'Bool',
  'Email',
  'Url',
  'Date',
  'Select',
  'Relation',
  'Json',
  'GeoPoint',
]

const TYPE_LABELS: Record<FieldType, string> = {
  Text: 'Texte',
  Editor: 'Texte enrichi',
  Number: 'Nombre',
  Bool: 'Booléen',
  Email: 'Courriel',
  Url: 'URL',
  Date: 'Date',
  AutoDate: 'Date automatique',
  Select: 'Liste fermée',
  File: 'Fichier',
  Relation: 'Relation',
  Json: 'JSON',
  GeoPoint: 'Point géographique',
}

export function typeLabel(type: FieldType): string {
  return TYPE_LABELS[type]
}

/** Le type admet-il plusieurs valeurs ? Doit rester aligné sur `FieldTypeInfo.SupportsMultiple`. */
export function supportsMultiple(type: FieldType): boolean {
  return type === 'Text' || type === 'Select' || type === 'File' || type === 'Relation'
}

/**
 * Valeur nulle d'un champ, alignée sur `FieldTypeInfo.ZeroValue`.
 *
 * Seul `Json` admet `null` : partout ailleurs le moteur supprime la distinction « absent » /
 * « vide », et un formulaire qui réintroduirait `null` produirait des filtres ambigus.
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
 * Le champ est-il modifiable depuis un formulaire ?
 *
 * Le validateur du serveur écarte tous les champs système sauf `email` et `emailVisibility` ; le
 * mot de passe est traité à part par le crochet d'authentification. Les proposer à la saisie
 * donnerait l'illusion qu'ils sont enregistrés.
 */
export function isEditable(field: Field): boolean {
  if (!field.isSystem) return field.type !== 'File'

  return field.name === 'email' || field.name === 'emailVisibility'
}

/** Champs affichables en colonne : tout sauf les champs masqués (mot de passe, clé de jeton). */
export function visibleFields(collection: Collection): Field[] {
  return collection.fields.filter((field) => !field.hidden)
}

/** Champs déclarés par l'utilisateur, dans l'ordre du schéma. */
export function userFields(collection: Collection): Field[] {
  return collection.fields.filter((field) => !field.isSystem)
}

/** Valeurs de départ d'un formulaire d'enregistrement. */
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

/** Coerce une valeur de formulaire vers la forme attendue par l'API. */
export function toPayloadValue(field: Field, value: unknown): unknown {
  if (field.type === 'Number' && typeof value === 'string') {
    return value.trim() === '' ? 0 : Number(value)
  }

  return value
}

/** Liste de chaînes, quelle que soit la forme reçue. */
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

/** Champ le plus représentatif d'un enregistrement, pour étiqueter une relation. */
export function labelField(collection: Collection): Field | undefined {
  const candidates = collection.fields.filter(
    (field) => !field.hidden && !field.multiple && (field.type === 'Text' || field.type === 'Email'),
  )

  return candidates.find((field) => !field.isSystem) ?? candidates[0]
}
