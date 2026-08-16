import { useMemo } from 'react'
import {
  Bar,
  BarChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { LOG_LEVELS, type LogGranularity, type LogLevel, type LogStats } from '../api'
import { LEVEL_META } from '../lib/logs'
import { formatCount } from '../lib/format'
import { cn } from '../ui'

/** Duration of a bucket, in milliseconds. */
const STEP: Record<LogGranularity, number> = {
  Minute: 60_000,
  Hour: 3_600_000,
  Day: 86_400_000,
}

/** Bucketing applied inside a bucket being drilled into. Minute no longer subdivides. */
const FINER: Record<LogGranularity, LogGranularity> = {
  Day: 'Hour',
  Hour: 'Minute',
  Minute: 'Minute',
}

/**
 * Bounds of a bucket, as the filter expects them.
 *
 * The upper bound is excluded, as it is server-side: two neighboring buckets would otherwise
 * share the entry sitting exactly on their boundary, and the sum of the bars would exceed the
 * total.
 */
export function bucketRange(at: number, granularity: LogGranularity) {
  return {
    from: new Date(at).toISOString(),
    to: new Date(at + STEP[granularity]).toISOString(),
    granularity: FINER[granularity],
  }
}

/**
 * Maximum number of bars.
 *
 * Beyond this, each bar measures less than a pixel: the chart stops being readable before it
 * stops being computable. The oldest buckets are then discarded, not aggregated — silently
 * aggregating would change the scale with nothing indicating it.
 */
const MAX_COLUMNS = 180

/**
 * Tone for each level.
 *
 * CSS variables rather than fixed values: they're the same tokens as the rest of the console, so
 * the chart follows the theme without any code having to listen for the change. A value computed
 * in JavaScript would instead stay stuck on whatever theme was active at mount.
 */
const BAR_FILLS: Record<LogLevel, string> = {
  Debug: 'var(--color-border-strong)',
  Info: 'var(--color-brand)',
  Warning: 'var(--color-warning)',
  Error: 'var(--color-danger)',
}

/** Equivalent classes, for the legend — a flat HTML fill can't read an SVG `fill`. */
const LEGEND_TONES: Record<LogLevel, string> = {
  Debug: 'bg-border-strong',
  Info: 'bg-brand',
  Warning: 'bg-warning',
  Error: 'bg-danger',
}

/**
 * Stacking order, from the bottom of the bar to the top.
 *
 * Recharts stacks in declaration order: the most severe is declared last, so it's drawn on top,
 * right where the eye finds it without looking.
 */
const STACK: LogLevel[] = ['Debug', 'Info', 'Warning', 'Error']

interface Column extends Record<LogLevel, number> {
  at: number
  total: number
}

function emptyCounts(): Record<LogLevel, number> {
  return { Debug: 0, Info: 0, Warning: 0, Error: 0 }
}

/**
 * Reconstructs the full sequence of buckets.
 *
 * The server only returns non-empty buckets: displaying them as-is would place two hours three
 * days apart right next to each other, and the chart would misrepresent the pace of events. Gaps
 * are therefore restored here, with their real width.
 */
function buildColumns(stats: LogStats | null): Column[] {
  if (!stats || stats.items.length === 0) return []

  const step = STEP[stats.granularity]
  const counts = new Map<number, Record<LogLevel, number>>()

  for (const item of stats.items) {
    const at = Date.parse(item.bucket)

    if (Number.isNaN(at)) continue

    const bucket = counts.get(at) ?? emptyCounts()

    bucket[item.level] += item.count
    counts.set(at, bucket)
  }

  if (counts.size === 0) return []

  const times = [...counts.keys()]
  const truncate = (value: number) => Math.floor(value / step) * step

  const ceiling = stats.to ? Date.parse(stats.to) : Number.NaN

  // Without an upper bound, the last bucket is now, not the last populated bucket: a chart that
  // stops at the last error implies the silence that followed doesn't exist. With an upper bound
  // — a bucket drilled into by a click — it's that bound that closes the series: extending it to
  // now would push the whole window off the chart, which would render empty while the table
  // below it shows rows.
  const end = Number.isNaN(ceiling)
    ? Math.max(truncate(Date.now()), ...times)
    : truncate(ceiling - 1)

  const requested = stats.from ? truncate(Date.parse(stats.from)) : Math.min(...times)
  const start = Math.max(
    Number.isNaN(requested) ? Math.min(...times) : requested,
    end - (MAX_COLUMNS - 1) * step,
  )

  const columns: Column[] = []

  for (let at = start; at <= end; at += step) {
    const bucket = counts.get(at) ?? emptyCounts()
    const total = LOG_LEVELS.reduce((sum, level) => sum + bucket[level], 0)

    columns.push({ at, ...bucket, total })
  }

  return columns
}

const LABEL_FORMATS: Record<LogGranularity, Intl.DateTimeFormatOptions> = {
  Minute: { hour: '2-digit', minute: '2-digit' },
  Hour: { day: '2-digit', month: '2-digit', hour: '2-digit' },
  Day: { day: '2-digit', month: '2-digit' },
}

/**
 * Bucket heading in the tooltip, more explicit than the axis tick.
 *
 * None of these templates mix `dateStyle` with individual components: `Intl.DateTimeFormat`
 * rejects that mix at construction, and the tooltip would crash the whole screen on first hover.
 */
const DETAIL_FORMATS: Record<LogGranularity, Intl.DateTimeFormatOptions> = {
  Minute: { dateStyle: 'short', timeStyle: 'short' },
  Hour: { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' },
  Day: { dateStyle: 'full' },
}

/**
 * A slice of the hovered bar, as Recharts hands it over.
 *
 * Redeclared here rather than imported: the library's type carries a dozen generic fields of
 * which the tooltip uses only two, and depending on it would make upgrading Recharts more
 * expensive than it needs to be.
 */
interface Slice {
  /** `unknown` because Recharts also allows an accessor function; here it's always a level. */
  dataKey?: unknown
  value?: unknown
}

/**
 * Detail tooltip for a bucket.
 *
 * It carries the count for each level rather than just the total: knowing a spike is worth
 * three hundred requests teaches nothing; knowing two hundred eighty are errors does.
 */
function HistogramTooltip({
  active,
  payload,
  label,
  granularity,
}: {
  active?: boolean
  payload?: readonly Slice[]
  label?: number | string
  granularity: LogGranularity
}) {
  if (!active || !payload || payload.length === 0) return null

  const at = typeof label === 'number' ? label : Number(label)
  const detail = new Intl.DateTimeFormat('en-US', DETAIL_FORMATS[granularity])

  const rows = STACK.map((level) => {
    const value = payload.find((slice) => slice.dataKey === level)?.value

    return { level, count: typeof value === 'number' ? value : 0 }
  })
    .filter((row) => row.count > 0)
    .reverse()

  const total = rows.reduce((sum, row) => sum + row.count, 0)

  return (
    <div className="rounded-[var(--radius-control)] border border-border-subtle bg-surface-raised px-3 py-2 shadow-popover">
      <p className="text-[11px] text-ink-muted">
        {Number.isNaN(at) ? '' : detail.format(at)}
      </p>

      {total === 0 ? (
        <p className="mt-1 text-xs text-ink-faint">No entries</p>
      ) : (
        <ul className="mt-1 space-y-0.5">
          {rows.map((row) => (
            <li key={row.level} className="flex items-center gap-2 text-xs">
              <span
                aria-hidden="true"
                className={cn('size-2 shrink-0 rounded-sm', LEGEND_TONES[row.level])}
              />
              <span className="flex-1 text-ink-muted">{LEVEL_META[row.level].label}</span>
              <span className="tabular-nums text-ink">{formatCount(row.count)}</span>
            </li>
          ))}
        </ul>
      )}

      {rows.length > 1 && (
        <p className="mt-1.5 border-t border-border-subtle pt-1 text-right text-xs tabular-nums text-ink">
          {formatCount(total)} total
        </p>
      )}
    </div>
  )
}

/**
 * Log volume histogram.
 *
 * Stacked by level rather than overlaid: what you're looking for at a glance is "when did it
 * break", not the curve of each level. Colors mirror the table's badges, otherwise you'd have to
 * learn two codes to read the same screen.
 */
export function LogHistogram({
  stats,
  onSelect,
}: {
  stats: LogStats | null
  /** Drilling into a bucket: the filter narrows to it and the histogram rebuckets within it. */
  onSelect?: (at: number, granularity: LogGranularity) => void
}) {
  const columns = useMemo(() => buildColumns(stats), [stats])

  const totals = columns.reduce((sums, column) => {
    for (const level of LOG_LEVELS) sums[level] += column[level]

    return sums
  }, emptyCounts())

  const overall = LOG_LEVELS.reduce((sum, level) => sum + totals[level], 0)

  if (columns.length === 0 || overall === 0) {
    return (
      <div className="flex h-36 items-center justify-center text-xs text-ink-faint">
        No entries in this window.
      </div>
    )
  }

  const granularity = stats?.granularity ?? 'Hour'
  const axis = new Intl.DateTimeFormat('en-US', LABEL_FORMATS[granularity])

  const summary =
    `${formatCount(overall)} entries, ` +
    LOG_LEVELS.filter((level) => totals[level] > 0)
      .map((level) => `${formatCount(totals[level])} ${LEVEL_META[level].label.toLowerCase()}`)
      .join(', ')

  return (
    <div
      role="img"
      aria-label={`Histogram: ${summary}`}
      className={cn(
        'h-36',
        onSelect && 'cursor-pointer',
        // Recharts renders its chart focusable for keyboard navigation: on click, the browser
        // places its focus ring on it, which reads as a selection when nothing is selected. It's
        // removed on the frame and the SVG surface, no further.
        '[&_.recharts-surface]:outline-none [&_.recharts-wrapper]:outline-none',
      )}
    >
      <ResponsiveContainer width="100%" height="100%">
        <BarChart
          data={columns}
          margin={{ top: 4, right: 4, bottom: 0, left: 0 }}
          barCategoryGap={1}
          onClick={(state) => {
            if (!onSelect) return

            // `activeLabel` carries the axis value, i.e. the bucket's instant: safer than the
            // index, whose type varies by version and says nothing if the columns changed
            // between render and click.
            const at = Number(state?.activeLabel)

            if (Number.isFinite(at)) onSelect(at, granularity)
          }}
        >
          {/* Only horizontal lines: vertical ones would duplicate the bars themselves. */}
          <CartesianGrid vertical={false} stroke="var(--color-border-subtle)" />

          <XAxis
            dataKey="at"
            tickFormatter={(value: number) => axis.format(value)}
            tick={{ fontSize: 11, fill: 'var(--color-ink-faint)' }}
            tickLine={false}
            axisLine={{ stroke: 'var(--color-border-subtle)' }}
            // Let Recharts space out the ticks: at sixty bars, one label per bar would overlap
            // until unreadable.
            minTickGap={48}
          />

          <YAxis
            width={36}
            allowDecimals={false}
            tick={{ fontSize: 11, fill: 'var(--color-ink-faint)' }}
            tickLine={false}
            axisLine={false}
          />

          <Tooltip
            cursor={{ fill: 'var(--color-surface-hover)' }}
            content={({ active, payload, label }) => (
              <HistogramTooltip
                active={active}
                payload={payload}
                label={label as number}
                granularity={granularity}
              />
            )}
          />

          {STACK.map((level) => (
            <Bar
              key={level}
              dataKey={level}
              stackId="levels"
              fill={BAR_FILLS[level]}
              isAnimationActive={false}
            />
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}

/** Level legend, shared by the histogram and the filters. */
export function LevelLegend() {
  return (
    <ul className="flex flex-wrap items-center gap-3">
      {LOG_LEVELS.map((level) => (
        <li key={level} className="flex items-center gap-1.5 text-[11px] text-ink-muted">
          <span aria-hidden="true" className={cn('size-2 rounded-sm', LEGEND_TONES[level])} />
          {LEVEL_META[level].label}
        </li>
      ))}
    </ul>
  )
}
