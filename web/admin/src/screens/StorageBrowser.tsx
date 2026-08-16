import { useEffect, useState } from 'react'
import {
  Download,
  Eraser,
  FileQuestion,
  FolderOpen,
  Image as ImageIcon,
  RotateCw,
  Trash2,
  X,
} from 'lucide-react'
import { api, describeFailure, type Collection, type StoredObject } from '../api'
import { useDebounced } from '../hooks/useDebounced'
import { useStorageObjects } from '../hooks/useStorageObjects'
import { routeHref } from '../hooks/useRoute'
import { formatBytes, formatCount, plural } from '../lib/format'
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
  SelectMenu,
  TBody,
  THead,
  Table,
  TableSkeleton,
  Td,
  Th,
  Tooltip,
  Tr,
  useToast,
} from '../ui'

const PER_PAGE_CHOICES = [25, 50, 100, 200]

/** Can an image be shown as a preview? */
function isImage(entry: StoredObject): boolean {
  return entry.contentType.startsWith('image/')
}

/**
 * Read address for an object.
 *
 * It goes through the existing file route, which applies the view rule when the field is
 * protected. The inventory therefore opens no new access path: it names files, it doesn't
 * unlock them.
 */
function fileHref(entry: StoredObject, token: string, thumb?: string): string {
  const query = new URLSearchParams()

  if (thumb) query.set('thumb', thumb)
  if (token) query.set('token', token)

  const search = query.toString()

  return `/api/files/${encodeURIComponent(entry.collection)}/${encodeURIComponent(entry.recordId)}/${encodeURIComponent(entry.fileName)}${search ? `?${search}` : ''}`
}

/** Thumbnail for the preview column, or a fallback glyph. */
function Preview({ entry, token }: { entry: StoredObject; token: string }) {
  if (!isImage(entry) || entry.collection === '') {
    return (
      <span className="grid size-9 place-items-center rounded-[var(--radius-control)] bg-surface-sunken text-ink-faint">
        <FileQuestion size={15} aria-hidden="true" />
      </span>
    )
  }

  return (
    <img
      src={fileHref(entry, token, '0x60')}
      alt=""
      loading="lazy"
      className="size-9 rounded-[var(--radius-control)] border border-border-subtle object-cover"
    />
  )
}

