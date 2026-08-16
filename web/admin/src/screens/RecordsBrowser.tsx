import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { CircleHelp, Database, Filter, Plus, RotateCw, Search, Trash2, X } from 'lucide-react'
import { api, describeFailure, type Collection, type RecordValue } from '../api'
import { useRealtime } from '../hooks/useRealtime'
import { useRecords } from '../hooks/useRecords'
import { visibleFields } from '../lib/fields'
import { formatCount, plural } from '../lib/format'
import {
  Badge,
  Button,
  Card,
  ConfirmDialog,
  EmptyState,
  ErrorBlock,
  Input,
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
  useToast,
  type SortDirection,
} from '../ui'
import { FilterHelp } from './FilterHelp'
import { RecordCell } from './RecordCell'
import { RecordEditor } from './RecordEditor'

const PER_PAGE_CHOICES = [25, 50, 100]

function directionOf(sort: string, name: string): SortDirection {
  if (sort === name) return 'asc'
  if (sort === `-${name}`) return 'desc'

  return null
}

/** Cycle ascending → descending → default sort, as in most consoles. */
function nextSort(sort: string, name: string): string {
  if (sort === name) return `-${name}`
  if (sort === `-${name}`) return ''

  return name
}

/**
 * Records browser.
 *
 * Three distinct blocks, in the order they're used: what restricts the list, the list, what
 * paginates it. Actions that *create* or *reload* filter nothing: they live outside the filter
 * form, which now contains nothing but the filter itself and its submission.
 */
