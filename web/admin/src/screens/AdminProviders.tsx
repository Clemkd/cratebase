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
 * Fournisseurs d'identité externes.
 *
 * En lecture seule, et c'est un choix : un identifiant client et son secret se déclarent dans la
 * configuration de l'hôte — variables d'environnement <code>Cratebase__OAuth2__…</code> — et non
 * dans une table que la console sait lire et qu'une sauvegarde emporte. PocketBase les stocke en
 * base ; Cratebase préfère qu'une fuite de la base ne livre aucun secret exploitable.
 *
 * L'écran reste utile : il dit ce que le moteur a effectivement chargé, ce qui est la seule façon
 * de vérifier qu'une variable d'environnement a bien été prise en compte.
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
  if (!providers) return <LoadingBlock label="Lecture des fournisseurs…" />

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Recharger">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Recharger les fournisseurs"
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
          title="Aucun fournisseur configuré"
          description="Renseignez Cratebase__OAuth2__Google__ClientId et …__ClientSecret dans l'environnement de l'hôte, puis redémarrez : le fournisseur apparaîtra ici et sur l'écran de connexion."
        />
      ) : (
        <Card className="overflow-hidden">
          <Table bare caption="Fournisseurs d'identité configurés">
            <THead>
              <tr>
                <Th>Fournisseur</Th>
                <Th className="w-24">État</Th>
                <Th>Point d'autorisation</Th>
                <Th className="w-40">Portées</Th>
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
                      {provider.enabled ? 'activé' : 'inactif'}
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
                      {provider.usePkce ? 'oui' : 'non'}
                    </Badge>
                  </Td>
                </Tr>
              ))}
            </TBody>
          </Table>
        </Card>
      )}

      <Panel title="Où se configurent-ils ?">
        <div className="space-y-2 text-sm text-ink-muted">
          <p>
            Les identifiants clients sont lus au démarrage depuis la configuration de l'hôte, jamais
            depuis la base. Un secret absent de la base est un secret qu'aucune sauvegarde, aucune
            réplication et aucun export ne peut divulguer.
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
