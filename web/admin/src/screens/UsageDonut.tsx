import { Cell, Pie, PieChart, ResponsiveContainer, Tooltip } from 'recharts'
import { formatBytes } from '../lib/format'
import { cn } from '../ui'

/** A slice of the donut. */
export interface UsageSlice {
  label: string
  bytes: number
  /** CSS variable for the tone, so the chart follows the theme without sync code. */
  fill: string
  /** Equivalent class, for the legend swatch — a flat HTML fill can't read an SVG `fill`. */
  swatch: string
}

interface Slice {
  name?: unknown
  value?: unknown
}

/** Hover tooltip: a slice, its size, its proportion. */
function DonutTooltip({
  active,
  payload,
  total,
}: {
  active?: boolean
  payload?: readonly Slice[]
  total: number
}) {
  const slice = active ? payload?.[0] : undefined

  if (!slice || typeof slice.value !== 'number') return null

  return (
    <div className="rounded-[var(--radius-control)] border border-border-subtle bg-surface-raised px-3 py-2 shadow-popover">
      <p className="text-xs text-ink">{String(slice.name ?? '')}</p>
      <p className="text-xs tabular-nums text-ink-muted">
        {formatBytes(slice.value)}
        {total > 0 && ` — ${((slice.value / total) * 100).toFixed(1)}%`}
      </p>
    </div>
  )
}

/**
 * Volume usage, as a ring.
 *
 * The ring isn't decorative: what you look for on a disk is a proportion — "has the database
 * taken up all the room?" — and a proportion reads better as an angle than as aligned numbers.
 * The center carries the total, because a ring without a denominator says nothing.
 *
 * Zero slices are discarded before rendering: Recharts still reserves a zero-thickness sector for
 * them that fills out the legend anyway, and a legend full of zeros drowns out the two lines that
 * matter.
 */
export function UsageDonut({
  slices,
  total,
  caption,
  className,
}: {
  slices: UsageSlice[]
  /** Denominator of the ring. Equals the sum of the slices when capacity is known. */
  total: number
  /** What the center announces under the total. */
  caption: string
  className?: string
}) {
  const visible = slices.filter((slice) => slice.bytes > 0)
  const data = visible.map((slice) => ({ name: slice.label, value: slice.bytes }))

  return (
    <div className={cn('flex flex-wrap items-center gap-4', className)}>
      <div className="relative size-36 shrink-0">
        <ResponsiveContainer width="100%" height="100%">
          <PieChart>
            <Pie
              data={data}
              dataKey="value"
              nameKey="name"
              innerRadius="62%"
              outerRadius="100%"
              paddingAngle={1}
              strokeWidth={0}
              isAnimationActive={false}
            >
              {visible.map((slice) => (
                <Cell key={slice.label} fill={slice.fill} />
              ))}
            </Pie>

            <Tooltip
              content={({ active, payload }) => (
                <DonutTooltip
                  active={active}
                  payload={payload as readonly Slice[] | undefined}
                  total={total}
                />
              )}
            />
          </PieChart>
        </ResponsiveContainer>

        {/* The total at the center, outside the SVG: it must remain selectable and follow the
            page's typography, neither of which a <text> element does. */}
        <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
          <span className="text-sm font-semibold tabular-nums text-ink">{formatBytes(total)}</span>
          <span className="text-[11px] text-ink-faint">{caption}</span>
        </div>
      </div>

      <ul className="min-w-0 flex-1 space-y-1.5">
        {visible.map((slice) => (
          <li key={slice.label} className="flex items-center gap-2 text-xs">
            <span aria-hidden="true" className={cn('size-2.5 shrink-0 rounded-sm', slice.swatch)} />
            <span className="min-w-0 flex-1 truncate text-ink-muted">{slice.label}</span>
            <span className="shrink-0 tabular-nums text-ink">{formatBytes(slice.bytes)}</span>
            {total > 0 && (
              <span className="w-12 shrink-0 text-right tabular-nums text-ink-faint">
                {((slice.bytes / total) * 100).toFixed(1)}%
              </span>
            )}
          </li>
        ))}

        {visible.length === 0 && <li className="text-xs text-ink-faint">Nothing to measure.</li>}
      </ul>
    </div>
  )
}
