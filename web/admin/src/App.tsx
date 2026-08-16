import { useCallback, useEffect, useMemo, useState } from 'react'
import { Plus } from 'lucide-react'
import { api, session, type Collection, type Identity } from './api'
import { useCollections } from './hooks/useCollections'
import { SCHEMA_TABS, useRoute, type AdminSection, type CollectionTab } from './hooks/useRoute'
import { AppShell } from './layout/AppShell'
import { Page } from './layout/Page'
import { TAB_LABELS, adminLabel } from './layout/navigation'
import { AccountsBrowser } from './screens/AccountsBrowser'
import { AdminProviders } from './screens/AdminProviders'
import { AdminSettings } from './screens/AdminSettings'
import { AdminStorage } from './screens/AdminStorage'
import { CollectionEditor } from './screens/CollectionEditor'
import { Dashboard } from './screens/Dashboard'
import { analyseIndexes, analyseRules } from './screens/CollectionHealth'
import { Login } from './screens/Login'
import { LogsBrowser } from './screens/LogsBrowser'
import { RecordsBrowser } from './screens/RecordsBrowser'
import { StorageBrowser } from './screens/StorageBrowser'
import { Superusers } from './screens/Superusers'
import {
  Badge,
  Button,
  EmptyState,
  ErrorBlock,
  LoadingBlock,
  TabPanel,
  Tabs,
  ToastProvider,
  useToast,
} from './ui'

/** What each admin section promises, in one sentence. */
const ADMIN_DESCRIPTIONS: Record<AdminSection, string> = {
  settings: 'Settings honored by the engine. Everything else belongs to the host configuration.',
  storage:
    'File store: active configuration, connection test, export. Read, never written.',
  superusers:
    'Accounts that administer the instance. They bypass every access rule on every collection.',
  providers: 'OAuth2 providers loaded at startup from the host configuration.',
}

export function App() {
  return (
    <ToastProvider>
      <Authenticated />
    </ToastProvider>
  )
}

function Authenticated() {
  const [identity, setIdentity] = useState<Identity | null>(null)
  const [checking, setChecking] = useState(true)
  const toast = useToast()

  const verify = useCallback(async () => {
    if (!session.token) {
      setIdentity(null)
      setChecking(false)
      return
    }

    try {
      // The session token may have been revoked server-side — signed out from another tab,
      // permissions withdrawn. It's verified on load rather than trusted just because it's present.
      setIdentity(await api.auth.me())
    } catch {
      session.token = null
      setIdentity(null)
    } finally {
      setChecking(false)
    }
  }, [])

  useEffect(() => {
    void verify()
  }, [verify])

  // A 401 on any call purges the token: all that's left is to bring back the login screen.
  useEffect(
    () =>
      session.onExpired(() => {
        setIdentity((current) => {
          if (current) toast.error('Session expired. Please sign in again.')
          return null
        })
      }),
    [toast],
  )

  if (checking) {
    return (
      <div className="flex min-h-dvh items-center justify-center bg-canvas">
        <LoadingBlock label="Verifying session…" />
      </div>
    )
  }

  if (!identity) {
    return <Login onAuthenticated={() => void verify()} />
  }

  return <Console identity={identity} onSignedOut={() => setIdentity(null)} />
}

/**
 * Badges carried by a collection's tabs.
 *
 * Computed from the saved definition: they report the state of what's in the database, not the
 * state of the in-progress draft — that's what the "General" tab shows.
 */
function useTabBadges(collection: Collection | null) {
  return useMemo(() => {
    if (!collection) return null

    const rules = analyseRules(collection.kind, collection.rules)
    const issues = analyseIndexes({
      indexes: collection.indexes,
      savedIndexes: collection.indexes,
      fields: collection.fields.map((field) => ({
        name: field.name,
        type: field.type,
        multiple: field.multiple,
      })),
      kind: collection.kind,
      collectionName: collection.name,
      isNew: false,
    })

    return { openRules: rules.open.length, issues: issues.length }
  }, [collection])
}