export function RecordsBrowser({
  collection,
  collections,
  focus,
}: {
  collection: Collection
  collections: Collection[]
  /**
   * Record designated by the address. It becomes an ordinary filter, visible in the bar and
   * clearable like any other — rather than a hidden selection that would suggest the collection
   * contains only one row.
   */
  focus?: string
}) {
  const toast = useToast()

  // The value comes from the address, so from the outside: it's escaped before entering a
  // string in the filter language. A real identifier has neither an apostrophe nor a backslash,
  // but that's precisely the assumption a forged link tries to defeat.
  const focusFilter = focus
    ? `id = '${focus.replaceAll('\\', '\\\\').replaceAll("'", "\\'")}'`
    : ''

  const [draftFilter, setDraftFilter] = useState(focusFilter)
  const [filter, setFilter] = useState(focusFilter)
  const [sort, setSort] = useState('')
  const [page, setPage] = useState(1)
  const [perPage, setPerPage] = useState(25)
  const [selection, setSelection] = useState<string[]>([])
  const [editing, setEditing] = useState<{ record: RecordValue | null } | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [helpOpen, setHelpOpen] = useState(false)

  const columns = useMemo(() => visibleFields(collection), [collection])
  const query = useMemo(() => ({ page, perPage, filter, sort }), [page, perPage, filter, sort])
  const { result, loading, error, reload } = useRecords(collection.name, query)

  // The console consumes its own realtime stream: a write from elsewhere — another tab, a
  // client, a background worker — refreshes the list without needing a reload. It's also the
  // only proof worth having that the stream works for everyone else.
  useRealtime([collection.name], () => void reload())


  // Switching collections resets the toolbar: a filter written for another table targets fields
  // that don't exist here, and would therefore produce a confusing 400 error. A record
  // designated by the address takes the place of that empty filter.
  useEffect(() => {
    setDraftFilter(focusFilter)
    setFilter(focusFilter)
    setSort('')
    setPage(1)
    setSelection([])
  }, [collection.name, focusFilter])

  const items = result?.items ?? []
  const ids = items.map((item) => String(item.id ?? ''))
  const allSelected = ids.length > 0 && ids.every((id) => selection.includes(id))

  const apply = (event: FormEvent) => {
    event.preventDefault()
    setPage(1)
    setFilter(draftFilter)
  }

  const removeSelected = async () => {
    setDeleting(true)

    const failures: string[] = []

    for (const id of selection) {
      try {
        await api.records.remove(collection.name, id)
      } catch (failure) {
        failures.push(describeFailure(failure))
      }
    }

    setDeleting(false)
    setConfirming(false)
    setSelection([])

    if (failures[0]) toast.error(`Partial deletion: ${failures[0]}`)
    else
      toast.success(
        `${selection.length} ${plural(selection.length, 'record deleted', 'records deleted')}.`,
      )

    await reload()
  }

  return (
    <div className="space-y-4">
      <Card className="flex flex-wrap items-center gap-2 px-4 py-3">
        {/* The form contains only the filter: leaving "New record" in it would place a create
            action inside a search submission, two unrelated gestures. */}
        <form onSubmit={apply} className="flex min-w-64 flex-1 items-center gap-2">
          <div className="relative min-w-48 flex-1">
            <Search
              size={14}
              aria-hidden="true"
              className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-ink-faint"
            />
            <Input
              value={draftFilter}
              spellCheck={false}
              aria-label="Filter expression"
              placeholder="Filter — e.g. online = true && views > 10"
              className="h-9 pr-9 pl-9 font-mono text-xs"
              onChange={(event) => setDraftFilter(event.target.value)}
            />
            {draftFilter !== '' && (
              <button
                type="button"
                aria-label="Clear filter"
                onClick={() => {
                  setDraftFilter('')
                  setFilter('')
                  setPage(1)
                }}
                className="absolute top-1/2 right-2.5 -translate-y-1/2 rounded p-0.5 text-ink-faint hover:text-ink"
              >
                <X size={14} aria-hidden="true" />
              </button>
            )}
          </div>

          <Button
            type="submit"
            size="sm"
            variant="outline"
            icon={<Filter size={14} aria-hidden="true" />}
            disabled={draftFilter === filter}
          >
            Apply
          </Button>

          <Tooltip content="Help on filter syntax">
            <Button
              variant="ghost"
              size="icon"
              className="size-8"
              aria-label="Help on filter syntax"
              onClick={() => setHelpOpen(true)}
            >
              <CircleHelp size={16} aria-hidden="true" />
            </Button>
          </Tooltip>
        </form>

        <div className="ml-auto flex items-center gap-2">
          <Tooltip content="Reload">
            <Button
              variant="ghost"
              size="icon"
              className="size-8"
              aria-label="Reload list"
              onClick={() => void reload()}
            >
              <RotateCw size={16} aria-hidden="true" />
            </Button>
          </Tooltip>

          <Button
            size="sm"
            variant="primary"
            icon={<Plus size={15} aria-hidden="true" />}
            onClick={() => setEditing({ record: null })}
          >
            New record
          </Button>
        </div>
      </Card>

      {selection.length > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-[var(--radius-card)] border border-brand/30 bg-brand-subtle px-4 py-3">
          <p className="text-sm text-ink">
            {selection.length}{' '}
            {plural(selection.length, 'row selected', 'rows selected')}
          </p>
          <div className="flex gap-2">
            <Button size="sm" onClick={() => setSelection([])}>
              Deselect
            </Button>
            <Button
              size="sm"
              variant="danger"
              icon={<Trash2 size={14} aria-hidden="true" />}
              onClick={() => setConfirming(true)}
            >
              Delete
            </Button>
          </div>
        </div>
      )}

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={Math.min(columns.length, 6)} />}

      {!loading && !error && items.length === 0 && (
        <EmptyState
          icon={<Database size={28} aria-hidden="true" />}
          title="No records"
          description={
            filter
              ? 'No row satisfies this filter. Check the syntax and field names.'
              : 'This collection is empty.'
          }
          action={
            <Button
              variant="primary"
              icon={<Plus size={15} aria-hidden="true" />}
              onClick={() => setEditing({ record: null })}
            >
              Create the first record
            </Button>
          }
        />
      )}

      {items.length > 0 && (
        // Table and pagination in the same card: pagination belongs to the table, placing it
        // below as a floating row would make it a page element with no anchor.
        <Card className="overflow-hidden">
          <Table bare caption={`Records in the ${collection.name} collection`}>
            <THead>
              <tr>
                <Th className="w-10">
                  <input
                    type="checkbox"
                    checked={allSelected}
                    aria-label="Select all on this page"
                    className="size-4 cursor-pointer rounded border-border-strong accent-brand"
                    onChange={(event) => setSelection(event.target.checked ? ids : [])}
                  />
                </Th>

                {columns.map((field) => (
                  <SortableTh
                    key={field.id}
                    label={field.name}
                    direction={directionOf(sort, field.name)}
                    onSort={() => {
                      setSort(nextSort(sort, field.name))
                      setPage(1)
                    }}
                  />
                ))}

                <Th className="w-12">
                  <span className="sr-only">Actions</span>
                </Th>
              </tr>
            </THead>

            <TBody>
              {items.map((item) => {
                const id = String(item.id ?? '')
                const selected = selection.includes(id)

                return (
                  <Tr key={id} selected={selected}>
                    <Td>
                      <input
                        type="checkbox"
                        checked={selected}
                        aria-label={`Select ${id}`}
                        className="size-4 cursor-pointer rounded border-border-strong accent-brand"
                        onChange={(event) =>
                          setSelection((current) =>
                            event.target.checked
                              ? [...current, id]
                              : current.filter((entry) => entry !== id),
                          )
                        }
                      />
                    </Td>

                    {columns.map((field) => (
                      <Td key={field.id}>
                        <button
                          type="button"
                          onClick={() => setEditing({ record: item })}
                          className="block w-full max-w-full cursor-pointer text-left"
                        >
                          <RecordCell field={field} record={item} />
                        </button>
                      </Td>
                    ))}

                    <Td>
                      <Button
                        variant="ghost"
                        size="icon"
                        className="size-8 hover:text-danger"
                        aria-label={`Delete ${id}`}
                        onClick={() => {
                          setSelection([id])
                          setConfirming(true)
                        }}
                      >
                        <Trash2 size={15} aria-hidden="true" />
                      </Button>
                    </Td>
                  </Tr>
                )
              })}
            </TBody>
          </Table>

          {result && (
            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border-subtle bg-surface-sunken px-4 py-2.5 text-xs text-ink-muted">
              <div className="flex items-center gap-2">
                <span className="tabular-nums">
                  {formatCount(result.totalItems)}{' '}
                  {plural(result.totalItems, 'record', 'records')}
                </span>
                <Badge>
                  page {result.page} / {Math.max(result.totalPages, 1)}
                </Badge>
              </div>

              <div className="flex items-center gap-2">
                <div className="flex items-center gap-1.5">
                  {/* A wrapping `label` would name nothing: the trigger is a button, which only
                      `aria-label` can designate. */}
                  <span aria-hidden="true">Per page</span>
                  <SelectMenu<string>
                    value={String(perPage)}
                    size="sm"
                    aria-label="Records per page"
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

                <Button
                  size="sm"
                  disabled={result.page <= 1}
                  onClick={() => setPage(result.page - 1)}
                >
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
      )}

      <FilterHelp
        open={helpOpen}
        collection={collection}
        onClose={() => setHelpOpen(false)}
        onUseExample={(expression) => {
          setDraftFilter(expression)
          setFilter(expression)
          setPage(1)
        }}
      />

      {editing && (
        <RecordEditor
          open
          collection={collection}
          collections={collections}
          record={editing.record}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            void reload()
          }}
        />
      )}

      <ConfirmDialog
        open={confirming}
        busy={deleting}
        title={`Delete ${selection.length} ${plural(selection.length, 'record', 'records')}?`}
        message="The deletion is permanent. Cascading relations will take with them the rows that depend on these."
        confirmLabel="Delete permanently"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        onConfirm={() => void removeSelected()}
        onClose={() => setConfirming(false)}
      />
    </div>
  )
}
