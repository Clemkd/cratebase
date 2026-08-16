import { Bug, CircleAlert, Info, TriangleAlert } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { BadgeTone } from '../ui'
import type { LogLevel } from '../api'

/**
 * Presentation of severity levels.
 *
 * A tone <b>and</b> an icon: color alone distinguishes nothing for someone colorblind, and the
 * logs screen is first read by scanning the levels column.
 *
 * Two labels, because the two uses don't call for the same thing. In the table, the levels column
 * repeats on every row: "Warning" would consume width the request path needs far more, and the
 * short form is recognizable anyway from its tone and icon. Everywhere the word is written only
 * once — filter, legend, detail — the full label is used.
 */
export const LEVEL_META: Record<
  LogLevel,
  { label: string; short: string; tone: BadgeTone; icon: LucideIcon }
> = {
  Debug: { label: 'Debug', short: 'DEBUG', tone: 'neutral', icon: Bug },
  Info: { label: 'Information', short: 'INFO', tone: 'brand', icon: Info },
  Warning: { label: 'Warning', short: 'WARN', tone: 'warning', icon: TriangleAlert },
  Error: { label: 'Error', short: 'ERROR', tone: 'danger', icon: CircleAlert },
}

/**
 * Ink color for each level, in literal classes.
 *
 * Written out in full rather than composed on the fly: Tailwind only generates the classes it
 * finds in the sources, and a class assembled at runtime simply doesn't exist in the produced
 * stylesheet.
 */
export const LEVEL_INK: Record<LogLevel, string> = {
  Debug: 'text-ink-faint',
  Info: 'text-brand',
  Warning: 'text-warning',
  Error: 'text-danger',
}

/** Windows offered by the logs screen. */
export type LogWindow = '1h' | '24h' | '7d' | '30d' | 'all'

export const LOG_WINDOWS: { value: LogWindow; label: string; hours: number | null }[] = [
  { value: '1h', label: 'Last hour', hours: 1 },
  { value: '24h', label: '24 hours', hours: 24 },
  { value: '7d', label: '7 days', hours: 24 * 7 },
  { value: '30d', label: '30 days', hours: 24 * 30 },
  { value: 'all', label: 'All', hours: null },
]

/**
 * Lower bound of a window, in ISO-8601.
 *
 * Computed on every call rather than memoized: a memoized sliding window drifts by however long
 * the tab has stayed open, and the histogram ends up showing a range that no longer matches its
 * label.
 */
export function windowStart(value: LogWindow, now = new Date()): string | undefined {
  const window = LOG_WINDOWS.find((entry) => entry.value === value)

  if (!window?.hours) return undefined

  return new Date(now.getTime() - window.hours * 3600 * 1000).toISOString()
}

/** Renders a readable processing duration. */
export function formatDuration(milliseconds: number): string {
  if (milliseconds >= 1000) return `${(milliseconds / 1000).toFixed(2)} s`
  if (milliseconds >= 10) return `${Math.round(milliseconds)} ms`

  return `${milliseconds.toFixed(1)} ms`
}

/** Renders a readable uptime duration. */
export function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)

  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${minutes}min`
  if (minutes > 0) return `${minutes}min`

  return `${Math.floor(seconds)} s`
}

/** Tone of an HTTP status, aligned with the levels' tones. */
export function statusTone(status: number): BadgeTone {
  if (status === 0) return 'neutral'
  if (status >= 500) return 'danger'
  if (status >= 400) return 'warning'
  if (status >= 300) return 'brand'

  return 'success'
}
