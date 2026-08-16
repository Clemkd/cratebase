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

/** Heading for a drilled-down slice: its two bounds, at the precision that distinguishes it. */
function sliceLabel(slice: LogSlice): string {
  const from = new Date(slice.from)
  const to = new Date(slice.to)
  const sameDay = from.toDateString() === to.toDateString()

  const date = new Intl.DateTimeFormat('en-US', { dateStyle: 'short' })
  const time = new Intl.DateTimeFormat('en-US', { hour: '2-digit', minute: '2-digit' })

  return sameDay
    ? `${date.format(from)} ${time.format(from)} – ${time.format(to)}`
    : `${date.format(from)} – ${date.format(to)}`
}

/**
 * Level badge, icon included.
 *
 * In abbreviated form in the table, where the column repeats on every row; the full label
 * remains accessible on hover.
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
 * An entry's author, under their name rather than their identifier.
 *
 * The link leads to the account's record, framed by the address: that's the question that
 * immediately follows "who did this". It's only set if the collection still exists — a link to a
 * deleted collection would only lead to an empty screen.
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
  if (entry.authId === '') return <span className="text-ink-faint">anonymous</span>

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

/** Icon alone for a level, for the filter list's options. */
function LevelIcon({ level }: { level: LogLevel }) {
  const meta = LEVEL_META[level]

  return <meta.icon size={13} aria-hidden="true" className={LEVEL_INK[level]} />
}

