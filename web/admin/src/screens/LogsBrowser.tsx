import { useEffect, useState, type ReactNode } from 'react'
import { CornerUpLeft, Eraser, RotateCw, ScrollText, Trash2, X } from 'lucide-react'
import {
  api,
  describeFailure,
  LOG_LEVELS,
  type Collection,
  type LogEntry,
  type LogGranularity,
  type LogLevel,
} from '../api'
import { useAuthors, authorKey } from '../hooks/useAuthors'
import { useDebounced } from '../hooks/useDebounced'
import { useLogs, type LogSlice } from '../hooks/useLogs'
import { routeHref } from '../hooks/useRoute'
import {
  LEVEL_INK,
  LEVEL_META,
  LOG_WINDOWS,
  formatDuration,
  statusTone,
  type LogWindow,
} from '../lib/logs'
import { formatCount, formatDateTime, plural } from '../lib/format'
import { PageActions } from '../layout/Page'
import {
  Badge,
  Button,
  Card,
  ConfirmDialog,
  CopyButton,
  Dialog,
  EmptyState,
  ErrorBlock,
  Input,
  MultiSelectMenu,
  SelectMenu,
  SortableTh,
  TBody,
  THead,
  Table,
  TableSkeleton,
  Td,
  Th,
  Tooltip,
  Tr,
  cn,
  useToast,
} from '../ui'
import { LevelLegend, LogHistogram, bucketRange } from './LogHistogram'

const PER_PAGE_CHOICES = [25, 50, 100, 200]
const METHODS = ['GET', 'POST', 'PATCH', 'DELETE']

/** Intitulé d'une tranche ouverte : ses deux bornes, dans la précision qui la distingue. */
function sliceLabel(slice: LogSlice): string {
  const from = new Date(slice.from)
  const to = new Date(slice.to)
  const sameDay = from.toDateString() === to.toDateString()

  const date = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short' })
  const time = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' })

  return sameDay
    ? `${date.format(from)} ${time.format(from)} – ${time.format(to)}`
    : `${date.format(from)} – ${date.format(to)}`
}

/**
 * Pastille de niveau, icône comprise.
 *
 * En forme abrégée dans le tableau, où la colonne se répète à chaque ligne ; le libellé entier
 * reste accessible au survol.
 */
function LevelBadge({ level, compact = false }: { level: LogLevel; compact?: boolean }) {
  const meta = LEVEL_META[level]

  return (
    <Badge tone={meta.tone} title={compact ? meta.label : undefined}>
      <meta.icon size={11} aria-hidden="true" className="shrink-0" />
      {compact ? meta.short : meta.label}
    </Badge>
  )
}

/**
 * Auteur d'une entrée, sous son nom et non sous son identifiant.
 *
 * Le lien mène à l'enregistrement du compte, cadré par l'adresse : c'est la question qui suit
 * immédiatement « qui a fait ça ». Il n'est posé que si la collection existe encore — un lien vers
 * une collection supprimée ne mènerait qu'à un écran vide.
 */
function AuthorLink({
  entry,
  label,
  reachable,
}: {
  entry: LogEntry
  label?: string
  reachable: boolean
}) {
  if (entry.authId === '') return <span className="text-ink-faint">anonyme</span>

  const title = `${entry.authCollection} / ${entry.authId}`
  const text = label ?? `${entry.authId.slice(0, 8)}…`
  const styles = cn('truncate', label === undefined && 'font-mono')

  if (!reachable) {
    return (
      <span title={title} className={styles}>
        {text}
      </span>
    )
  }

  return (
    <a
      href={routeHref({
        kind: 'collection',
        name: entry.authCollection,
        tab: 'records',
        focus: entry.authId,
      })}
      title={title}
      className={cn(styles, 'inline-block max-w-full text-brand hover:underline')}
    >
      {text}
    </a>
  )
}

/** Icône seule d'un niveau, pour les options de la liste de filtre. */
function LevelIcon({ level }: { level: LogLevel }) {
  const meta = LEVEL_META[level]

  return <meta.icon size={13} aria-hidden="true" className={LEVEL_INK[level]} />
}

