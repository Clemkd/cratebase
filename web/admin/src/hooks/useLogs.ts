import { useCallback, useEffect, useState } from 'react'
import {
  api,
  describeFailure,
  type LogEntry,
  type LogGranularity,
  type LogLevel,
  type LogStats,
  type Page,
} from '../api'
import { windowStart, type LogWindow } from '../lib/logs'

/** Tranche de temps désignée en cliquant une barre de l'histogramme. */
export interface LogSlice {
  from: string
  to: string
  /** Découpage à appliquer <i>à l'intérieur</i> de la tranche : un cran plus fin que celui d'où elle vient. */
  granularity: LogGranularity
}

export interface LogsQuery {
  page: number
  perPage: number
  /** Niveaux retenus. Vide : tous. */
  levels: LogLevel[]
  method: string
  /** Statut HTTP exact, ou zéro pour tous. */
  status: number
  q: string
  window: LogWindow
  /** Tranche forée, qui prend le pas sur la fenêtre tant qu'elle est posée. */
  slice: LogSlice | null
  /** Du plus ancien au plus récent. Le défaut est l'inverse. */
  ascending: boolean
}

export interface LogsState {
  result: Page<LogEntry> | null
  stats: LogStats | null
  loading: boolean
  error: string | null
  reload: () => Promise<void>
}

/**
 * Granularité de l'histogramme, déduite de la fenêtre.
 *
 * Décidée par le client et non laissée au serveur : c'est le client qui sait combien de barres son
 * écran peut porter, et une heure découpée à l'heure donnerait une barre unique.
 */
function granularityFor(window: LogWindow): LogGranularity {
  if (window === '1h') return 'Minute'
  if (window === '24h') return 'Hour'

  return 'Day'
}

/**
 * Page du journal et histogramme de la même fenêtre.
 *
 * Un seul appel pour les deux : le serveur vide son tampon d'écriture à chaque lecture du journal,
 * donc deux appels successifs ne décrivent pas le même état — l'histogramme affichait dix-neuf
 * entrées au-dessus d'un tableau qui en comptait dix-sept.
 *
 * La borne basse est calculée à l'instant de l'appel, jamais mémorisée dans l'état : une fenêtre
 * glissante figée au montage montrerait « dernière heure » en désignant une heure révolue. Une
 * tranche forée, elle, a des bornes fixes — c'est tout l'intérêt d'en désigner une.
 */
export function useLogs(query: LogsQuery): LogsState {
  const [result, setResult] = useState<Page<LogEntry> | null>(null)
  const [stats, setStats] = useState<LogStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const { page, perPage, levels, method, status, q, window, slice, ascending } = query

  // Les niveaux voyagent en clé de dépendance : un tableau littéral change d'identité à chaque
  // rendu du parent, et l'effet rechargerait le journal en boucle.
  const levelKey = levels.join(',')
  const sliceKey = slice ? `${slice.from}|${slice.to}|${slice.granularity}` : ''

  const reload = useCallback(async () => {
    setLoading(true)

    const bounds = slice
      ? { from: slice.from, to: slice.to, granularity: slice.granularity }
      : { from: windowStart(window), to: undefined, granularity: granularityFor(window) }

    try {
      const { stats: histogram, ...list } = await api.logs.listWithStats({
        levels,
        method: method === '' ? undefined : method,
        // Zéro n'est pas un statut HTTP : c'est ce que portent les entrées d'application, et le
        // filtre ne sait pas les désigner. Il vaut donc « aucun filtre ».
        status: status === 0 ? undefined : status,
        q: q === '' ? undefined : q,
        ...bounds,
        page,
        perPage,
        sort: ascending ? 'created' : '-created',
      })

      setResult(list)
      setStats(histogram)
      setError(null)
    } catch (failure) {
      setError(describeFailure(failure))
    } finally {
      setLoading(false)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `levels` et `slice` passent par leur clé.
  }, [page, perPage, levelKey, method, status, q, window, sliceKey, ascending])

  useEffect(() => {
    void reload()
  }, [reload])

  return { result, stats, loading, error, reload }
}
