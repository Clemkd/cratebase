const dateTimeFormat = new Intl.DateTimeFormat('en-US', {
  dateStyle: 'short',
  timeStyle: 'short',
})

/** Renders an ISO-8601 instant as readable text, or the raw value if it isn't one. */
export function formatDateTime(value: unknown): string {
  if (typeof value !== 'string' || value === '') return ''

  const parsed = new Date(value)

  return Number.isNaN(parsed.getTime()) ? value : dateTimeFormat.format(parsed)
}

/** Converts an ISO-8601 instant into the value of an `input[type=datetime-local]`. */
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
 * Converts local input into a UTC ISO-8601 instant.
 *
 * Normalization to UTC happens here rather than being left to the server: the input field carries
 * no time zone, so sending its value as-is would shift the record by the local offset.
 */
export function fromLocalInputValue(value: string): string {
  if (value === '') return ''

  const parsed = new Date(value)

  return Number.isNaN(parsed.getTime()) ? '' : parsed.toISOString()
}

/** Number formatted for English, without a superfluous thousands separator on small numbers. */
export function formatCount(value: number): string {
  return new Intl.NumberFormat('en-US').format(value)
}

/** Agrees a noun with its count. */
export function plural(count: number, singular: string, plural: string): string {
  return count > 1 ? plural : singular
}

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB']

/**
 * Readable size, in decimal units.
 *
 * A thousand, not 1,024: that's what file systems and object storage invoices report, so it's
 * the figure the operator compares against their own.
 */
export function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return '0 B'

  const exponent = Math.min(Math.floor(Math.log10(value) / 3), UNITS.length - 1)
  const scaled = value / 1000 ** exponent

  return `${scaled.toFixed(exponent === 0 || scaled >= 100 ? 0 : 1)} ${UNITS[exponent]}`
}
