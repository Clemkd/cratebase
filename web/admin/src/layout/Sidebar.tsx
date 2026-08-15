import { useId, useState, type ReactNode } from 'react'
import { Boxes, ChevronRight, Plus, Wrench } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { Collection } from '../api'
import { groupCollections } from '../hooks/useCollections'
import { routeHref, type AdminSection, type Route } from '../hooks/useRoute'
import { readFlag, writeFlag } from '../lib/preferences'
import { Badge, Button, Skeleton, Tooltip, cn } from '../ui'
import { ADMIN_ITEMS, FILES_ICON, GROUPS, LOGS_ICON } from './navigation'

const OPEN_KEYS = {
  collections: 'cratebase.sidebar.collections',
  administration: 'cratebase.sidebar.administration',
} as const

type MenuId = keyof typeof OPEN_KEYS

/** Entrée de premier niveau qui mène directement à un écran. */
function Entry({
  href,
  icon: Icon,
  label,
  active,
  collapsed,
  badge,
}: {
  href: string
  icon: LucideIcon
  label: string
  active: boolean
  collapsed: boolean
  badge?: ReactNode
}) {
  const link = (
    <a
      href={href}
      aria-current={active ? 'page' : undefined}
      aria-label={collapsed ? label : undefined}
      className={cn(
        'flex items-center gap-2.5 rounded-[var(--radius-control)] px-2 py-2 text-sm transition-colors',
        collapsed ? 'w-10 justify-center px-0' : 'w-full',
        active
          ? 'bg-brand-subtle font-medium text-brand'
          : 'text-ink-muted hover:bg-surface-sunken hover:text-ink',
      )}
    >
      <Icon size={16} aria-hidden="true" className="shrink-0" />
      {!collapsed && <span className="min-w-0 flex-1 truncate">{label}</span>}
      {!collapsed && badge}
    </a>
  )

  return collapsed ? <Tooltip content={label}>{link}</Tooltip> : link
}

/**
 * Entrée de premier niveau qui déploie un sous-menu.
 *
 * En mode réduit, l'entrée ne déploie rien : elle réclame le déploiement de la colonne entière.
 * Ouvrir un sous-menu de noms dans un rail de quatre rem afficherait une liste de mots tronqués au
 * troisième caractère.
 */
function Menu({
  id,
  icon: Icon,
  label,
  active,
  collapsed,
  open,
  count,
  onToggle,
  onExpand,
  children,
}: {
  id: MenuId
  icon: LucideIcon
  label: string
  active: boolean
  collapsed: boolean
  open: boolean
  count?: number
  onToggle: () => void
  onExpand: () => void
  children: ReactNode
}) {
  const panelId = useId()

  const button = (
    <button
      type="button"
      onClick={collapsed ? onExpand : onToggle}
      aria-expanded={collapsed ? undefined : open}
      aria-controls={collapsed || !open ? undefined : panelId}
      aria-label={collapsed ? `${label} — déployer le menu` : undefined}
      data-menu={id}
      className={cn(
        'flex items-center gap-2.5 rounded-[var(--radius-control)] px-2 py-2 text-sm transition-colors',
        collapsed ? 'w-10 justify-center px-0' : 'w-full',
        active ? 'font-medium text-ink' : 'text-ink-muted hover:bg-surface-sunken hover:text-ink',
      )}
    >
      <Icon size={16} aria-hidden="true" className={cn('shrink-0', active && 'text-brand')} />

      {!collapsed && (
        <>
          <span className="min-w-0 flex-1 truncate text-left">{label}</span>
          {count !== undefined && (
            <span className="shrink-0 text-[11px] tabular-nums text-ink-faint">{count}</span>
          )}
          <ChevronRight
            size={14}
            aria-hidden="true"
            className={cn('shrink-0 text-ink-faint transition-transform', open && 'rotate-90')}
          />
        </>
      )}
    </button>
  )

  return (
    <div>
      {collapsed ? <Tooltip content={label}>{button}</Tooltip> : button}

      {!collapsed && open && (
        // Le trait vertical rattache visuellement les enfants à leur menu : sans lui, un sous-menu
        // ouvert se confond avec le niveau du dessus dès qu'on a fait défiler la colonne.
        <div id={panelId} className="mt-0.5 ml-4 border-l border-border-subtle pl-2">
          {children}
        </div>
      )}
    </div>
  )
}

