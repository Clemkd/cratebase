import { useEffect, useState, type ReactNode } from 'react'
import {
  LogOut,
  Menu,
  Monitor,
  Moon,
  PanelLeftClose,
  PanelLeftOpen,
  Sun,
  X,
} from 'lucide-react'
import { api, type Collection, type Identity } from '../api'
import { useTheme, type ThemePreference } from '../hooks/useTheme'
import type { Route } from '../hooks/useRoute'
import { readFlag, writeFlag } from '../lib/preferences'
import { Button, SegmentedControl, Tooltip, cn } from '../ui'
import { Breadcrumbs } from './Breadcrumbs'
import { Sidebar } from './Sidebar'

const COLLAPSED_KEY = 'cratebase.sidebar.collapsed'

/**
 * Application shell: navigation column, top bar, content area.
 *
 * The height is fixed to the window's, and only the content scrolls: the column and the top bar
 * then stay visible while scrolling through a table of a thousand rows.
 */
export function AppShell({
  identity,
  onSignedOut,
  collections,
  loading,
  engine,
  appName,
  route,
  activeCollection,
  onSelectCollection,
  onCreateCollection,
  children,
}: {
  identity: Identity
  onSignedOut: () => void
  collections: Collection[]
  loading: boolean
  engine: string
  /** Instance name, as set in the admin area. */
  appName: string
  route: Route
  activeCollection: Collection | null
  onSelectCollection: (name: string) => void
  onCreateCollection: () => void
  children: ReactNode
}) {
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [collapsed, setCollapsed] = useState(() => readFlag(COLLAPSED_KEY, false))

  const collapse = (next: boolean) => {
    setCollapsed(next)
    writeFlag(COLLAPSED_KEY, next)
  }

  // The drawer closes on every navigation, otherwise it would hide the screen reached.
  useEffect(() => {
    setDrawerOpen(false)
  }, [route])

  // Escape closes the drawer: it's the expected gesture for an overlaid surface, and the veil
  // covering it can only be reached with the mouse.
  useEffect(() => {
    if (!drawerOpen) return

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setDrawerOpen(false)
    }

    globalThis.addEventListener('keydown', onKeyDown)
    return () => globalThis.removeEventListener('keydown', onKeyDown)
  }, [drawerOpen])

  const sidebar = (reduced: boolean) => (
    <Sidebar
      collections={collections}
      loading={loading}
      engine={engine}
      appName={appName}
      route={route}
      collapsed={reduced}
      activeName={activeCollection?.name ?? null}
      onSelect={onSelectCollection}
      onCreate={onCreateCollection}
      onExpand={() => collapse(false)}
    />
  )

  return (
    <div className="flex h-dvh overflow-hidden bg-canvas">
      {/* The width is carried here rather than in the column: it's what shifts the content, and
          animating it from a single place keeps the shift and the collapse from going out of sync. */}
      <div
        className={cn(
          'hidden shrink-0 transition-[width] duration-200 lg:block',
          collapsed ? 'w-16' : 'w-64',
        )}
      >
        {sidebar(collapsed)}
      </div>

      {/* The mobile drawer ignores collapsed mode: it opens over the content, so the width it
          takes up doesn't cost anyone anything. */}
      {drawerOpen && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <button
            type="button"
            aria-label="Close menu"
            className="absolute inset-0 bg-black/50"
            onClick={() => setDrawerOpen(false)}
          />
          <div className="absolute inset-y-0 left-0 w-64">{sidebar(false)}</div>
        </div>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 shrink-0 items-center gap-2 border-b border-border-subtle bg-surface/80 px-3 backdrop-blur sm:px-5">
          <button
            type="button"
            onClick={() => setDrawerOpen((current) => !current)}
            className="grid size-9 shrink-0 place-items-center rounded-[var(--radius-control)] text-ink-muted hover:bg-surface-sunken hover:text-ink lg:hidden"
            aria-label={drawerOpen ? 'Close menu' : 'Open menu'}
            aria-expanded={drawerOpen}
          >
            {drawerOpen ? <X size={16} aria-hidden="true" /> : <Menu size={16} aria-hidden="true" />}
          </button>

          {/* The collapse control lives here rather than in the column: reduced to a rail, the
              column no longer has room for a labeled button, and a button that moves depending on
              state is a button you have to hunt for. */}
          <Tooltip content={collapsed ? 'Expand menu' : 'Collapse menu'}>
            <button
              type="button"
              onClick={() => collapse(!collapsed)}
              className="hidden size-9 shrink-0 place-items-center rounded-[var(--radius-control)] text-ink-muted hover:bg-surface-sunken hover:text-ink lg:grid"
              aria-label={collapsed ? 'Expand menu' : 'Collapse menu'}
              aria-expanded={!collapsed}
            >
              {collapsed ? (
                <PanelLeftOpen size={16} aria-hidden="true" />
              ) : (
                <PanelLeftClose size={16} aria-hidden="true" />
              )}
            </button>
          </Tooltip>

          <Breadcrumbs route={route} collection={activeCollection} />

          <div className="ml-auto flex items-center gap-2">
            <ThemeToggle />
            <AccountMenu identity={identity} onSignedOut={onSignedOut} />
          </div>
        </header>

        <main className="min-w-0 flex-1 overflow-y-auto">{children}</main>
      </div>
    </div>
  )
}

function AccountMenu({
  identity,
  onSignedOut,
}: {
  identity: Identity
  onSignedOut: () => void
}) {
  const [busy, setBusy] = useState(false)
  const label = String(identity.fields.email ?? identity.id)

  return (
    <div className="flex items-center gap-1">
      <span
        title={label}
        className="hidden max-w-56 items-center gap-2 rounded-[var(--radius-control)] px-2 py-1 sm:flex"
      >
        <span className="grid size-7 shrink-0 place-items-center rounded-full bg-brand-subtle text-xs font-semibold text-brand">
          {label.slice(0, 1).toUpperCase()}
        </span>
        <span className="min-w-0">
          <span className="block truncate text-xs font-medium text-ink">{label}</span>
          <span className="block truncate text-[11px] text-ink-faint">
            {identity.isSuperuser ? 'superuser' : identity.collectionName}
          </span>
        </span>
      </span>

      <Tooltip content="Sign out">
        <Button
          variant="ghost"
          size="icon"
          aria-label="Sign out"
          loading={busy}
          onClick={async () => {
            setBusy(true)
            await api.auth.logout()
            onSignedOut()
          }}
        >
          <LogOut size={16} aria-hidden="true" />
        </Button>
      </Tooltip>
    </div>
  )
}

function ThemeToggle() {
  const { preference, setPreference } = useTheme()

  return (
    <SegmentedControl<ThemePreference>
      label="Console theme"
      value={preference}
      onChange={setPreference}
      // Segments without text: their accessible name is given explicitly, otherwise they would
      // announce themselves as "button" and nothing else.
      options={[
        {
          value: 'light',
          label: <Sun size={13} aria-hidden="true" />,
          title: 'Light theme',
          srLabel: 'Light theme',
        },
        {
          value: 'system',
          label: <Monitor size={13} aria-hidden="true" />,
          title: 'System theme',
          srLabel: 'System theme',
        },
        {
          value: 'dark',
          label: <Moon size={13} aria-hidden="true" />,
          title: 'Dark theme',
          srLabel: 'Dark theme',
        },
      ]}
    />
  )
}