/** Detail of an object, in a side panel. */
function ObjectDetail({
  entry,
  token,
  reachable,
  onClose,
  onDelete,
}: {
  entry: StoredObject | null
  token: string
  reachable: boolean
  onClose: () => void
  onDelete: (entry: StoredObject) => void
}) {
  if (!entry) return null

  const removable = entry.orphan || entry.isThumb

  return (
    <Dialog
      open
      side="right"
      onClose={onClose}
      title={entry.fileName}
      description={formatBytes(entry.size)}
      footer={
        <>
          {removable && (
            <Button
              variant="danger"
              icon={<Trash2 size={15} aria-hidden="true" />}
              onClick={() => onDelete(entry)}
            >
              Delete
            </Button>
          )}
          <Button variant="primary" icon={<X size={15} aria-hidden="true" />} onClick={onClose}>
            Close
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <div className="flex flex-wrap items-center gap-2">
          {entry.isThumb && <Badge tone="neutral">thumbnail</Badge>}
          {entry.orphan ? (
            <Badge tone="warning">orphan</Badge>
          ) : (
            <Badge tone="success">referenced</Badge>
          )}
          <Badge mono>{entry.contentType || 'unknown type'}</Badge>
        </div>

        {isImage(entry) && entry.collection !== '' && (
          <img
            src={fileHref(entry, token)}
            alt={entry.fileName}
            className="max-h-72 w-full rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken object-contain"
          />
        )}

        <div className="space-y-2 text-xs">
          <div className="flex items-start justify-between gap-3 border-b border-border-subtle pb-2">
            <span className="shrink-0 text-ink-muted">Key</span>
            <span className="min-w-0 text-right font-mono break-all text-ink">
              {entry.key}
              <CopyButton value={entry.key} size="icon" className="ml-1 size-6 align-middle" />
            </span>
          </div>

          <div className="flex items-center justify-between gap-3 border-b border-border-subtle pb-2">
            <span className="text-ink-muted">Collection</span>
            <span className="font-mono text-ink">{entry.collection || '—'}</span>
          </div>

          <div className="flex items-center justify-between gap-3 border-b border-border-subtle pb-2">
            <span className="text-ink-muted">Record</span>
            {reachable ? (
              <a
                href={routeHref({
                  kind: 'collection',
                  name: entry.collection,
                  tab: 'records',
                  focus: entry.recordId,
                })}
                className="truncate font-mono text-brand hover:underline"
              >
                {entry.recordId}
              </a>
            ) : (
              <span className="font-mono text-ink">{entry.recordId || '—'}</span>
            )}
          </div>

          <div className="flex items-center justify-between gap-3">
            <span className="text-ink-muted">Size</span>
            <span className="tabular-nums text-ink">{formatBytes(entry.size)}</span>
          </div>
        </div>

        {entry.orphan && (
          <p className="rounded-[var(--radius-control)] border border-warning/40 bg-warning/10 px-3 py-2 text-xs text-ink">
            No record references this file. It occupies space with no view of the application
            leading to it.
          </p>
        )}

        {!removable && (
          <p className="text-xs text-ink-muted">
            This file is referenced by its record: it's removed from that record's editor, which
            updates the reference at the same time.
          </p>
        )}
      </div>
    </Dialog>
  )
}

/**
 * File store inventory.
 *
 * The store only knows keys; this screen gives them back their meaning — which collection, which
 * record, and above all: is anyone still using it. That's the one question neither a file
 * explorer nor an S3 console can answer, because it takes the database to know.
 *
 * Filters sit under the header of the column they restrict, as in the log.
 */
export function StorageBrowser({ collections }: { collections: Collection[] }) {
  const toast = useToast()

  const [search, setSearch] = useState('')
  const [collection, setCollection] = useState('')
  const [kind, setKind] = useState('')
  const [orphansOnly, setOrphansOnly] = useState(false)
  const [page, setPage] = useState(1)
  const [perPage, setPerPage] = useState(50)
  const [selected, setSelected] = useState<StoredObject | null>(null)
  const [confirming, setConfirming] = useState<StoredObject[] | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [token, setToken] = useState('')

  const q = useDebounced(search)

  const { result, loading, error, reload } = useStorageObjects({
    page,
    perPage,
    collection,
    q,
    kind,
    orphansOnly,
  })

  // A short-lived token serves the previews: an <img> tag carries no header, and files from a
  // protected field are refused without it.
  useEffect(() => {
    let abandoned = false

    api.files
      .token()
      .then((issued) => {
        if (!abandoned) setToken(issued)
      })
      .catch(() => {
        // Without a token, previews of protected fields don't display; the inventory itself
        // remains fully readable.
      })

    return () => {
      abandoned = true
    }
  }, [])

  const items = result?.items ?? []
  const filtered = search !== '' || collection !== '' || kind !== '' || orphansOnly

  const criteria = `${q}|${collection}|${kind}|${orphansOnly}`

  useEffect(() => {
    setPage(1)
  }, [criteria])

  const reset = () => {
    setSearch('')
    setCollection('')
    setKind('')
    setOrphansOnly(false)
    setPage(1)
  }

  const remove = async (entries: StoredObject[]) => {
    setDeleting(true)

    try {
      const { deleted } = await api.storage.remove(entries.map((entry) => entry.key))

      toast.success(`${formatCount(deleted)} ${plural(deleted, 'object deleted', 'objects deleted')}.`)
      setSelected(null)
      await reload()
    } catch (failure) {
      toast.error(describeFailure(failure))
    } finally {
      setDeleting(false)
      setConfirming(null)
    }
  }

  const download = async () => {
    try {
      globalThis.location.href = await api.storage.archive({
        collection: collection === '' ? undefined : collection,
      })
    } catch (failure) {
      toast.error(describeFailure(failure))
    }
  }

  const orphans = result?.orphans ?? 0

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Reload">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Reload inventory"
            onClick={() => void reload()}
          >
            <RotateCw size={16} aria-hidden="true" />
          </Button>
        </Tooltip>

        {filtered && (
          <Button variant="outline" icon={<Eraser size={15} aria-hidden="true" />} onClick={reset}>
            Reset
          </Button>
        )}

        <Button
          variant="outline"
          icon={<Download size={15} aria-hidden="true" />}
          onClick={() => void download()}
        >
          Export files
        </Button>
      </PageActions>

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {orphans > 0 && !orphansOnly && (
        <div className="flex flex-wrap items-center gap-3 rounded-[var(--radius-card)] border border-warning/40 bg-warning/10 px-4 py-3 text-sm text-ink">
          <span className="flex-1">
            {formatCount(orphans)} {plural(orphans, 'object', 'objects')}{' '}
            {plural(orphans, 'matches', 'match')} no record.
          </span>
          <Button size="sm" variant="outline" onClick={() => setOrphansOnly(true)}>
            Isolate them
          </Button>
        </div>
      )}

      {loading && result === null && <TableSkeleton columns={6} />}

      <Card className="overflow-hidden">
        <Table bare caption="Store objects">
          <THead>
            {/* The label and its filter form a single block: the rule doesn't separate them,
                it closes off the pair below the filter row. */}
            <tr className="[&>th]:border-b-0">
              <Th className="w-14">Preview</Th>
              <Th>File</Th>
              <Th className="w-40">Collection</Th>
              <Th className="w-44">Record</Th>
              <Th className="w-24">Size</Th>
              <Th className="w-28">State</Th>
            </tr>

            <tr className="[&>td]:border-b [&>td]:border-border-subtle [&>td]:px-2 [&>td]:pb-2">
              <td>
                <SelectMenu<string>
                  value={kind === '' ? 'all' : kind}
                  size="sm"
                  aria-label="Object kind"
                  className="w-full"
                  options={[
                    { value: 'all', label: 'All' },
                    { value: 'files', label: 'Files' },
                    { value: 'thumbs', label: 'Thumbnails' },
                  ]}
                  onChange={(choice) => setKind(choice === 'all' ? '' : choice)}
                />
              </td>
              <td>
                <Input
                  value={search}
                  spellCheck={false}
                  aria-label="Filter on key"
                  placeholder="name or key"
                  className="h-8 font-mono text-xs"
                  onChange={(event) => setSearch(event.target.value)}
                />
              </td>
              <td>
                <SelectMenu<string>
                  value={collection === '' ? 'all' : collection}
                  size="sm"
                  aria-label="Collection"
                  className="w-full"
                  options={[
                    { value: 'all', label: 'All' },
                    ...collections.map((entry) => ({ value: entry.name, label: entry.name })),
                  ]}
                  onChange={(choice) => setCollection(choice === 'all' ? '' : choice)}
                />
              </td>
              <td />
              <td />
              <td>
                <SelectMenu<string>
                  value={orphansOnly ? 'orphans' : 'all'}
                  size="sm"
                  aria-label="Reference state"
                  className="w-full"
                  options={[
                    { value: 'all', label: 'All' },
                    { value: 'orphans', label: 'Orphans' },
                  ]}
                  onChange={(choice) => setOrphansOnly(choice === 'orphans')}
                />
              </td>
            </tr>
          </THead>

          <TBody>
            {items.map((entry) => (
              <Tr key={entry.key}>
                <Td>
                  <Preview entry={entry} token={token} />
                </Td>
                <Td className="max-w-0">
                  <button
                    type="button"
                    onClick={() => setSelected(entry)}
                    className="block w-full truncate text-left font-mono text-xs text-ink"
                    title={entry.key}
                  >
                    {entry.fileName}
                    {entry.isThumb && <span className="ml-1.5 text-ink-faint">thumbnail</span>}
                  </button>
                </Td>
                <Td className="font-mono text-xs text-ink-muted">{entry.collection || '—'}</Td>
                <Td className="max-w-0 text-xs">
                  {entry.recordId === '' ? (
                    <span className="text-ink-faint">—</span>
                  ) : collections.some((item) => item.name === entry.collection) ? (
                    <a
                      href={routeHref({
                        kind: 'collection',
                        name: entry.collection,
                        tab: 'records',
                        focus: entry.recordId,
                      })}
                      title={entry.recordId}
                      className="block truncate font-mono text-brand hover:underline"
                    >
                      {entry.recordId.slice(0, 8)}…
                    </a>
                  ) : (
                    <span className="font-mono text-ink-muted">{entry.recordId.slice(0, 8)}…</span>
                  )}
                </Td>
                <Td className="text-xs tabular-nums text-ink-muted">{formatBytes(entry.size)}</Td>
                <Td>
                  {entry.orphan ? (
                    <Badge tone="warning">orphan</Badge>
                  ) : (
                    <Badge tone="success">referenced</Badge>
                  )}
                </Td>
              </Tr>
            ))}

            {items.length === 0 && !loading && (
              <tr>
                <td colSpan={6} className="px-3 py-10">
                  <EmptyState
                    icon={
                      filtered ? (
                        <FolderOpen size={28} aria-hidden="true" />
                      ) : (
                        <ImageIcon size={28} aria-hidden="true" />
                      )
                    }
                    title="No objects"
                    description={
                      filtered
                        ? 'No object matches these criteria.'
                        : 'The store is empty. Files uploaded to a file-type field appear here.'
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

        {result && items.length > 0 && (
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border-subtle bg-surface-sunken px-4 py-2.5 text-xs text-ink-muted">
            <div className="flex items-center gap-2">
              <span className="tabular-nums">
                {formatCount(result.totalItems)} {plural(result.totalItems, 'object', 'objects')}
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
                  aria-label="Objects per page"
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

      <ObjectDetail
        entry={selected}
        token={token}
        reachable={
          selected !== null && collections.some((item) => item.name === selected.collection)
        }
        onClose={() => setSelected(null)}
        onDelete={(entry) => setConfirming([entry])}
      />

      <ConfirmDialog
        open={confirming !== null}
        busy={deleting}
        title="Delete this object?"
        message={
          confirming?.[0]?.isThumb
            ? 'This thumbnail will be regenerated on the next request: deleting it only frees up space.'
            : 'This file is referenced by no record. The deletion is permanent.'
        }
        confirmLabel="Delete"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        onConfirm={() => void remove(confirming ?? [])}
        onClose={() => setConfirming(null)}
      />
    </div>
  )
}
