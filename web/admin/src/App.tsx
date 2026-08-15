import { useCallback, useEffect, useMemo, useState } from 'react'
import { Plus } from 'lucide-react'
import { api, session, type Collection, type Identity } from './api'
import { useCollections } from './hooks/useCollections'
import { SCHEMA_TABS, useRoute, type AdminSection, type CollectionTab } from './hooks/useRoute'
import { AppShell } from './layout/AppShell'
import { Page } from './layout/Page'
import { TAB_LABELS, adminLabel } from './layout/navigation'
import { AccountsBrowser } from './screens/AccountsBrowser'
import { AdminOverview } from './screens/AdminOverview'
import { AdminProviders } from './screens/AdminProviders'
import { AdminSettings } from './screens/AdminSettings'
import { AdminStorage } from './screens/AdminStorage'
import { CollectionEditor } from './screens/CollectionEditor'
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

/** Ce que chaque section d'administration promet, en une phrase. */
const ADMIN_DESCRIPTIONS: Record<AdminSection, string> = {
  overview: "Ce que sert ce processus : moteur, stockage, version et volumétrie.",
  settings: "Réglages honorés par le moteur. Le reste appartient à la configuration de l'hôte.",
  storage:
    "Magasin des fichiers : configuration en vigueur, test de connexion, extraction. Lu, jamais écrit.",
  superusers:
    "Comptes qui administrent l'instance. Ils passent outre toutes les règles d'accès des collections.",
  providers: "Fournisseurs OAuth2 chargés au démarrage depuis la configuration de l'hôte.",
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
      // Le jeton en session peut avoir été révoqué côté serveur — déconnexion depuis un autre
      // onglet, droits retirés. On le vérifie au chargement plutôt que de faire confiance à sa
      // présence.
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

  // Un 401 sur n'importe quel appel purge le jeton : il ne reste qu'à ramener l'écran de connexion.
  useEffect(
    () =>
      session.onExpired(() => {
        setIdentity((current) => {
          if (current) toast.error('Session expirée. Reconnectez-vous.')
          return null
        })
      }),
    [toast],
  )

  if (checking) {
    return (
      <div className="flex min-h-dvh items-center justify-center bg-canvas">
        <LoadingBlock label="Vérification de la session…" />
      </div>
    )
  }

  if (!identity) {
    return <Login onAuthenticated={() => void verify()} />
  }

  return <Console identity={identity} onSignedOut={() => setIdentity(null)} />
}

/**
 * Pastilles portées par les onglets d'une collection.
 *
 * Calculées sur la définition enregistrée : elles disent l'état de ce qui est en base, pas celui du
 * brouillon en cours — c'est l'onglet « Général » qui montre le second.
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

  // Le nom de l'instance est réglable : il nomme la colonne de navigation et l'onglet du
  // navigateur, ce qui est la seule façon de distinguer deux consoles ouvertes côte à côte.
  const [appName, setAppName] = useState('Cratebase')

  useEffect(() => {
    api.settings
      .get()
      .then((settings) => setAppName(settings.appName))
      .catch(() => {
        // Réglages illisibles : la console reste utilisable sous son nom par défaut.
      })
  }, [])

  useEffect(() => {
    globalThis.document.title = `${appName} — administration`
  }, [appName])

  const superusers = collections.find((item) => item.name === api.auth.superusers) ?? null

  const selected =
    route.kind === 'collection'
      ? (collections.find((item) => item.name === route.name) ?? null)
      : null

  const badges = useTabBadges(selected)

  // Sans sélection explicite, on ouvre la première collection de données : atterrir sur un écran
  // vide alors que la base en contient donnerait l'impression que rien n'a été chargé.
  useEffect(() => {
    if (route.kind !== 'home' || collections.length === 0) return

    const first = collections.find((item) => !item.isSystem) ?? collections[0]

    if (first) navigate({ kind: 'collection', name: first.name, tab: 'records' })
  }, [route.kind, collections, navigate])

  // Une seule barre d'onglets pour toute la collection : les quatre vues du schéma y siègent au
  // même rang que les enregistrements. Deux barres empilées — l'une pour la collection, l'autre
  // pour le schéma — obligeaient à retenir laquelle commande quoi.
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
      return <Badge tone="danger">{badges.openRules} ouverte{badges.openRules > 1 ? 's' : ''}</Badge>
    }
    if (tab === 'general' && selected.isSystem) return <Badge>lecture seule</Badge>

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
          title="Nouvelle collection"
          description="Le moteur crée la table et expose son API CRUD dès l'enregistrement."
        >
          <div className="space-y-4">
            <Tabs
              name="collection-vue"
              label="Sections de la nouvelle collection"
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
                  // Une collection qui n'existe pas encore n'a ni enregistrements ni comptes :
                  // seules les vues du schéma sont atteignables.
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

      {route.kind === 'files' && (
        <Page
          wide
          title="Fichiers"
          description="Inventaire du magasin : ce qui est stocké, qui le référence, et ce qui ne sert plus."
        >
          <StorageBrowser collections={collections} />
        </Page>
      )}

      {route.kind === 'logs' && (
        <Page
          wide
          title="Journaux"
          description="Requêtes servies par l'API, refus d'accès et évènements d'administration."
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
          {route.section === 'overview' && (
            <AdminOverview onOpenLogs={() => navigate({ kind: 'logs' })} />
          )}

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
              <LoadingBlock label="Chargement des superadministrateurs…" />
            ))}

          {route.section === 'providers' && <AdminProviders />}
        </Page>
      )}

      {(route.kind === 'home' || route.kind === 'collection') &&
        !loading &&
        collections.length === 0 &&
        !error && (
          <Page title="Console" description="Aucune collection n'existe encore dans cette base.">
            <EmptyState
              title="Aucune collection"
              description="Créez-en une : le moteur crée la table et expose son API CRUD immédiatement."
              action={
                <Button
                  variant="primary"
                  icon={<Plus size={15} aria-hidden="true" />}
                  onClick={() => navigate({ kind: 'new', section: 'general' })}
                >
                  Nouvelle collection
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
                {selected.kind === 'Auth' ? 'collection de comptes' : 'collection de données'}
              </Badge>
              <Badge>
                {selected.fields.length} champ{selected.fields.length > 1 ? 's' : ''}
              </Badge>
              <Badge>{selected.indexes.length} index</Badge>
              {selected.isSystem && <Badge tone="warning">collection système</Badge>}
            </>
          }
        >
          <div className="space-y-4">
            <Tabs
              name="collection-vue"
              label={`Vues de la collection ${selected.name}`}
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

            {/* Un seul montage pour les quatre vues du schéma : le brouillon leur est commun, donc
                passer de « Champs » à « Règles » ne doit pas repartir de la définition enregistrée. */}
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
