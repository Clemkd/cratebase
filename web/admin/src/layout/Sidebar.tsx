import { useCallback, useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import { Boxes, ChevronRight, Plus, Search, Wrench } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { Collection } from '../api'
import { sortCollections } from '../hooks/useCollections'
import { routeHref, type AdminSection, type Route } from '../hooks/useRoute'
import { readFlag, writeFlag } from '../lib/preferences'
import { collectionAlert, type CollectionAlert } from '../screens/CollectionHealth'
import { Badge, Input, Skeleton, Tooltip, cn } from '../ui'
import { ADMIN_ITEMS, DASHBOARD_ICON, FILES_ICON, LOGS_ICON } from './navigation'

const OPEN_KEYS = {
  collections: 'cratebase.sidebar.collections',
  administration: 'cratebase.sidebar.administration',
} as const

type MenuId = keyof typeof OPEN_KEYS

/**
 * Nombre de collections à partir duquel un champ de filtre apparaît.
 *
 * En deçà, la liste tient sous les yeux et le champ ne ferait que la raccourcir d'une ligne.
 */
const SEARCHABLE_FROM = 8

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

/** Délai de grâce avant fermeture d'une bulle, le temps que le curseur la rejoigne. */
const FLYOUT_GRACE = 140

/**
 * Ancrage d'une bulle de sous-menu sur le rail réduit.
 *
 * La bulle est posée en `fixed` aux coordonnées mesurées du déclencheur : la colonne défile dans
 * son propre cadre, et une bulle posée dans ce flux serait rognée par lui. Un défilement ou un
 * redimensionnement rend les coordonnées fausses, donc la ferme.
 */
function useFlyout(anchor: React.RefObject<HTMLDivElement | null>, enabled: boolean) {
  const [box, setBox] = useState<{ top: number; left: number; maxHeight: number } | null>(null)
  const closing = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  const close = useCallback(() => {
    clearTimeout(closing.current)
    setBox(null)
  }, [])

  const open = useCallback(() => {
    clearTimeout(closing.current)

    const rect = anchor.current?.getBoundingClientRect()

    if (!rect) return

    const margin = 8
    const top = Math.min(rect.top, Math.max(margin, globalThis.innerHeight - 320 - margin))

    setBox({
      top,
      left: rect.right + 6,
      maxHeight: Math.max(160, globalThis.innerHeight - top - margin),
    })
  }, [anchor])

  // La fermeture est différée : sans ce répit, le trajet du curseur entre l'icône et la bulle —
  // qui passe par six pixels de vide — refermerait la bulle avant qu'il n'y arrive.
  const leave = useCallback(() => {
    clearTimeout(closing.current)
    closing.current = setTimeout(() => setBox(null), FLYOUT_GRACE)
  }, [])

  useEffect(() => {
    if (!enabled) close()
  }, [enabled, close])

  useEffect(() => {
    if (box === null) return

    globalThis.addEventListener('scroll', close, true)
    globalThis.addEventListener('resize', close)

    return () => {
      globalThis.removeEventListener('scroll', close, true)
      globalThis.removeEventListener('resize', close)
    }
  }, [box, close])

  useEffect(() => () => clearTimeout(closing.current), [])

  return { box, open, leave, close }
}

/**
 * Entrée de premier niveau qui déploie un sous-menu.
 *
 * En mode réduit, le sous-menu ne s'ouvre pas dans le rail — quatre rem tronqueraient les noms au
 * troisième caractère — mais dans une bulle posée à sa droite, au survol comme au focus. La colonne
 * réduite reste ainsi une colonne de navigation complète : sans elle, replier la colonne revenait à
 * ne plus pouvoir atteindre que trois destinations sur quinze.
 *
 * Le clic, lui, redéploie la colonne : c'est le geste de celui qui vient s'installer, quand la
 * bulle est celui de qui passe.
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
  const anchor = useRef<HTMLDivElement>(null)

  const { box, open: openFlyout, leave, close } = useFlyout(anchor, collapsed)

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
    <div
      ref={anchor}
      onMouseEnter={collapsed ? openFlyout : undefined}
      onMouseLeave={collapsed ? leave : undefined}
      // Le focus ouvre la bulle comme le survol : une navigation au clavier n'a pas de curseur à
      // promener, et lui laisser le seul clic reviendrait à lui imposer le redéploiement.
      onFocus={collapsed ? openFlyout : undefined}
      onBlur={collapsed ? leave : undefined}
    >
      {collapsed && box === null ? <Tooltip content={label}>{button}</Tooltip> : button}

      {!collapsed && open && (
        // Le trait vertical rattache visuellement les enfants à leur menu : sans lui, un sous-menu
        // ouvert se confond avec le niveau du dessus dès qu'on a fait défiler la colonne.
        <div id={panelId} className="mt-0.5 ml-4 border-l border-border-subtle pl-2">
          {children}
        </div>
      )}

      {collapsed && box && (
        <div
          role="group"
          aria-label={label}
          onMouseEnter={openFlyout}
          onMouseLeave={leave}
          style={{ top: box.top, left: box.left, maxHeight: box.maxHeight }}
          className="fixed z-50 w-60 overflow-y-auto overscroll-contain rounded-[var(--radius-card)] border border-border-subtle bg-surface p-2 shadow-popover"
        >
          <p className="mb-1 flex items-center gap-2 px-1 text-xs font-semibold text-ink">
            <Icon size={13} aria-hidden="true" className="shrink-0 text-brand" />
            {label}
            {count !== undefined && (
              <span className="ml-auto tabular-nums text-ink-faint">{count}</span>
            )}
          </p>

          {/* Un clic dans la bulle mène quelque part : elle n'a plus lieu d'être ouverte à
              l'arrivée sur la destination. */}
          <div onClick={close}>{children}</div>
        </div>
      )}
    </div>
  )
}

