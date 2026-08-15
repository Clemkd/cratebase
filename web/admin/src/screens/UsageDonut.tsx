import { Cell, Pie, PieChart, ResponsiveContainer, Tooltip } from 'recharts'
import { formatBytes } from '../lib/format'
import { cn } from '../ui'

/** Une part du donut. */
export interface UsageSlice {
  label: string
  bytes: number
  /** Variable CSS de la teinte, pour que le graphique suive le thème sans code de synchronisation. */
  fill: string
  /** Classe équivalente, pour la pastille de la légende — un aplat HTML ne lit pas un `fill` SVG. */
  swatch: string
}

interface Slice {
  name?: unknown
  value?: unknown
}

/** Bulle de survol : une part, sa taille, sa proportion. */
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
        {total > 0 && ` — ${((slice.value / total) * 100).toFixed(1)} %`}
      </p>
    </div>
  )
}

/**
 * Occupation d'un volume, en anneau.
 *
 * L'anneau n'est pas décoratif : ce qu'on cherche sur un disque, c'est une proportion — « la base
 * a-t-elle pris toute la place ? » —, et une proportion se lit mieux en angle qu'en chiffres alignés.
 * Le centre porte le total, parce qu'un anneau sans dénominateur ne dit rien.
 *
 * Les parts nulles sont écartées avant le rendu : Recharts leur réserve un secteur d'épaisseur nulle
 * qui garnit quand même la légende, et une légende de zéros noie les deux lignes qui comptent.
 */
export function UsageDonut({
  slices,
  total,
  caption,
  className,
}: {
  slices: UsageSlice[]
  /** Dénominateur de l'anneau. Il vaut la somme des parts quand la capacité est connue. */
  total: number
  /** Ce que le centre annonce sous le total. */
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

        {/* Le total au centre, hors du SVG : il doit rester sélectionnable et suivre la typographie
            de la page, ce qu'un <text> ne fait ni l'un ni l'autre. */}
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
                {((slice.bytes / total) * 100).toFixed(1)} %
              </span>
            )}
          </li>
        ))}

        {visible.length === 0 && <li className="text-xs text-ink-faint">Rien à mesurer.</li>}
      </ul>
    </div>
  )
}
