import { Boxes, Plus } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { Collection } from '../api'
import { groupCollections } from '../hooks/useCollections'
import { routeHref } from '../hooks/useRoute'
import { Badge, Button, Skeleton, cn } from '../ui'
import { GROUPS } from './navigation'

function Group({
  title,
  icon: Icon,
  collections,
  activeName,
  onSelect,
}: {
  title: string
  icon: LucideIcon
  collections: Collection[]
  activeName: string | null
  onSelect: (name: string) => void
}) {
  if (collections.length === 0) return null

  return (
    <div className="mb-4">
      <h2 className="mb-1 flex items-center gap-1.5 px-2 text-[11px] font-semibold tracking-wide text-ink-faint uppercase">
        <Icon size={11} aria-hidden="true" />
        {title}
        <span className="ml-auto tabular-nums">{collections.length}</span>
      </h2>

      <ul className="space-y-0.5">
        {collections.map((collection) => (
          <li key={collection.id}>
            <a
              href={routeHref({ kind: 'collection', name: collection.name, tab: 'records' })}
              aria-current={collection.name === activeName ? 'page' : undefined}
              onClick={() => onSelect(collection.name)}
              className={cn(
                'flex items-center justify-between gap-2 rounded-[var(--radius-control)] px-2 py-1.5 text-sm transition-colors',
                collection.name === activeName
                  ? 'bg-brand-subtle font-medium text-brand'
                  : 'text-ink-muted hover:bg-surface-sunken hover:text-ink',
              )}
            >
              <span className="truncate font-mono text-xs">{collection.name}</span>
              {collection.kind === 'Auth' && <Badge tone="neutral">auth</Badge>}
            </a>
          </li>
        ))}
      </ul>
    </div>
  )
}

/**
 * Colonne de navigation : le catalogue des collections, et rien d'autre.
 *
 * Les commandes de session — thème, compte, déconnexion — vivent dans la barre du haut : elles ne
 * mènent nulle part dans l'application, donc elles n'ont pas leur place dans un menu de destinations.
 *
 * Aucun mode réduit à icônes : les entrées ne diffèrent que par leur nom, et une colonne de quatre
 * rem les rendrait toutes identiques.
 */
export function Sidebar({
  collections,
  loading,
  engine,
  activeName,
  onSelect,
  onCreate,
}: {
  collections: Collection[]
  loading: boolean
  engine: string
  activeName: string | null
  onSelect: (name: string) => void
  onCreate: () => void
}) {
  const groups = groupCollections(collections)

  return (
    <div className="flex h-full flex-col border-r border-border-subtle bg-surface">
      <div className="flex h-14 shrink-0 items-center gap-2.5 px-4">
        <span className="grid size-8 shrink-0 place-items-center rounded-lg bg-brand text-brand-ink">
          <Boxes size={17} aria-hidden="true" />
        </span>
        <div className="min-w-0">
          <p className="text-sm font-semibold tracking-tight text-ink">Cratebase</p>
          <p className="truncate text-[11px] text-ink-faint">
            {engine ? `moteur ${engine}` : 'moteur inconnu'}
          </p>
        </div>
      </div>

      <nav aria-label="Collections" className="scrollbar-none min-h-0 flex-1 overflow-y-auto px-2 pt-2">
        {loading && collections.length === 0 ? (
          <div className="space-y-1.5 p-1">
            {Array.from({ length: 5 }, (_, index) => (
              <Skeleton key={index} className="h-6" />
            ))}
          </div>
        ) : (
          GROUPS.map((group) => (
            <Group
              key={group.id}
              title={group.label}
              icon={group.icon}
              collections={groups[group.id]}
              activeName={activeName}
              onSelect={onSelect}
            />
          ))
        )}
      </nav>

      {/* Le bouton de création reste posé sous la liste qu'il alimente, à une place fixe : le
          chercher au bout d'un défilement en ferait une action qu'on croit absente. */}
      <div className="border-t border-border-subtle p-2">
        <Button
          className="w-full"
          icon={<Plus size={15} aria-hidden="true" />}
          onClick={onCreate}
        >
          Nouvelle collection
        </Button>
      </div>
    </div>
  )
}