function Console({ identity, onSignedOut }: { identity: Identity; onSignedOut: () => void }) {
  const { collections, engine, loading, error, reload } = useCollections()
  const { route, navigate } = useRoute()

  // The instance name is configurable: it names the navigation column and the browser tab, which
  // is the only way to tell two consoles apart when they're open side by side.
  const [appName, setAppName] = useState('Cratebase')

  useEffect(() => {
    api.settings
      .get()
      .then((settings) => setAppName(settings.appName))
      .catch(() => {
        // Settings unreadable: the console remains usable under its default name.
      })
  }, [])

  useEffect(() => {
    globalThis.document.title = `${appName} — Admin`
  }, [appName])

  const superusers = collections.find((item) => item.name === api.auth.superusers) ?? null

  const selected =
    route.kind === 'collection'
      ? (collections.find((item) => item.name === route.name) ?? null)
      : null

  const badges = useTabBadges(selected)

  // A single tab bar for the whole collection: the four schema views sit at the same rank as
  // records. Two stacked bars — one for the collection, one for the schema — forced you to
  // remember which controlled what.
  const tabs: CollectionTab[] =
    selected?.kind === 'Auth'
      ? ['records', 'accounts', ...SCHEMA_TABS]
      : ['records', ...SCHEMA_TABS]

  const requested = route.kind === 'collection' ? route.tab : 'records'
  const activeTab = selected && tabs.includes(requested) ? requested : 'records'
  const schemaSection = SCHEMA_TABS.find((tab) => tab === activeTab) ?? null

  const badgeFor = (tab: CollectionTab) => {
    if (!selected || !badges) return undefined

    if (tab === 'fields') return <Badge>{selected.fields.length}</Badge>
    if (tab === 'indexes') {
      return badges.issues > 0 ? (
        <Badge tone="warning">{badges.issues}</Badge>
      ) : (
        <Badge>{selected.indexes.length}</Badge>
      )
    }
    if (tab === 'rules' && badges.openRules > 0) {
      return <Badge tone="danger">{badges.openRules} open</Badge>
    }
    if (tab === 'general' && selected.isSystem) return <Badge>read-only</Badge>

    return undefined
  }

  return (
    <AppShell
      identity={identity}
      onSignedOut={onSignedOut}
      collections={collections}
      loading={loading}
      engine={engine}
      appName={appName}
      route={route}
      activeCollection={selected}
      onSelectCollection={(name) => navigate({ kind: 'collection', name, tab: 'records' })}
      onCreateCollection={() => navigate({ kind: 'new', section: 'general' })}
    >
      {error && (
        <div className="px-4 pt-6 sm:px-6">
          <ErrorBlock message={error} onRetry={() => void reload()} />
        </div>
      )}

      {route.kind === 'new' && (
        <Page
          wide
          title="New collection"
          description="The engine creates the table and exposes its CRUD API as soon as it's saved."
        >
          <div className="space-y-4">
            <Tabs
              name="collection-vue"
              label="Sections of the new collection"
              active={route.section}
              onChange={(section) => navigate({ kind: 'new', section })}
              tabs={SCHEMA_TABS.map((tab) => ({ id: tab, label: TAB_LABELS[tab] }))}
            />

            <TabPanel name="collection-vue" id={route.section} active={route.section}>
              <CollectionEditor
                collection={null}
                collections={collections}
                section={route.section}
                onNavigate={(tab) => {
                  // A collection that doesn't exist yet has neither records nor accounts: only
                  // the schema views are reachable.
                  const section = SCHEMA_TABS.find((entry) => entry === tab)

                  if (section) navigate({ kind: 'new', section })
                }}
                onSaved={(name) => {
                  void reload()
                  navigate({ kind: 'collection', name, tab: 'records' })
                }}
                onCancel={() => navigate({ kind: 'home' })}
                onDeleted={() => navigate({ kind: 'home' })}
              />
            </TabPanel>
          </div>
        </Page>
      )}

      {route.kind === 'home' && (
        <Page
          wide
          title="Dashboard"
          description="What this process serves, and what it uses: engine, storage, volume."
        >
          <Dashboard onOpenLogs={() => navigate({ kind: 'logs' })} />
        </Page>
      )}

      {route.kind === 'files' && (
        <Page
          wide
          title="Files"
          description="Inventory of the store: what's stored, what references it, and what no longer serves any purpose."
        >
          <StorageBrowser collections={collections} />
        </Page>
      )}

      {route.kind === 'logs' && (
        <Page
          wide
          title="Logs"
          description="Requests served by the API, access denials, and administration events."
        >
          <LogsBrowser collections={collections} />
        </Page>
      )}

      {route.kind === 'admin' && (
        <Page
          wide={route.section !== 'settings'}
          title={adminLabel(route.section)}
          description={ADMIN_DESCRIPTIONS[route.section]}
        >
          {route.section === 'settings' && <AdminSettings onSaved={(next) => setAppName(next.appName)} />}

          {route.section === 'storage' && <AdminStorage />}

          {route.section === 'superusers' &&
            (superusers ? (
              <Superusers
                collection={superusers}
                identity={identity}
                onSignedOut={onSignedOut}
              />
            ) : (
              <LoadingBlock label="Loading superusers…" />
            ))}

          {route.section === 'providers' && <AdminProviders />}
        </Page>
      )}

      {route.kind === 'collection' && !loading && collections.length === 0 && !error && (
          <Page title="Console" description="No collection exists yet in this database.">
            <EmptyState
              title="No collections"
              description="Create one: the engine creates the table and exposes its CRUD API immediately."
              action={
                <Button
                  variant="primary"
                  icon={<Plus size={15} aria-hidden="true" />}
                  onClick={() => navigate({ kind: 'new', section: 'general' })}
                >
                  New collection
                </Button>
              }
            />
          </Page>
        )}

      {route.kind === 'collection' && selected && (
        <Page
          wide
          title={<span className="font-mono">{selected.name}</span>}
          meta={
            <>
              <Badge tone={selected.kind === 'Auth' ? 'brand' : 'neutral'}>
                {selected.kind === 'Auth' ? 'accounts collection' : 'data collection'}
              </Badge>
              <Badge>
                {selected.fields.length} field{selected.fields.length > 1 ? 's' : ''}
              </Badge>
              <Badge>{selected.indexes.length} index</Badge>
              {selected.isSystem && <Badge tone="warning">system collection</Badge>}
            </>
          }
        >
          <div className="space-y-4">
            <Tabs
              name="collection-vue"
              label={`Views of the ${selected.name} collection`}
              active={activeTab}
              onChange={(tab) => navigate({ kind: 'collection', name: selected.name, tab })}
              tabs={tabs.map((tab) => ({ id: tab, label: TAB_LABELS[tab], badge: badgeFor(tab) }))}
            />

            <TabPanel name="collection-vue" id="records" active={activeTab}>
              <RecordsBrowser
                key={selected.name}
                collection={selected}
                collections={collections}
                focus={route.kind === 'collection' ? route.focus : undefined}
              />
            </TabPanel>

            <TabPanel name="collection-vue" id="accounts" active={activeTab}>
              <AccountsBrowser
                key={selected.name}
                collection={selected}
                collections={collections}
              />
            </TabPanel>

            {/* A single mount for the four schema views: the draft is shared between them, so
                switching from "Fields" to "Rules" must not restart from the saved definition. */}
            {schemaSection && (
              <TabPanel name="collection-vue" id={schemaSection} active={activeTab}>
                <CollectionEditor
                  key={selected.name}
                  collection={selected}
                  collections={collections}
                  section={schemaSection}
                  onNavigate={(tab) =>
                    navigate({ kind: 'collection', name: selected.name, tab })
                  }
                  onSaved={(name) => {
                    void reload()
                    navigate({ kind: 'collection', name, tab: 'records' })
                  }}
                  onCancel={() =>
                    navigate({ kind: 'collection', name: selected.name, tab: 'records' })
                  }
                  onDeleted={() => {
                    void reload()
                    navigate({ kind: 'home' })
                  }}
                />
              </TabPanel>
            )}
          </div>
        </Page>
      )}
    </AppShell>
  )
}
