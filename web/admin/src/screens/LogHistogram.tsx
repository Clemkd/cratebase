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

/** Durée d'une tranche, en millisecondes. */
const STEP: Record<LogGranularity, number> = {
  Minute: 60_000,
  Hour: 3_600_000,
  Day: 86_400_000,
}

/** Découpage appliqué à l'intérieur d'une tranche qu'on ouvre. La minute ne se subdivise plus. */
const FINER: Record<LogGranularity, LogGranularity> = {
  Day: 'Hour',
  Hour: 'Minute',
  Minute: 'Minute',
}

/**
 * Bornes d'une tranche, telles que le filtre les attend.
 *
 * La borne haute est exclue, comme côté serveur : deux tranches voisines partageraient sinon
 * l'entrée posée exactement sur leur frontière, et la somme des barres dépasserait le total.
 */
export function bucketRange(at: number, granularity: LogGranularity) {
  return {
    from: new Date(at).toISOString(),
    to: new Date(at + STEP[granularity]).toISOString(),
    granularity: FINER[granularity],
  }
}

/**
 * Nombre maximal de barres.
 *
 * Au-delà, chaque barre mesure moins d'un pixel : le graphique cesse d'être lisible avant de
 * cesser d'être calculable. Les tranches les plus anciennes sont alors écartées, pas agrégées —
 * agréger silencieusement changerait l'échelle sans que rien ne l'indique.
 */
const MAX_COLUMNS = 180

/**
 * Teinte de chaque niveau.
 *
 * Des variables CSS et non des valeurs figées : ce sont les mêmes jetons que le reste de la
 * console, donc le graphique bascule avec le thème sans qu'aucun code n'ait à écouter ce
 * changement. Une valeur calculée en JavaScript resterait, elle, celle du thème actif au montage.
 */
const BAR_FILLS: Record<LogLevel, string> = {
  Debug: 'var(--color-border-strong)',
  Info: 'var(--color-brand)',
  Warning: 'var(--color-warning)',
  Error: 'var(--color-danger)',
}

/** Classes équivalentes, pour la légende — un aplat HTML ne lit pas un `fill` SVG. */
const LEGEND_TONES: Record<LogLevel, string> = {
  Debug: 'bg-border-strong',
  Info: 'bg-brand',
  Warning: 'bg-warning',
  Error: 'bg-danger',
}

/**
 * Ordre d'empilement, du bas de la barre vers le haut.
 *
 * Recharts empile dans l'ordre de déclaration : le plus grave est déclaré en dernier, donc dessiné
 * en haut, là où l'œil le trouve sans chercher.
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
 * Reconstitue la suite complète des tranches.
 *
 * Le serveur ne renvoie que les tranches non vides : les afficher telles quelles collerait deux
 * heures distantes de trois jours l'une à côté de l'autre, et le graphique mentirait sur le rythme
 * des évènements. Les creux sont donc rétablis ici, avec leur largeur réelle.
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

  // Sans borne haute, la dernière tranche est celle de maintenant et non la dernière tranche
  // peuplée : un graphique qui s'arrête à la dernière erreur laisse croire que le silence qui a
  // suivi n'existe pas. Avec une borne haute — une tranche ouverte au clic —, c'est elle qui
  // ferme la série : la prolonger jusqu'à maintenant repousserait la fenêtre entière hors du
  // graphique, qui s'afficherait vide alors que le tableau sous lui montre des lignes.
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
 * Intitulé d'une tranche dans la bulle, plus explicite que la graduation de l'axe.
 *
 * Aucun de ces gabarits ne mêle `dateStyle` à des composants isolés : `Intl.DateTimeFormat` refuse
 * ce mélange à la construction, et la bulle emporterait l'écran entier au premier survol.
 */