/** Une collection dans le sous-menu. */
function CollectionLink({
  collection,
  active,
  onSelect,
}: {
  collection: Collection
  active: boolean
  onSelect: (name: string) => void
}) {
  return (
    <li>
      <a
        href={routeHref({ kind: 'collection', name: collection.name, tab: 'records' })}
        aria-current={active ? 'page' : undefined}
        onClick={() => onSelect(collection.name)}
        className={cn(
          'flex items-center justify-between gap-2 rounded-[var(--radius-control)] px-2 py-1.5 transition-colors',
          active
            ? 'bg-brand-subtle font-medium text-brand'
            : 'text-ink-muted hover:bg-surface-sunken hover:text-ink',
        )}
      >
        <span className="truncate font-mono text-xs">{collection.name}</span>
        {collection.kind === 'Auth' && <Badge tone="neutral">auth</Badge>}
      </a>
    </li>
  )
}

/**
 * Colonne de navigation.
 *
 * Trois destinations de premier niveau : les collections, le journal, l'administration. Les
 * collections sont derrière un menu déployable parce qu'elles sont les seules à être en nombre
 * variable — une instance qui en compte quarante ne doit pas repousser « Journaux » hors de l'écran.
 *
 * Le mode réduit à icônes n'existe que depuis que ce premier niveau existe : tant que la colonne ne
 * contenait que des noms de collections, un rail d'icônes les aurait toutes rendues identiques.
 * C'est aussi pourquoi replier la colonne ne replie pas les collections dans le rail : on y clique
 * pour redéployer, jamais pour choisir à l'aveugle.
 *
 * Les commandes de session — thème, compte, déconnexion — restent dans la barre du haut : elles ne
 * mènent nulle part dans l'application, donc elles n'ont pas leur place dans un menu de destinations.
 */