/**
 * Création d'une collection, posée comme une collection.
 *
 * Elle ferme la liste qu'elle alimente, au lieu d'un bouton isolé sous la colonne : c'est là qu'on
 * regarde en constatant que la collection cherchée n'existe pas, et la forme d'une entrée de liste
 * dit mieux que celle d'un bouton ce que l'action va produire.
 */
function CreateCollectionLink({ onCreate }: { onCreate: () => void }) {
  return (
    <li>
      <button
        type="button"
        onClick={onCreate}
        className="flex w-full items-center gap-2 rounded-[var(--radius-control)] px-2 py-1.5 text-left text-ink-muted transition-colors hover:bg-surface-sunken hover:text-brand"
      >
        <Plus size={13} aria-hidden="true" className="shrink-0" />
        <span className="truncate text-xs">Nouvelle collection</span>
      </button>
    </li>
  )
}

/**
 * Une collection dans le sous-menu.
 *
 * Le repère de criticité est posé au bout de la ligne, à une position constante d'une collection à
 * l'autre : c'est ce qui permet de balayer la colonne et de voir d'un coup laquelle réclame de
 * l'attention, sans lire un seul nom.
 */
function CollectionLink({
  collection,
  alert,
  active,
  onSelect,
}: {
  collection: Collection
  alert: CollectionAlert | null
  active: boolean
  onSelect: (name: string) => void
}) {
  return (
    <li>
      <a
        href={routeHref({ kind: 'collection', name: collection.name, tab: 'records' })}
        aria-current={active ? 'page' : undefined}
        onClick={() => onSelect(collection.name)}
        title={collection.name}
        className={cn(
          'flex items-center gap-2 rounded-[var(--radius-control)] px-2 py-1.5 transition-colors',
          active
            ? 'bg-brand-subtle font-medium text-brand'
            : 'text-ink-muted hover:bg-surface-sunken hover:text-ink',
        )}
      >
        <span className="min-w-0 flex-1 truncate font-mono text-xs">{collection.name}</span>

        <span className="flex shrink-0 items-center gap-1">
          {alert && (
            <Badge tone={alert.tone} title={alert.reason}>
              {alert.count}
              {/* Le nombre seul ne dit pas de quoi il s'agit à un lecteur d'écran, et la couleur ne
                  lui dit rien du tout : la raison est donc lue, pas seulement survolée. */}
              <span className="sr-only"> — {alert.reason}</span>
            </Badge>
          )}
        </span>
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
  const sorted = useMemo(() => sortCollections(collections), [collections])

  // Le diagnostic est recalculé quand le catalogue change, pas à chaque rendu : la colonne se
  // redessine à chaque navigation, et le balayage des index de quarante collections n'a aucune
  // raison d'être refait pour un changement de page.
  const [filter, setFilter] = useState('')

  const visible = useMemo(() => {
    const needle = filter.trim().toLowerCase()

    return sorted.filter((collection) => collection.name.toLowerCase().includes(needle))
  }, [sorted, filter])

  const alerts = useMemo(() => {
    const found = new Map<string, CollectionAlert>()

    for (const collection of collections) {
      const alert = collectionAlert(collection)

      if (alert) found.set(collection.name, alert)
    }

    return found
  }, [collections])

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
        <Entry
          href={routeHref({ kind: 'home' })}
          icon={DASHBOARD_ICON}
          label="Tableau de bord"
          active={route.kind === 'home'}
          collapsed={collapsed}
        />

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
            <>
              {/* Le champ n'apparaît qu'au-delà de ce qu'un œil embrasse d'un coup. En dessous, il
                  coûterait une ligne pour filtrer une liste déjà entièrement visible. */}
              {collections.length > SEARCHABLE_FROM && (
                <div className="relative mb-1">
                  <Search
                    size={13}
                    aria-hidden="true"
                    className="pointer-events-none absolute top-1/2 left-2 -translate-y-1/2 text-ink-faint"
                  />
                  <Input
                    value={filter}
                    spellCheck={false}
                    aria-label="Filtrer les collections"
                    placeholder="Filtrer"
                    className="h-7 pl-7 font-mono text-xs"
                    onChange={(event) => setFilter(event.target.value)}
                  />
                </div>
              )}

              <ul className="space-y-0.5">
                {visible.map((collection) => (
                  <CollectionLink
                    key={collection.id}
                    collection={collection}
                    alert={alerts.get(collection.name) ?? null}
                    active={collection.name === activeName}
                    onSelect={onSelect}
                  />
                ))}
              </ul>

              {visible.length === 0 && (
                <p className="px-2 py-1.5 text-xs text-ink-faint">Aucune collection de ce nom.</p>
              )}
            </>
          )}

          {/* Hors de la liste : la création n'appartient à aucun groupe — c'est elle qui décidera
              du sien. */}
          <ul className={cn(collections.length > 0 && 'mt-1 border-t border-border-subtle pt-1')}>
            <CreateCollectionLink onCreate={onCreate} />
          </ul>
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

    </div>
  )
}
