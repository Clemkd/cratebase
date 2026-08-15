import { Bug, CircleAlert, Info, TriangleAlert } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { BadgeTone } from '../ui'
import type { LogLevel } from '../api'

/**
 * Présentation des niveaux de gravité.
 *
 * Une teinte <b>et</b> une icône : la couleur seule ne distingue rien pour un daltonien, et
 * l'écran des journaux se lit d'abord en balayant la colonne des niveaux.
 *
 * Deux libellés, parce que les deux emplois ne demandent pas la même chose. Dans le tableau, la
 * colonne des niveaux est répétée à chaque ligne : « Avertissement » y consomme une largeur que le
 * chemin de la requête réclame bien davantage, et la forme abrégée se reconnaît de toute façon à sa
 * teinte et à son icône. Partout où le mot n'est écrit qu'une fois — filtre, légende, détail — c'est
 * le libellé entier qui sert.
 */
export const LEVEL_META: Record<
  LogLevel,
  { label: string; short: string; tone: BadgeTone; icon: LucideIcon }
> = {
  Debug: { label: 'Débogage', short: 'DEBUG', tone: 'neutral', icon: Bug },
  Info: { label: 'Information', short: 'INFO', tone: 'brand', icon: Info },
  Warning: { label: 'Avertissement', short: 'ALERTE', tone: 'warning', icon: TriangleAlert },
  Error: { label: 'Erreur', short: 'ERREUR', tone: 'danger', icon: CircleAlert },
}

/**
 * Encre de chaque niveau, en classes littérales.
 *
 * Écrites en toutes lettres et non composées à la volée : Tailwind ne génère que les classes qu'il
 * trouve dans les sources, et une classe assemblée à l'exécution n'existe simplement pas dans la
 * feuille produite.
 */
export const LEVEL_INK: Record<LogLevel, string> = {
  Debug: 'text-ink-faint',
  Info: 'text-brand',
  Warning: 'text-warning',
  Error: 'text-danger',
}

/** Fenêtres proposées par l'écran des journaux. */
export type LogWindow = '1h' | '24h' | '7d' | '30d' | 'all'

export const LOG_WINDOWS: { value: LogWindow; label: string; hours: number | null }[] = [
  { value: '1h', label: 'Dernière heure', hours: 1 },
  { value: '24h', label: '24 heures', hours: 24 },
  { value: '7d', label: '7 jours', hours: 24 * 7 },
  { value: '30d', label: '30 jours', hours: 24 * 30 },
  { value: 'all', label: 'Tout', hours: null },
]

/**
 * Borne basse d'une fenêtre, en ISO-8601.
 *
 * Calculée à chaque appel plutôt que mémorisée : une fenêtre glissante mémorisée se décale de la
 * durée pendant laquelle l'onglet est resté ouvert, et l'histogramme finit par montrer une plage
 * qui ne correspond plus à son libellé.
 */
export function windowStart(value: LogWindow, now = new Date()): string | undefined {
  const window = LOG_WINDOWS.find((entry) => entry.value === value)

  if (!window?.hours) return undefined

  return new Date(now.getTime() - window.hours * 3600 * 1000).toISOString()
}

/** Rend une durée de traitement lisible. */
export function formatDuration(milliseconds: number): string {
  if (milliseconds >= 1000) return `${(milliseconds / 1000).toFixed(2)} s`
  if (milliseconds >= 10) return `${Math.round(milliseconds)} ms`

  return `${milliseconds.toFixed(1)} ms`
}

/** Rend une durée de fonctionnement lisible. */
export function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)

  if (days > 0) return `${days} j ${hours} h`
  if (hours > 0) return `${hours} h ${minutes} min`
  if (minutes > 0) return `${minutes} min`

  return `${Math.floor(seconds)} s`
}

/** Teinte d'un statut HTTP, alignée sur celle des niveaux. */
export function statusTone(status: number): BadgeTone {
  if (status === 0) return 'neutral'
  if (status >= 500) return 'danger'
  if (status >= 400) return 'warning'
  if (status >= 300) return 'brand'

  return 'success'
}
