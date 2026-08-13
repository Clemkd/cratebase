import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { CircleHelp, Database, Plus, RotateCw, Search, Trash2, X } from 'lucide-react'
import { api, describeFailure, type Collection, type RecordValue } from '../api'
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

/** Cycle croissant → décroissant → tri par défaut, comme dans la plupart des consoles. */
function nextSort(sort: string, name: string): string {
  if (sort === name) return `-${name}`
  if (sort === `-${name}`) return ''

  return name
}

/**
 * Navigateur d'enregistrements.
 *
 * Trois blocs distincts, dans l'ordre où on s'en sert : ce qui restreint la liste, la liste, ce qui
 * la parcourt. Les actions qui *créent* ou *rechargent* ne filtrent rien : elles vivent hors du
 * formulaire de filtre, qui ne contient plus que le filtre lui-même et sa validation.
 */
export function RecordsBrowser({
  collection,
  collections,
}: {
  collection: Collection
  collections: Collection[]
}) {
  const toast = useToast()

  const [draftFilter, setDraftFilter] = useState('')
  const [filter, setFilter] = useState('')
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

  // Changer de collection remet la barre d'outils à zéro : un filtre écrit pour une autre table
  // désigne des champs qui n'existent pas ici, donc produirait une erreur 400 déroutante.
  useEffect(() => {
    setDraftFilter('')
    setFilter('')
    setSort('')
    setPage(1)
    setSelection([])
  }, [collection.name])

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

    if (failures[0]) toast.error(`Suppression partielle : ${failures[0]}`)
    else
      toast.success(
        `${selection.length} ${plural(selection.length, 'enregistrement supprimé', 'enregistrements supprimés')}.`,
      )

    await reload()
  }

  return (
    <div className="space-y-4">
      <Card className="flex flex-wrap items-center gap-2 px-4 py-3">
        {/* Le formulaire ne contient que le filtre : y laisser « Nouvel enregistrement » plaçait
            une action de création dans un envoi de recherche, deux gestes sans rapport. */}
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
              aria-label="Expression de filtre"
              placeholder="Filtre — ex. : online = true && views > 10"
              className="h-9 pr-9 pl-9 font-mono text-xs"
              onChange={(event) => setDraftFilter(event.target.value)}
            />
            {draftFilter !== '' && (
              <button
                type="button"
                aria-label="Effacer le filtre"
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

          <Button type="submit" size="sm" variant="outline" disabled={draftFilter === filter}>
            Appliquer
          </Button>

          <Tooltip content="Aide sur la syntaxe des filtres">
            <Button
              variant="ghost"
              size="icon"
              className="size-8"
              aria-label="Aide sur la syntaxe des filtres"
              onClick={() => setHelpOpen(true)}
            >
              <CircleHelp size={16} aria-hidden="true" />
            </Button>
          </Tooltip>
        </form>

        <div className="ml-auto flex items-center gap-2">
          <Tooltip content="Recharger">
            <Button
              variant="ghost"
              size="icon"
              className="size-8"
              aria-label="Recharger la liste"
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
            Nouvel enregistrement
          </Button>
        </div>
      </Card>

      {selection.length > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-[var(--radius-card)] border border-brand/30 bg-brand-subtle px-4 py-3">
          <p className="text-sm text-ink">
            {selection.length}{' '}
            {plural(selection.length, 'ligne sélectionnée', 'lignes sélectionnées')}
          </p>
          <div className="flex gap-2">
            <Button size="sm" onClick={() => setSelection([])}>
              Désélectionner
            </Button>
            <Button
              size="sm"
              variant="danger"
              icon={<Trash2 size={14} aria-hidden="true" />}
              onClick={() => setConfirming(true)}
            >
              Supprimer
            </Button>
          </div>
        </div>
      )}

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={Math.min(columns.length, 6)} />}

      {!loading && !error && items.length === 0 && (
        <EmptyState
          icon={<Database size={28} aria-hidden="true" />}
          title="Aucun enregistrement"
          description={
            filter
              ? 'Aucune ligne ne satisfait ce filtre. Vérifiez la syntaxe et les noms de champs.'
              : 'Cette collection est vide.'
          }
          action={
            <Button
              variant="primary"
              icon={<Plus size={15} aria-hidden="true" />}
              onClick={() => setEditing({ record: null })}
            >
              Créer le premier enregistrement
            </Button>
          }
        />
      )}

      {items.length > 0 && (
        // Table et pagination dans une même carte : la pagination appartient au tableau, la poser
        // dessous en ligne flottante en faisait un élément de page sans attache.
        <Card className="overflow-hidden">
          <Table bare caption={`Enregistrements de la collection ${collection.name}`}>
            <THead>
              <tr>
                <Th className="w-10">
                  <input
                    type="checkbox"
                    checked={allSelected}
                    aria-label="Tout sélectionner sur cette page"
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
                        aria-label={`Sélectionner ${id}`}
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
                        aria-label={`Supprimer ${id}`}
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
                  {plural(result.totalItems, 'enregistrement', 'enregistrements')}
                </span>
                <Badge>
                  page {result.page} / {Math.max(result.totalPages, 1)}
                </Badge>
              </div>

              <div className="flex items-center gap-2">
                <div className="flex items-center gap-1.5">
                  {/* Un `label` enveloppant ne nommerait rien : le déclencheur est un bouton, que
                      seul `aria-label` sait désigner. */}
                  <span aria-hidden="true">Par page</span>
                  <SelectMenu<string>
                    value={String(perPage)}
                    size="sm"
                    aria-label="Enregistrements par page"
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
        title={`Supprimer ${selection.length} ${plural(selection.length, 'enregistrement', 'enregistrements')} ?`}
        message="La suppression est définitive. Les relations en cascade emporteront les lignes qui dépendent de celles-ci."
        confirmLabel="Supprimer définitivement"
        onConfirm={() => void removeSelected()}
        onClose={() => setConfirming(false)}
      />
    </div>
  )
}