export function Sidebar({
  collections,
  loading,
  engine,
  appName,
  route,
  activeName,
  collapsed,
  onSelect,
  onCreate,
  onExpand,
}: {
  collections: Collection[]
  loading: boolean
  engine: string
  appName: string
  route: Route
  activeName: string | null
  collapsed: boolean
  onSelect: (name: string) => void
  onCreate: () => void
  /** Déploie la colonne. Appelé quand un menu est cliqué en mode réduit. */
  onExpand: () => void
}) {
  const groups = groupCollections(collections)

  const [open, setOpen] = useState<Record<MenuId, boolean>>(() => ({
    collections: readFlag(OPEN_KEYS.collections, true),
    administration: readFlag(OPEN_KEYS.administration, false),
  }))

  const toggle = (id: MenuId) =>
    setOpen((current) => {
      const next = !current[id]

      writeFlag(OPEN_KEYS[id], next)

      return { ...current, [id]: next }
    })

  // Déployer la colonne depuis un menu réduit doit ouvrir *ce* menu : sinon le clic déploie une
  // colonne où la destination demandée reste fermée, et il faut un second clic pour la voir.
  const expandInto = (id: MenuId) => {
    setOpen((current) => ({ ...current, [id]: true }))
    writeFlag(OPEN_KEYS[id], true)
    onExpand()
  }

  const inCollections = route.kind === 'collection' || route.kind === 'new'
  const adminSection: AdminSection | null = route.kind === 'admin' ? route.section : null

  return (
    <div className="flex h-full flex-col border-r border-border-subtle bg-surface">
      <div
        className={cn(
          'flex h-14 shrink-0 items-center gap-2.5',
          collapsed ? 'justify-center px-0' : 'px-4',
        )}
      >
        <a
          href={routeHref({ kind: 'home' })}
          aria-label={appName}
          className="grid size-8 shrink-0 place-items-center rounded-lg bg-brand text-brand-ink"
        >
          <Boxes size={17} aria-hidden="true" />
        </a>

        {!collapsed && (
          <div className="min-w-0">
            <p className="truncate text-sm font-semibold tracking-tight text-ink">{appName}</p>
            <p className="truncate text-[11px] text-ink-faint">
              {engine ? `moteur ${engine}` : 'moteur inconnu'}
            </p>
          </div>
        )}
      </div>

      <nav
        aria-label="Navigation principale"
        className={cn('scrollbar-none min-h-0 flex-1 space-y-1 overflow-y-auto pt-2', collapsed ? 'px-3' : 'px-2')}
      >
        <Menu
          id="collections"
          icon={Boxes}
          label="Collections"
          active={inCollections}
          collapsed={collapsed}
          open={open.collections}
          count={collections.length}
          onToggle={() => toggle('collections')}
          onExpand={() => expandInto('collections')}
        >
          {loading && collections.length === 0 ? (
            <div className="space-y-1.5 p-1">
              {Array.from({ length: 4 }, (_, index) => (
                <Skeleton key={index} className="h-6" />
              ))}
            </div>
          ) : (
            GROUPS.map((group) => {
              const items = groups[group.id]

              if (items.length === 0) return null

              return (
                <div key={group.id} className="mb-2 last:mb-0">
                  <h2 className="mb-1 flex items-center gap-1.5 px-2 text-[11px] font-semibold tracking-wide text-ink-faint uppercase">
                    <group.icon size={11} aria-hidden="true" />
                    {group.label}
                    <span className="ml-auto tabular-nums">{items.length}</span>
                  </h2>

                  <ul className="space-y-0.5">
                    {items.map((collection) => (
                      <CollectionLink
                        key={collection.id}
                        collection={collection}
                        active={collection.name === activeName}
                        onSelect={onSelect}
                      />
                    ))}
                  </ul>
                </div>
              )
            })
          )}
        </Menu>

        <Entry
          href={routeHref({ kind: 'files' })}
          icon={FILES_ICON}
          label="Fichiers"
          active={route.kind === 'files'}
          collapsed={collapsed}
        />

        <Entry
          href={routeHref({ kind: 'logs' })}
          icon={LOGS_ICON}
          label="Journaux"
          active={route.kind === 'logs'}
          collapsed={collapsed}
        />

        <Menu
          id="administration"
          icon={Wrench}
          label="Administration"
          active={adminSection !== null}
          collapsed={collapsed}
          open={open.administration}
          onToggle={() => toggle('administration')}
          onExpand={() => expandInto('administration')}
        >
          <ul className="space-y-0.5">
            {ADMIN_ITEMS.map((item) => (
              <li key={item.id}>
                <a
                  href={routeHref({ kind: 'admin', section: item.id })}
                  aria-current={item.id === adminSection ? 'page' : undefined}
                  className={cn(
                    'flex items-center gap-2 rounded-[var(--radius-control)] px-2 py-1.5 text-[13px] transition-colors',
                    item.id === adminSection
                      ? 'bg-brand-subtle font-medium text-brand'
                      : 'text-ink-muted hover:bg-surface-sunken hover:text-ink',
                  )}
                >
                  <item.icon size={13} aria-hidden="true" className="shrink-0" />
                  <span className="truncate">{item.label}</span>
                </a>
              </li>
            ))}
          </ul>
        </Menu>
      </nav>

      {/* Le bouton de création reste posé sous la liste qu'il alimente, à une place fixe : le
          chercher au bout d'un défilement en ferait une action qu'on croit absente. */}
      <div className={cn('border-t border-border-subtle p-2', collapsed && 'flex justify-center')}>
        {collapsed ? (
          <Tooltip content="Nouvelle collection">
            <Button size="icon" aria-label="Nouvelle collection" onClick={onCreate}>
              <Plus size={16} aria-hidden="true" />
            </Button>
          </Tooltip>
        ) : (
          <Button className="w-full" icon={<Plus size={15} aria-hidden="true" />} onClick={onCreate}>
            Nouvelle collection
          </Button>
        )}
      </div>
    </div>
  )
}
