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
 * Coquille applicative : colonne de navigation, barre supérieure, zone de contenu.
 *
 * La hauteur est fixée à celle de la fenêtre et c'est le contenu seul qui défile : la colonne et la
 * barre du haut restent alors visibles pendant qu'on parcourt une table de mille lignes.
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
  /** Nom de l'instance, tel qu'il est réglé dans l'administration. */
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

  // Le tiroir se referme à chaque navigation, sinon il masquerait l'écran atteint.
  useEffect(() => {
    setDrawerOpen(false)
  }, [route])

  // Échap referme le tiroir : c'est le geste attendu d'une surface superposée, et le voile qui la
  // recouvre n'est atteignable qu'à la souris.
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
      {/* La largeur est portée ici et non dans la colonne : c'est elle qui décale le contenu, et
          l'animer d'un seul endroit évite que le décalage et le repli se désynchronisent. */}
      <div
        className={cn(
          'hidden shrink-0 transition-[width] duration-200 lg:block',
          collapsed ? 'w-16' : 'w-64',
        )}
      >
        {sidebar(collapsed)}
      </div>

      {/* Le tiroir mobile ignore le mode réduit : il s'ouvre par-dessus le contenu, donc la largeur
          qu'il occupe n'enlève rien à personne. */}
      {drawerOpen && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <button
            type="button"
            aria-label="Fermer le menu"
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
            aria-label={drawerOpen ? 'Fermer le menu' : 'Ouvrir le menu'}
            aria-expanded={drawerOpen}
          >
            {drawerOpen ? <X size={16} aria-hidden="true" /> : <Menu size={16} aria-hidden="true" />}
          </button>

          {/* La commande de repli est ici plutôt que dans la colonne : réduite à un rail, celle-ci
              n'a plus la largeur d'un bouton étiqueté, et un bouton qui change de place selon
              l'état est un bouton qu'on cherche. */}
          <Tooltip content={collapsed ? 'Déployer le menu' : 'Réduire le menu'}>
            <button
              type="button"
              onClick={() => collapse(!collapsed)}
              className="hidden size-9 shrink-0 place-items-center rounded-[var(--radius-control)] text-ink-muted hover:bg-surface-sunken hover:text-ink lg:grid"
              aria-label={collapsed ? 'Déployer le menu' : 'Réduire le menu'}
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
            {identity.isSuperuser ? 'superadministrateur' : identity.collectionName}
          </span>
        </span>
      </span>

      <Tooltip content="Se déconnecter">
        <Button
          variant="ghost"
          size="icon"
          aria-label="Se déconnecter"
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
      label="Thème de la console"
      value={preference}
      onChange={setPreference}
      // Segments sans texte : leur nom accessible est donné explicitement, sans quoi ils
      // s'annonceraient « bouton » et rien d'autre.
      options={[
        {
          value: 'light',
          label: <Sun size={13} aria-hidden="true" />,
          title: 'Thème clair',
          srLabel: 'Thème clair',
        },
        {
          value: 'system',
          label: <Monitor size={13} aria-hidden="true" />,
          title: 'Thème du système',
          srLabel: 'Thème du système',
        },
        {
          value: 'dark',
          label: <Moon size={13} aria-hidden="true" />,
          title: 'Thème sombre',
          srLabel: 'Thème sombre',
        },
      ]}
    />
  )
}
