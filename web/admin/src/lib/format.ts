const dateTimeFormat = new Intl.DateTimeFormat('fr-FR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

/** Rend un instant ISO-8601 lisible, ou la valeur brute si elle n'en est pas un. */
export function formatDateTime(value: unknown): string {
  if (typeof value !== 'string' || value === '') return ''

  const parsed = new Date(value)

  return Number.isNaN(parsed.getTime()) ? value : dateTimeFormat.format(parsed)
}

/** Convertit un instant ISO-8601 en valeur d'un `input[type=datetime-local]`. */
export function toLocalInputValue(value: unknown): string {
  if (typeof value !== 'string' || value === '') return ''

  const parsed = new Date(value)

  if (Number.isNaN(parsed.getTime())) return ''

  const pad = (part: number) => String(part).padStart(2, '0')

  return (
    `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}` +
    `T${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`
  )
}

/**
 * Convertit une saisie locale en instant ISO-8601 UTC.
 *
 * La normalisation en UTC est faite ici plutôt que laissée au serveur : le champ de saisie ne porte
 * aucun fuseau, donc envoyer sa valeur telle quelle décalerait l'enregistrement de l'écart local.
 */
export function fromLocalInputValue(value: string): string {
  if (value === '') return ''

  const parsed = new Date(value)

  return Number.isNaN(parsed.getTime()) ? '' : parsed.toISOString()
}

/** Nombre au format français, sans séparateur de milliers superflu sur les petits nombres. */
export function formatCount(value: number): string {
  return new Intl.NumberFormat('fr-FR').format(value)
}

/** Accorde un nom au pluriel. */
export function plural(count: number, singular: string, plural: string): string {
  return count > 1 ? plural : singular
}
