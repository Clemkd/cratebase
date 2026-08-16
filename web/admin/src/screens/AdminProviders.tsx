import { useCallback, useEffect, useState } from 'react'
import { KeyRound, RotateCw } from 'lucide-react'
import { api, describeFailure, type OAuthProvider } from '../api'
import { PageActions } from '../layout/Page'
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorBlock,
  LoadingBlock,
  Panel,
  TBody,
  THead,
  Table,
  Td,
  Th,
  Tooltip,
  Tr,
} from '../ui'

/**
 * External identity providers.
 *
 * Read-only, and that's a deliberate choice: a client ID and its secret are declared in the host
 * configuration — <code>Cratebase__OAuth2__…</code> environment variables — rather than in a
 * table the console can read and a backup would carry off. PocketBase stores them in the
 * database; Cratebase prefers that a database leak deliver no usable secret.
 *
 * The screen still earns its keep: it reports what the engine actually loaded, which is the only
 * way to verify that an environment variable was indeed picked up.
 */
export function AdminProviders() {
  const [providers, setProviders] = useState<OAuthProvider[] | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)

    try {
      setProviders((await api.instance()).providers)
      setError(null)
    } catch (failure) {
      setError(describeFailure(failure))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void reload()
  }, [reload])

  if (error) return <ErrorBlock message={error} onRetry={() => void reload()} />
  if (!providers) return <LoadingBlock label="Loading providers…" />

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Reload">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Reload providers"
            loading={loading}
            onClick={() => void reload()}
          >
            <RotateCw size={16} aria-hidden="true" />
          </Button>
        </Tooltip>
      </PageActions>

      {providers.length === 0 ? (
        <EmptyState
          icon={<KeyRound size={28} aria-hidden="true" />}
          title="No provider configured"
          description="Set Cratebase__OAuth2__Google__ClientId and …__ClientSecret in the host environment, then restart: the provider will appear here and on the login screen."
        />
      ) : (
        <Card className="overflow-hidden">
          <Table bare caption="Configured identity providers">
            <THead>
              <tr>
                <Th>Provider</Th>
                <Th className="w-24">State</Th>
                <Th>Authorization endpoint</Th>
                <Th className="w-40">Scopes</Th>
                <Th className="w-20">PKCE</Th>
              </tr>
            </THead>

            <TBody>
              {providers.map((provider) => (
                <Tr key={provider.name}>
                  <Td>
                    <span className="text-sm text-ink">{provider.displayName}</span>
                    <span className="ml-2 font-mono text-xs text-ink-faint">{provider.name}</span>
                  </Td>
                  <Td>
                    <Badge tone={provider.enabled ? 'success' : 'neutral'} dot>
                      {provider.enabled ? 'enabled' : 'inactive'}
                    </Badge>
                  </Td>
                  <Td className="max-w-0">
                    <span className="block truncate font-mono text-xs text-ink-muted">
                      {provider.authorizationUrl}
                    </span>
                  </Td>
                  <Td className="font-mono text-xs text-ink-muted">
                    {provider.scopes.join(', ') || '—'}
                  </Td>
                  <Td>
                    <Badge tone={provider.usePkce ? 'success' : 'warning'}>
                      {provider.usePkce ? 'yes' : 'no'}
                    </Badge>
                  </Td>
                </Tr>
              ))}
            </TBody>
          </Table>
        </Card>
      )}

      <Panel title="Where are they configured?">
        <div className="space-y-2 text-sm text-ink-muted">
          <p>
            Client identifiers are read at startup from the host configuration, never from the
            database. A secret absent from the database is a secret that no backup, no
            replication, and no export can leak.
          </p>
          <pre className="overflow-x-auto rounded-[var(--radius-control)] border border-border-subtle bg-surface-sunken px-3 py-2 font-mono text-[11px] leading-relaxed text-ink">
            {[
              'Cratebase__OAuth2__Google__ClientId=…',
              'Cratebase__OAuth2__Google__ClientSecret=…',
              'Cratebase__OAuth2__Facebook__ClientId=…',
              'Cratebase__OAuth2__Facebook__ClientSecret=…',
            ].join('\n')}
          </pre>
        </div>
      </Panel>
    </div>
  )
}