const DETAIL_FORMATS: Record<LogGranularity, Intl.DateTimeFormatOptions> = {
  Minute: { dateStyle: 'short', timeStyle: 'short' },
  Hour: { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' },
  Day: { dateStyle: 'full' },
}

/**
 * Une part de la barre survolée, telle que Recharts la transmet.
 *
 * Redéclarée ici plutôt qu'importée : le type de la bibliothèque porte une douzaine de champs
 * génériques dont la bulle n'utilise que deux, et s'y accrocher rendrait la mise à jour de Recharts
 * plus coûteuse qu'elle ne doit l'être.
 */
interface Slice {
  /** `unknown` parce que Recharts admet aussi une fonction d'accès ; ici c'est toujours un niveau. */
  dataKey?: unknown
  value?: unknown
}

/**
 * Bulle de détail d'une tranche.
 *
 * Elle porte le décompte de chaque niveau et non le seul total : savoir qu'un pic vaut trois cents
 * requêtes n'apprend rien ; savoir que deux cent quatre-vingts sont des erreurs, si.
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
  const detail = new Intl.DateTimeFormat('fr-FR', DETAIL_FORMATS[granularity])

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
        <p className="mt-1 text-xs text-ink-faint">Aucune entrée</p>
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
          {formatCount(total)} au total
        </p>
      )}
    </div>
  )
}

/**
 * Histogramme des volumes du journal.
 *
 * Empilé par niveau plutôt que superposé : ce qu'on cherche d'un coup d'œil, c'est « quand est-ce
 * que ça a cassé », pas la courbe de chaque niveau. Les couleurs reprennent celles des pastilles
 * du tableau, sans quoi il faudrait apprendre deux codes pour lire un même écran.
 */
export function LogHistogram({
  stats,
  onSelect,
}: {
  stats: LogStats | null
  /** Ouverture d'une tranche : le filtre s'y restreint et l'histogramme s'y redécoupe. */
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
        Aucune entrée sur cette fenêtre.
      </div>
    )
  }

  const granularity = stats?.granularity ?? 'Hour'
  const axis = new Intl.DateTimeFormat('fr-FR', LABEL_FORMATS[granularity])

  const summary =
    `${formatCount(overall)} entrées, ` +
    LOG_LEVELS.filter((level) => totals[level] > 0)
      .map((level) => `${formatCount(totals[level])} ${LEVEL_META[level].label.toLowerCase()}`)
      .join(', ')

  return (
    <div
      role="img"
      aria-label={`Histogramme : ${summary}`}
      className={cn(
        'h-36',
        onSelect && 'cursor-pointer',
        // Recharts rend son graphique focalisable pour la navigation au clavier : au clic, le
        // navigateur y pose son anneau de focus, qui se lit comme une sélection alors que rien
        // n'est sélectionné. On le retire sur le cadre et sur la surface SVG, pas au-delà.
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

            // `activeLabel` porte la valeur de l'axe, donc l'instant de la tranche : plus sûr que
            // l'indice, dont le type varie selon la version et qui ne dit rien si les colonnes ont
            // changé entre le rendu et le clic.
            const at = Number(state?.activeLabel)

            if (Number.isFinite(at)) onSelect(at, granularity)
          }}
        >
          {/* Seules les lignes horizontales : les verticales doubleraient les barres elles-mêmes. */}
          <CartesianGrid vertical={false} stroke="var(--color-border-subtle)" />

          <XAxis
            dataKey="at"
            tickFormatter={(value: number) => axis.format(value)}
            tick={{ fontSize: 11, fill: 'var(--color-ink-faint)' }}
            tickLine={false}
            axisLine={{ stroke: 'var(--color-border-subtle)' }}
            // Laisser Recharts espacer les graduations : à soixante barres, une étiquette par barre
            // se chevaucherait jusqu'à devenir illisible.
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
              stackId="niveaux"
              fill={BAR_FILLS[level]}
              isAnimationActive={false}
            />
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}

/** Légende des niveaux, partagée par l'histogramme et les filtres. */
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
