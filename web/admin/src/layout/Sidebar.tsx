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
 * Number of collections above which a filter field appears.
 *
 * Below that, the list fits within view and the field would only shorten it by one line.
 */
const SEARCHABLE_FROM = 8

/** Top-level entry that leads directly to a screen. */
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

/** Grace delay before closing a flyout, giving the cursor time to reach it. */
const FLYOUT_GRACE = 140

/**
 * Anchoring of a submenu flyout on the collapsed rail.
 *
 * The flyout is placed `fixed` at the trigger's measured coordinates: the column scrolls within
 * its own frame, and a flyout placed in that flow would be clipped by it. A scroll or a resize
 * makes the coordinates stale, so it closes it.
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

  // Closing is deferred: without this grace period, the cursor's path from the icon to the
  // flyout — which crosses six pixels of empty space — would close the flyout before it arrives.
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
 * Top-level entry that expands a submenu.
 *
 * In collapsed mode, the submenu doesn't open in the rail — four rem would truncate names at the
 * third character — but in a flyout placed to its right, on hover as on focus. The collapsed
 * column thus remains a full navigation column: without this, collapsing the column would leave
 * only three destinations reachable out of fifteen.
 *
 * The click, meanwhile, re-expands the column: it's the gesture of someone settling in, whereas
 * the flyout is for someone just passing through.
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
      aria-label={collapsed ? `${label} — expand menu` : undefined}
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
      // Focus opens the flyout just like hover: keyboard navigation has no cursor to move
      // around, and leaving it only the click would force it to re-expand the column.
      onFocus={collapsed ? openFlyout : undefined}
      onBlur={collapsed ? leave : undefined}
    >
      {collapsed && box === null ? <Tooltip content={label}>{button}</Tooltip> : button}

      {!collapsed && open && (
        // The vertical rule visually ties the children to their menu: without it, an open
        // submenu blends into the level above as soon as the column has been scrolled.
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

          {/* A click in the flyout leads somewhere: it no longer needs to stay open once you've
              arrived at the destination. */}
          <div onClick={close}>{children}</div>
        </div>
      )}
    </div>
  )
}

/**
 * Collection creation, presented as a collection entry.
 *
 * It closes off the list it feeds, rather than sitting as an isolated button below the column:
 * that's where you look when you notice the collection you wanted doesn't exist, and the shape
 * of a list entry says better than a button what the action will produce.
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
        <span className="truncate text-xs">New collection</span>
      </button>
    </li>
  )
}

/**
 * A collection in the submenu.
 *
 * The severity marker sits at the end of the row, at a constant position from one collection to
 * the next: that's what lets you scan the column and see at a glance which one needs attention,
 * without reading a single name.
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
              {/* The number alone doesn't tell a screen reader what it's about, and the color
                  tells it nothing at all: so the reason is read out, not just hinted at visually. */}
              <span className="sr-only"> — {alert.reason}</span>
            </Badge>
          )}
        </span>
      </a>
    </li>
  )
}

/**
 * Navigation column.
 *
 * Three top-level destinations: collections, logs, admin. Collections sit behind an expandable
 * menu because they're the only ones present in variable numbers — an instance with forty of
 * them shouldn't push "Logs" off the screen.
 *
 * The collapsed icon-rail mode only exists since this top level was introduced: as long as the
 * column held nothing but collection names, an icon rail would have made them all identical.
 * That's also why collapsing the column doesn't collapse collections into the rail: you click
 * there to re-expand, never to pick blindly.
 *
 * Session controls — theme, account, sign out — stay in the top bar: they don't lead anywhere in
 * the application, so they have no place in a menu of destinations.
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
  /** Expands the column. Called when a menu is clicked in collapsed mode. */
  onExpand: () => void
}) {
  const sorted = useMemo(() => sortCollections(collections), [collections])

  // The diagnostic is recomputed when the catalog changes, not on every render: the column
  // redraws on every navigation, and scanning the indexes of forty collections has no reason to
  // be redone for a mere page change.
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

  // Expanding the column from a collapsed menu must open *that* menu: otherwise the click
  // expands a column where the requested destination stays closed, requiring a second click to
  // see it.
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
              {engine ? `${engine} engine` : 'unknown engine'}
            </p>
          </div>
        )}
      </div>

      <nav
        aria-label="Main navigation"
        className={cn('scrollbar-none min-h-0 flex-1 space-y-1 overflow-y-auto pt-2', collapsed ? 'px-3' : 'px-2')}
      >
        <Entry
          href={routeHref({ kind: 'home' })}
          icon={DASHBOARD_ICON}
          label="Dashboard"
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
              {/* The field only appears beyond what an eye can take in at a glance. Below that,
                  it would cost a line to filter a list that's already fully visible. */}
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
                    aria-label="Filter collections"
                    placeholder="Filter"
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
                <p className="px-2 py-1.5 text-xs text-ink-faint">No collection with that name.</p>
              )}
            </>
          )}

          {/* Outside the list: creation belongs to no group — it's the one that will decide its
              own. */}
          <ul className={cn(collections.length > 0 && 'mt-1 border-t border-border-subtle pt-1')}>
            <CreateCollectionLink onCreate={onCreate} />
          </ul>
        </Menu>

        <Entry
          href={routeHref({ kind: 'files' })}
          icon={FILES_ICON}
          label="Files"
          active={route.kind === 'files'}
          collapsed={collapsed}
        />

        <Entry
          href={routeHref({ kind: 'logs' })}
          icon={LOGS_ICON}
          label="Logs"
          active={route.kind === 'logs'}
          collapsed={collapsed}
        />

        <Menu
          id="administration"
          icon={Wrench}
          label="Admin"
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
