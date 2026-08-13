import type { FieldOptions, FieldType } from '../api'
import { supportsMultiple } from '../lib/fields'

/**
 * Contraintes d'un champ, décrites une fois pour toutes.
 *
 * L'éditeur de schéma affiche une ligne par champ : les réglages propres au type ne peuvent donc
 * plus s'étaler en formulaire. Ils deviennent des jetons posés dans une cellule, ajoutés depuis une
 * liste qui ne propose que ce que le type admet — une longueur maximale sur un booléen n'a aucun
 * sens et ne doit même pas apparaître.
 *
 * Chaque descripteur sait lire, écrire et effacer sa contrainte : le reste de l'écran manipule des
 * descripteurs, jamais les clés de `FieldOptions` une à une, ce qui évite d'oublier un cas au
 * changement de type.
 */

/** Ce qu'on édite dans le jeton : détermine le contrôle rendu. */
export type ConstraintKind = 'number' | 'text' | 'list' | 'flag' | 'collection'

/** Portion du brouillon qu'une contrainte peut modifier. */
export interface ConstraintTarget {
  maxSelect: number
  options: FieldOptions
}

export interface ConstraintDescriptor {
  /** Identifiant unique : deux contraintes peuvent viser la même clé sur des types différents. */
  id: string
  label: string
  /**
   * Libellé abrégé du jeton, dans le tableau des champs.
   *
   * La liste d'ajout dispose de toute la largeur d'une bulle et peut se permettre d'être explicite ;
   * le jeton, lui, partage une cellule avec quatre autres et doit tenir sur la même ligne.
   */
  short?: string
  kind: ConstraintKind
  types: FieldType[]
  /** La contrainte est-elle posée sur ce champ ? */
  isSet: (target: ConstraintTarget) => boolean
  /** Valeur telle qu'elle s'édite, sous forme de texte. */
  read: (target: ConstraintTarget) => string
  /** Écrit une valeur saisie. */
  write: (target: ConstraintTarget, raw: string) => ConstraintTarget
  /** Pose la contrainte avec sa valeur de départ. */
  add: (target: ConstraintTarget) => ConstraintTarget
  /** Retire la contrainte. */
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
    // Une case vidée en cours de frappe reste une contrainte posée, sans valeur : la retirer à la
    // dernière touche effacée ferait disparaître le jeton sous les doigts de l'utilisateur.
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

/** Types multi-valués, dérivés de `supportsMultiple` pour ne pas tenir deux listes. */
const MULTIPLE_TYPES: FieldType[] = ALL_TYPES.filter(supportsMultiple)

/**
 * Catalogue des contraintes, dans l'ordre où elles sont proposées.
 *
 * `Relation` porte sa collection cible ici plutôt que dans une colonne dédiée : c'est une
 * contrainte du champ au même titre que les autres, et lui réserver une colonne laisserait une
 * case vide sur toutes les lignes qui ne sont pas des relations.
 */
/**
 * Abrégés des jetons, indexés par contrainte.
 *
 * Rassemblés ici plutôt que passés à chaque fabrique : ce sont des libellés, pas un aspect du
 * comportement, et les tenir côte à côte est le seul moyen de vérifier d'un coup d'œil qu'ils
 * restent distincts les uns des autres.
 */
const SHORT_LABELS: Record<string, string> = {
  textMin: 'Long. min',
  textMax: 'Long. max',
  numberMin: 'Min',
  numberMax: 'Max',
  integerOnly: 'Entiers',
  values: 'Valeurs',
  cascade: 'Cascade',
  fileSize: 'Taille max',
  mime: 'MIME',
  thumbs: 'Vignettes',
  protected: 'Protégé',
  onCreate: 'À la création',
  onUpdate: 'À la modification',
  maxSelect: 'Max',
}

/** Libellé du jeton : l'abrégé s'il existe, le libellé complet sinon. */
export function constraintShort(constraint: ConstraintDescriptor): string {
  return constraint.short ?? constraint.label
}

const CATALOGUE: ConstraintDescriptor[] = [
  numberConstraint('textMin', 'Longueur min', ['Text', 'Editor'], 'min', 1),
  numberConstraint('textMax', 'Longueur max', ['Text', 'Editor'], 'max', 255),
  textConstraint('pattern', 'Motif', ['Text'], 'pattern'),

  numberConstraint('numberMin', 'Valeur min', ['Number'], 'min', 0),
  numberConstraint('numberMax', 'Valeur max', ['Number'], 'max', 100),
  flagConstraint('integerOnly', 'Entiers seulement', ['Number'], 'integerOnly'),

  listConstraint('values', 'Valeurs admises', ['Select'], 'values'),

  {
    id: 'target',
    label: 'Cible',
    kind: 'collection',
    types: ['Relation'],
    isSet: ({ options }) => options.targetCollection !== null && options.targetCollection !== undefined,
    read: ({ options }) => options.targetCollection ?? '',
    write: (target, raw) => withOption(target, 'targetCollection', raw),
    add: (target) => withOption(target, 'targetCollection', ''),
    clear: (target) => withOption(target, 'targetCollection', null),
  },
  flagConstraint('cascade', 'Suppression en cascade', ['Relation'], 'cascadeDelete'),

  numberConstraint('fileSize', 'Taille max (octets)', ['File'], 'maxFileSize', 5_242_880),
  listConstraint('mime', 'Types MIME', ['File'], 'mimeTypes'),
  listConstraint('thumbs', 'Vignettes', ['File'], 'thumbSizes'),
  flagConstraint('protected', 'Fichier protégé', ['File'], 'protected'),

  flagConstraint('onCreate', 'Posée à la création', ['AutoDate'], 'onCreate'),
  flagConstraint('onUpdate', 'Posée à la modification', ['AutoDate'], 'onUpdate'),

  {
    id: 'maxSelect',
    label: 'Max valeurs',
    kind: 'number',
    types: MULTIPLE_TYPES,
    // `maxSelect` à 1 n'est pas une contrainte mais l'absence de contrainte : c'est le champ
    // simple, cas par défaut de tous les types.
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

/** Contraintes que ce type de champ admet. */
export function constraintsFor(type: FieldType): ConstraintDescriptor[] {
  return CONSTRAINTS.filter((constraint) => constraint.types.includes(type))
}

/** Contraintes posées et applicables, dans l'ordre du catalogue. */
export function activeConstraints(type: FieldType, target: ConstraintTarget): ConstraintDescriptor[] {
  return constraintsFor(type).filter((constraint) => constraint.isSet(target))
}

/**
 * Retire les contraintes que le nouveau type n'admet pas.
 *
 * Rend aussi la liste de ce qui a été retiré : le changement ne doit pas être silencieux, sans quoi
 * un aller-retour entre deux types effacerait des réglages sans laisser de trace.
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

  // Une relation sans cible n'est pas enregistrable : le jeton est posé d'office pour que le choix
  // soit visible plutôt que réclamé par une erreur du serveur.
  if (next === 'Relation' && result.options.targetCollection === undefined) {
    result = withOption(result, 'targetCollection', '')
  }

  return { target: result, dropped }
}

/** Contrainte rendue en une ligne, pour les champs système qui ne s'éditent pas. */
export function describeConstraint(
  constraint: ConstraintDescriptor,
  target: ConstraintTarget,
): string {
  const label = constraintShort(constraint)

  if (constraint.kind === 'flag') return label

  const value = constraint.read(target)

  return value === '' ? label : `${label} : ${value}`
}