/** A detail row: heading on the left, value on the right. */
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
 * Detail for an entry.
 *
 * In a side panel rather than a centered modal: several entries get opened in succession to
 * compare, and a centered modal forces you to restart from the middle of the screen every time.
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
      title="Log entry"
      description={formatDateTime(entry.created)}
      footer={
        <>
          <CopyButton
            value={() => JSON.stringify(entry, null, 2)}
            variant="outline"
            size="md"
            label="Copy JSON"
          />
          <Button variant="primary" icon={<X size={15} aria-hidden="true" />} onClick={onClose}>
            Close
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
          <Detail label="ID" value={entry.id} mono copy={entry.id} />
          <Detail label="Timestamp" value={formatDateTime(entry.created)} />
          <Detail label="Path" value={entry.url} mono copy={entry.url} />
          <Detail
            label="Duration"
            value={entry.duration > 0 ? formatDuration(entry.duration) : null}
          />
          <Detail
            label="Author"
            value={
              <AuthorLink entry={entry} label={authorLabel} reachable={authorReachable} />
            }
          />
          <Detail label="Account collection" value={entry.authCollection} mono />
          <Detail label="Account ID" value={entry.authId} mono copy={entry.authId} />
          <Detail label="Address" value={entry.ip} mono />
          <Detail label="Referer" value={entry.referer} />
          <Detail label="Agent" value={entry.userAgent} />
        </div>

        {data.length > 0 && (
          <div>
            <h3 className="mb-1.5 text-xs font-semibold text-ink">Details</h3>
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
 * Request log.
 *
 * Two blocks: the shape of the window, then the rows. Filters aren't gathered into a bar above
 * but placed under the header of the column they restrict — that's the column you look at when
 * deciding to filter, and a separate bar forces you to figure out which control drives which
 * column.
 *
 * The histogram isn't an ornament: it says at a glance whether the anomaly you're looking for is
 * an isolated spike or a permanent state, a question a paginated table never answers. Clicking a
 * bar drills into it — the window narrows to that slice, which rebuckets one notch finer.
 */
export function LogsBrowser({ collections }: { collections: Collection[] }) {
  const toast = useToast()

  const [path, setPath] = useState('')
  const [levels, setLevels] = useState<LogLevel[]>([])
  const [method, setMethod] = useState('')
  const [statusText, setStatusText] = useState('')
  const [ascending, setAscending] = useState(false)
  // Named "range" rather than "window": the latter would shadow the global object of the same
  // name throughout the component, and the next line wanting `window.matchMedia` would fail for
  // no visible reason.
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

  // Any change of criteria resets to the first page: staying on page seven of a result that has
  // only two shows an empty table, with nothing saying why.
  const criteria = `${q}|${levels.join(',')}|${method}|${statusFilter}|${range}|${slice?.from ?? ''}`

  useEffect(() => {
    setPage(1)
  }, [criteria])

  /** Drills into a histogram bucket: the window narrows to it, the bucketing gets finer. */
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

      toast.success(`${formatCount(deleted)} ${plural(deleted, 'entry deleted', 'entries deleted')}.`)
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
        <Tooltip content="Reload">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Reload log"
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
            Reset
          </Button>
        )}

        <Button
          variant="danger"
          icon={<Trash2 size={15} aria-hidden="true" />}
          onClick={() => setConfirming(true)}
        >
          Clear log
        </Button>
      </PageActions>

      <Card className="space-y-2 px-4 py-3">
        {slice && (
          <div className="flex flex-wrap items-center gap-2 text-xs text-ink-muted">
            <span>Slice open</span>
            <Badge tone="brand">{sliceLabel(slice)}</Badge>
            <Button
              size="sm"
              variant="ghost"
              icon={<CornerUpLeft size={14} aria-hidden="true" />}
              onClick={() => setSlice(null)}
            >
              Back to window
            </Button>
          </div>
        )}

        <LogHistogram stats={stats} onSelect={openSlice} />

        <LevelLegend />
      </Card>

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={7} />}

      {/* The table stays mounted even when empty: the filters live in its header, and hiding it
          would make it impossible to undo the too-narrow criteria that emptied the result. */}
      <Card className="overflow-hidden">
          <Table bare caption="Log entries">
            <THead>
              {/* The label and its filter form a single block: the rule doesn't separate them,
                  it closes off the pair below the filter row. */}
              <tr className="[&>th]:border-b-0">
                {/* The only sort that makes sense here: two states, not three. A "no sort" would
                    hand the log back insertion order, which is already chronological order — a
                    third click with no visible effect. */}
                <SortableTh
                  className="w-40"
                  label="Timestamp"
                  direction={ascending ? 'asc' : 'desc'}
                  onSort={() => {
                    setAscending((current) => !current)
                    setPage(1)
                  }}
                />
                <Th className="w-28">Level</Th>
                <Th className="w-24">Method</Th>
                <Th>Path</Th>
                <Th className="w-24">Status</Th>
                <Th className="w-24">Duration</Th>
                <Th className="w-44">Author</Th>
              </tr>

              {/* Each filter sits under the column it restricts. Columns the API can't filter —
                  duration, author — expose nothing rather than an inert control. */}
              <tr className="[&>td]:border-b [&>td]:border-border-subtle [&>td]:px-2 [&>td]:pb-2">
                <td>
                  <SelectMenu<LogWindow>
                    value={range}
                    size="sm"
                    aria-label="Time window"
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
                    aria-label="Levels selected"
                    placeholder="All"
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
                    aria-label="HTTP method"
                    className="w-full"
                    options={[
                      { value: 'all', label: 'All' },
                      ...METHODS.map((entry) => ({ value: entry, label: entry })),
                    ]}
                    onChange={(choice) => setMethod(choice === 'all' ? '' : choice)}
                  />
                </td>
                <td>
                  <Input
                    value={path}
                    spellCheck={false}
                    aria-label="Filter on path or message"
                    placeholder="/api/collections"
                    className="h-8 font-mono text-xs"
                    onChange={(event) => setPath(event.target.value)}
                  />
                </td>
                <td>
                  <Input
                    value={statusText}
                    inputMode="numeric"
                    aria-label="Exact HTTP status"
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
                    {/* The whole row opens the detail, but the clickable element remains a
                        button: a cell fitted with an `onClick` is reachable neither by keyboard
                        nor by a screen reader. */}
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
                      // The badge filters: looking up "all the 404s" is the most common gesture
                      // on this screen, and doing it by hand would mean retyping a number that's
                      // right there in front of you.
                      <button
                        type="button"
                        aria-label={`Show only ${entry.status} responses`}
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
                      title="No entries"
                      description={
                        filtered
                          ? 'No entry matches these criteria in this window.'
                          : 'The log is empty. Requests served by the API appear here within seconds.'
                      }
                      action={
                        filtered ? (
                          <Button
                            variant="outline"
                            icon={<Eraser size={15} aria-hidden="true" />}
                            onClick={reset}
                          >
                            Reset filters
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
                  {formatCount(result.totalItems)} {plural(result.totalItems, 'entry', 'entries')}
                </span>
                <Badge>
                  page {result.page} / {Math.max(result.totalPages, 1)}
                </Badge>
              </div>

              <div className="flex items-center gap-2">
                <div className="flex items-center gap-1.5">
                  <span aria-hidden="true">Per page</span>
                  <SelectMenu<string>
                    value={String(perPage)}
                    size="sm"
                    aria-label="Entries per page"
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
                  Previous
                </Button>
                <Button
                  size="sm"
                  disabled={result.page >= result.totalPages}
                  onClick={() => setPage(result.page + 1)}
                >
                  Next
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
        title="Clear the log?"
        message="All entries will be deleted, including those documenting an ongoing incident. The deletion is permanent and will itself be logged."
        confirmLabel="Clear permanently"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        onConfirm={() => void clear()}
        onClose={() => setConfirming(false)}
      />
    </div>
  )
}

