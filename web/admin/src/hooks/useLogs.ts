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

/** Time slice designated by clicking a histogram bar. */
export interface LogSlice {
  from: string
  to: string
  /** Bucketing to apply <i>inside</i> the slice: one notch finer than the one it comes from. */
  granularity: LogGranularity
}

export interface LogsQuery {
  page: number
  perPage: number
  /** Levels kept. Empty: all. */
  levels: LogLevel[]
  method: string
  /** Exact HTTP status, or zero for all. */
  status: number
  q: string
  window: LogWindow
  /** Drilled-down slice, which takes precedence over the window whenever it's set. */
  slice: LogSlice | null
  /** Oldest to newest. The default is the reverse. */
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
 * Histogram granularity, derived from the window.
 *
 * Decided by the client rather than left to the server: it's the client that knows how many bars
 * its screen can carry, and an hour bucketed by the hour would give a single bar.
 */
function granularityFor(window: LogWindow): LogGranularity {
  if (window === '1h') return 'Minute'
  if (window === '24h') return 'Hour'

  return 'Day'
}

/**
 * Log page and histogram for the same window.
 *
 * A single call for both: the server drains its write buffer on every log read, so two
 * successive calls don't describe the same state — the histogram would show nineteen entries
 * above a table that counted seventeen.
 *
 * The lower bound is computed at call time, never memoized in state: a sliding window frozen at
 * mount would show "last hour" while pointing at an hour long past. A drilled-down slice, on the
 * other hand, has fixed bounds — that's the whole point of designating one.
 */
export function useLogs(query: LogsQuery): LogsState {
  const [result, setResult] = useState<Page<LogEntry> | null>(null)
  const [stats, setStats] = useState<LogStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const { page, perPage, levels, method, status, q, window, slice, ascending } = query

  // Levels travel as a dependency key: a literal array changes identity on every render of the
  // parent, and the effect would reload the log in a loop.
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
        // Zero isn't an HTTP status: it's what application entries carry, and the filter has no
        // way to designate them. So it means "no filter".
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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `levels` and `slice` travel via their key.
  }, [page, perPage, levelKey, method, status, q, window, sliceKey, ascending])

  useEffect(() => {
    void reload()
  }, [reload])

  return { result, stats, loading, error, reload }
}