/** Une ligne du détail : intitulé à gauche, valeur à droite. */
function Detail({
  label,
  value,
  mono = false,
  copy,
}: {
  label: string
  value: ReactNode
  mono?: boolean
  copy?: string
}) {
  if (value === '' || value === null || value === undefined) return null

  return (
    <div className="flex items-start justify-between gap-3 border-b border-border-subtle py-2 last:border-0">
      <span className="shrink-0 text-xs text-ink-muted">{label}</span>
      <span className={cn('min-w-0 text-right text-xs break-all text-ink', mono && 'font-mono')}>
        {value}
        {copy !== undefined && copy !== '' && (
          <CopyButton value={copy} size="icon" className="ml-1 size-6 align-middle" />
        )}
      </span>
    </div>
  )
}

/**
 * Détail d'une entrée.
 *
 * En panneau latéral et non en modale centrée : on ouvre plusieurs entrées à la suite pour
 * comparer, et une modale centrée oblige à repartir du milieu de l'écran à chaque fois.
 */
function LogDetail({
  entry,
  authorLabel,
  authorReachable,
  onClose,
}: {
  entry: LogEntry | null
  authorLabel?: string
  authorReachable: boolean
  onClose: () => void
}) {
  if (!entry) return null

  const data = Object.entries(entry.data)

  return (
    <Dialog
      open
      side="right"
      onClose={onClose}
      title="Entrée du journal"
      description={formatDateTime(entry.created)}
      footer={
        <>
          <CopyButton
            value={() => JSON.stringify(entry, null, 2)}
            variant="outline"
            size="md"
            label="Copier le JSON"
          />
          <Button variant="primary" icon={<X size={15} aria-hidden="true" />} onClick={onClose}>
            Fermer
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <div className="flex flex-wrap items-center gap-2">
          <LevelBadge level={entry.level} />
          {entry.status > 0 && <Badge tone={statusTone(entry.status)}>HTTP {entry.status}</Badge>}
          {entry.method !== '' && <Badge mono>{entry.method}</Badge>}
        </div>

        <p className="rounded-[var(--radius-control)] bg-surface-sunken px-3 py-2 font-mono text-xs break-all text-ink">
          {entry.message}
        </p>

        <div>
          <Detail label="Identifiant" value={entry.id} mono copy={entry.id} />
          <Detail label="Horodatage" value={formatDateTime(entry.created)} />
          <Detail label="Chemin" value={entry.url} mono copy={entry.url} />
          <Detail
            label="Durée"
            value={entry.duration > 0 ? formatDuration(entry.duration) : null}
          />
          <Detail
            label="Auteur"
            value={
              <AuthorLink entry={entry} label={authorLabel} reachable={authorReachable} />
            }
          />
          <Detail label="Collection du compte" value={entry.authCollection} mono />
          <Detail label="Identifiant du compte" value={entry.authId} mono copy={entry.authId} />
          <Detail label="Adresse" value={entry.ip} mono />
          <Detail label="Référent" value={entry.referer} />
          <Detail label="Agent" value={entry.userAgent} />
        </div>

        {data.length > 0 && (
          <div>
            <h3 className="mb-1.5 text-xs font-semibold text-ink">Détails</h3>
            <pre className="overflow-x-auto rounded-[var(--radius-control)] border border-border-subtle bg-surface-sunken px-3 py-2 font-mono text-[11px] leading-relaxed text-ink">
              {JSON.stringify(entry.data, null, 2)}
            </pre>
          </div>
        )}
      </div>
    </Dialog>
  )
}

/**
 * Journal des requêtes.
 *
 * Deux blocs : la forme de la fenêtre, puis les lignes. Les filtres ne sont pas rassemblés dans une
 * barre au-dessus mais posés sous l'en-tête de la colonne qu'ils restreignent — c'est la colonne
 * qu'on regarde quand on décide de filtrer, et une barre séparée oblige à retrouver quel contrôle
 * commande quelle colonne.
 *
 * L'histogramme n'est pas un ornement : il dit en un coup d'œil si l'anomalie cherchée est un pic
 * isolé ou un état permanent, question à laquelle une table paginée ne répond jamais. Cliquer une
 * barre l'ouvre — la fenêtre se restreint à cette tranche, qui se redécoupe d'un cran plus fin.
 */
export function LogsBrowser({ collections }: { collections: Collection[] }) {
  const toast = useToast()

  const [path, setPath] = useState('')
  const [levels, setLevels] = useState<LogLevel[]>([])
  const [method, setMethod] = useState('')
  const [statusText, setStatusText] = useState('')
  const [ascending, setAscending] = useState(false)
  // Nommée « range » et non « window » : le second masquerait l'objet global du même nom dans tout
  // le composant, et la prochaine ligne qui voudrait `window.matchMedia` échouerait sans raison
  // visible.
  const [range, setRange] = useState<LogWindow>('24h')
  const [slice, setSlice] = useState<LogSlice | null>(null)
  const [page, setPage] = useState(1)
  const [perPage, setPerPage] = useState(50)
  const [selected, setSelected] = useState<LogEntry | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [clearing, setClearing] = useState(false)

  const q = useDebounced(path)
  const statusFilter = Number.parseInt(useDebounced(statusText), 10) || 0

  const { result, stats, loading, error, reload } = useLogs({
    page,
    perPage,
    levels,
    method,
    status: statusFilter,
    q,
    window: range,
    slice,
    ascending,
  })

  const items = result?.items ?? []
  const filtered =
    path !== '' ||
    levels.length > 0 ||
    method !== '' ||
    statusText !== '' ||
    range !== '24h' ||
    slice !== null

  const authors = useAuthors(items, collections)
  const labelOf = (entry: LogEntry) => authors.get(authorKey(entry.authCollection, entry.authId))
  const reachable = (entry: LogEntry) =>
    collections.some((item) => item.name === entry.authCollection)

  // Tout changement de critère ramène à la première page : rester en page sept d'un résultat qui
  // en compte deux affiche un tableau vide, sans que rien ne dise pourquoi.
  const criteria = `${q}|${levels.join(',')}|${method}|${statusFilter}|${range}|${slice?.from ?? ''}`

  useEffect(() => {
    setPage(1)
  }, [criteria])

  /** Ouvre une tranche de l'histogramme : la fenêtre s'y restreint, le découpage s'affine. */
  const openSlice = (at: number, granularity: LogGranularity) => setSlice(bucketRange(at, granularity))

  const reset = () => {
    setPath('')
    setLevels([])
    setMethod('')
    setStatusText('')
    setRange('24h')
    setSlice(null)
    setPage(1)
  }

  const clear = async () => {
    setClearing(true)

    try {
      const { deleted } = await api.logs.clear()

      toast.success(`${formatCount(deleted)} ${plural(deleted, 'entrée supprimée', 'entrées supprimées')}.`)
      setPage(1)
      await reload()
    } catch (failure) {
      toast.error(describeFailure(failure))
    } finally {
      setClearing(false)
      setConfirming(false)
    }
  }

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Recharger">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Recharger le journal"
            onClick={() => void reload()}
          >
            <RotateCw size={16} aria-hidden="true" />
          </Button>
        </Tooltip>

        {filtered && (
          <Button
            variant="outline"
            icon={<Eraser size={15} aria-hidden="true" />}
            onClick={reset}
          >
            Réinitialiser
          </Button>
        )}

        <Button
          variant="danger"
          icon={<Trash2 size={15} aria-hidden="true" />}
          onClick={() => setConfirming(true)}
        >
          Vider le journal
        </Button>
      </PageActions>

      <Card className="space-y-2 px-4 py-3">
        {slice && (
          <div className="flex flex-wrap items-center gap-2 text-xs text-ink-muted">
            <span>Tranche ouverte</span>
            <Badge tone="brand">{sliceLabel(slice)}</Badge>
            <Button
              size="sm"
              variant="ghost"
              icon={<CornerUpLeft size={14} aria-hidden="true" />}
              onClick={() => setSlice(null)}
            >
              Revenir à la fenêtre
            </Button>
          </div>
        )}

        <LogHistogram stats={stats} onSelect={openSlice} />

        <LevelLegend />
      </Card>

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={7} />}

      {/* Le tableau reste monté même vide : les filtres vivent dans son en-tête, et les escamoter
          rendrait impossible de défaire le critère trop étroit qui a vidé le résultat. */}
      <Card className="overflow-hidden">
          <Table bare caption="Entrées du journal">
            <THead>
              <tr>
                {/* Le seul tri qui ait un sens ici : deux états, pas trois. Un « aucun tri »
                    rendrait au journal l'ordre d'insertion, qui est déjà l'ordre chronologique —
                    un troisième clic sans effet visible. */}
                <SortableTh
                  className="w-40"
                  label="Horodatage"
                  direction={ascending ? 'asc' : 'desc'}
                  onSort={() => {
                    setAscending((current) => !current)
                    setPage(1)
                  }}
                />
                <Th className="w-28">Niveau</Th>
                <Th className="w-24">Méthode</Th>
                <Th>Chemin</Th>
                <Th className="w-24">Statut</Th>
                <Th className="w-24">Durée</Th>
                <Th className="w-44">Auteur</Th>
              </tr>

              {/* Chaque filtre est posé sous la colonne qu'il restreint. Les colonnes que l'API ne
                  sait pas filtrer — durée, auteur — n'exposent rien plutôt qu'un contrôle inerte. */}
              <tr className="[&>td]:border-b [&>td]:border-border-subtle [&>td]:px-2 [&>td]:pb-2">
                <td>
                  <SelectMenu<LogWindow>
                    value={range}
                    size="sm"
                    aria-label="Fenêtre temporelle"
                    className="w-full"
                    disabled={slice !== null}
                    options={LOG_WINDOWS.map((entry) => ({
                      value: entry.value,
                      label: entry.label,
                    }))}
                    onChange={setRange}
                  />
                </td>
                <td>
                  <MultiSelectMenu<LogLevel>
                    values={levels}
                    size="sm"
                    aria-label="Niveaux retenus"
                    placeholder="Tous"
                    className="w-full"
                    options={LOG_LEVELS.map((entry) => ({
                      value: entry,
                      label: LEVEL_META[entry].short,
                      text: LEVEL_META[entry].label,
                      icon: <LevelIcon level={entry} />,
                    }))}
                    onChange={setLevels}
                  />
                </td>
                <td>
                  <SelectMenu<string>
                    value={method === '' ? 'all' : method}
                    size="sm"
                    aria-label="Méthode HTTP"
                    className="w-full"
                    options={[
                      { value: 'all', label: 'Toutes' },
                      ...METHODS.map((entry) => ({ value: entry, label: entry })),
                    ]}
                    onChange={(choice) => setMethod(choice === 'all' ? '' : choice)}
                  />
                </td>
                <td>
                  <Input
                    value={path}
                    spellCheck={false}
                    aria-label="Filtre sur le chemin ou le message"
                    placeholder="/api/collections"
                    className="h-8 font-mono text-xs"
                    onChange={(event) => setPath(event.target.value)}
                  />
                </td>
                <td>
                  <Input
                    value={statusText}
                    inputMode="numeric"
                    aria-label="Statut HTTP exact"
                    placeholder="404"
                    className="h-8 text-xs tabular-nums"
                    onChange={(event) =>
                      setStatusText(event.target.value.replaceAll(/\D/g, '').slice(0, 3))
                    }
                  />
                </td>
                <td />
                <td />
              </tr>
            </THead>

            <TBody>
              {items.map((entry) => (
                <Tr key={entry.id}>
                  <Td className="p-0">
                    {/* La ligne entière ouvre le détail, mais l'élément cliquable reste un bouton :
                        une cellule munie d'un `onClick` n'est atteignable ni au clavier ni au
                        lecteur d'écran. */}
                    <button
                      type="button"
                      onClick={() => setSelected(entry)}
                      className="w-full px-3 py-2 text-left text-xs whitespace-nowrap text-ink-muted"
                    >
                      {formatDateTime(entry.created)}
                    </button>
                  </Td>
                  <Td>
                    <LevelBadge level={entry.level} compact />
                  </Td>
                  <Td className="font-mono text-xs text-ink-muted">{entry.method}</Td>
                  <Td className="max-w-0">
                    <button
                      type="button"
                      onClick={() => setSelected(entry)}
                      className="block w-full truncate text-left font-mono text-xs text-ink"
                      title={entry.url === '' ? entry.message : entry.url}
                    >
                      {entry.url === '' ? entry.message : entry.url}
                    </button>
                  </Td>
                  <Td>
                    {entry.status > 0 && (
                      // La pastille filtre : chercher « tous les 404 » est le geste le plus courant
                      // de cet écran, et le faire à la main obligerait à retaper un nombre qu'on a
                      // sous les yeux.
                      <button
                        type="button"
                        aria-label={`Ne montrer que les réponses ${entry.status}`}
                        onClick={() => setStatusText(String(entry.status))}
                        className="cursor-pointer"
                      >
                        <Badge tone={statusTone(entry.status)}>{entry.status}</Badge>
                      </button>
                    )}
                  </Td>
                  <Td className="text-xs tabular-nums text-ink-muted">
                    {entry.duration > 0 ? formatDuration(entry.duration) : ''}
                  </Td>
                  <Td className="max-w-0 text-xs text-ink-muted">
                    <AuthorLink
                      entry={entry}
                      label={labelOf(entry)}
                      reachable={reachable(entry)}
                    />
                  </Td>
                </Tr>
              ))}
              {items.length === 0 && !loading && (
                <tr>
                  <td colSpan={7} className="px-3 py-10">
                    <EmptyState
                      icon={<ScrollText size={28} aria-hidden="true" />}
                      title="Aucune entrée"
                      description={
                        filtered
                          ? "Aucune entrée ne correspond à ces critères sur cette fenêtre."
                          : "Le journal est vide. Les requêtes servies par l'API y apparaissent en quelques secondes."
                      }
                      action={
                        filtered ? (
                          <Button
                            variant="outline"
                            icon={<Eraser size={15} aria-hidden="true" />}
                            onClick={reset}
                          >
                            Réinitialiser les filtres
                          </Button>
                        ) : undefined
                      }
                    />
                  </td>
                </tr>
              )}
            </TBody>
          </Table>

          {result && (
            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border-subtle bg-surface-sunken px-4 py-2.5 text-xs text-ink-muted">
              <div className="flex items-center gap-2">
                <span className="tabular-nums">
                  {formatCount(result.totalItems)} {plural(result.totalItems, 'entrée', 'entrées')}
                </span>
                <Badge>
                  page {result.page} / {Math.max(result.totalPages, 1)}
                </Badge>
              </div>

              <div className="flex items-center gap-2">
                <div className="flex items-center gap-1.5">
                  <span aria-hidden="true">Par page</span>
                  <SelectMenu<string>
                    value={String(perPage)}
                    size="sm"
                    aria-label="Entrées par page"
                    className="w-auto"
                    options={PER_PAGE_CHOICES.map((choice) => ({
                      value: String(choice),
                      label: String(choice),
                    }))}
                    onChange={(choice) => {
                      setPerPage(Number(choice))
                      setPage(1)
                    }}
                  />
                </div>

                <Button size="sm" disabled={result.page <= 1} onClick={() => setPage(result.page - 1)}>
                  Précédent
                </Button>
                <Button
                  size="sm"
                  disabled={result.page >= result.totalPages}
                  onClick={() => setPage(result.page + 1)}
                >
                  Suivant
                </Button>
              </div>
            </div>
          )}
      </Card>

      <LogDetail
        entry={selected}
        authorLabel={selected ? labelOf(selected) : undefined}
        authorReachable={selected ? reachable(selected) : false}
        onClose={() => setSelected(null)}
      />

      <ConfirmDialog
        open={confirming}
        busy={clearing}
        title="Vider le journal ?"
        message="Toutes les entrées seront supprimées, y compris celles qui documentent un incident en cours. La suppression est définitive et sera elle-même journalisée."
        confirmLabel="Vider définitivement"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        onConfirm={() => void clear()}
        onClose={() => setConfirming(false)}
      />
    </div>
  )
}

