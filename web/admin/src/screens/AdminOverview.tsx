import { useCallback, useEffect, useState } from 'react'
import {
  Boxes,
  Database,
  HardDrive,
  RotateCw,
  ScrollText,
  ShieldAlert,
} from 'lucide-react'
import { api, describeFailure, type Instance } from '../api'
import { formatUptime } from '../lib/logs'
import { formatCount, formatDateTime } from '../lib/format'
import { PageActions, Stat, StatGrid } from '../layout/Page'
import { Badge, Button, ErrorBlock, LoadingBlock, Panel, Tooltip, cn } from '../ui'

/** Une ligne de la fiche d'instance. */
function Line({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex items-start justify-between gap-4 border-b border-border-subtle py-2 last:border-0">
      <span className="shrink-0 text-xs text-ink-muted">{label}</span>
      <span className={cn('min-w-0 text-right text-xs break-all text-ink', mono && 'font-mono')}>
        {value}
      </span>
    </div>
  )
}

/**
 * Aperçu de l'instance.
 *
 * Le premier écran de l'administration ne modifie rien : il répond à « sur quoi suis-je en train de
 * travailler ». Moteur, stockage et répertoire de données y figurent parce que ce sont les trois
 * réponses qu'on cherche quand une console ressemble à une autre — et se tromper d'instance est la
 * façon la plus banale de perdre des données.
 */
export function AdminOverview({ onOpenLogs }: { onOpenLogs: () => void }) {
  const [instance, setInstance] = useState<Instance | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)

    try {
      setInstance(await api.instance())
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
  if (!instance) return <LoadingBlock label="Lecture de l'instance…" />

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Recharger">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Recharger l'aperçu"
            loading={loading}
            onClick={() => void reload()}
          >
            <RotateCw size={16} aria-hidden="true" />
          </Button>
        </Tooltip>
      </PageActions>

      <StatGrid>
        <Stat
          label="Moteur de base"
          value={instance.engine}
          hint="Modifiable en configuration d'hôte uniquement"
          icon={<Database size={16} aria-hidden="true" />}
          tone="brand"
        />
        <Stat
          label="Stockage des fichiers"
          value={instance.storage}
          hint={instance.dataDirectory}
          icon={<HardDrive size={16} aria-hidden="true" />}
        />
        <Stat
          label="Collections"
          value={formatCount(instance.collections.total)}
          hint={`${instance.collections.system} système, ${instance.collections.auth} de comptes`}
          icon={<Boxes size={16} aria-hidden="true" />}
        />
        <Stat
          label="Entrées de journal"
          value={formatCount(instance.logs.total)}
          hint={
            instance.logs.retentionDays === 0
              ? 'Conservation illimitée'
              : `Conservées ${instance.logs.retentionDays} jours`
          }
          icon={<ScrollText size={16} aria-hidden="true" />}
          onClick={onOpenLogs}
          actionLabel="Ouvrir le journal"
        />
      </StatGrid>

      {/* Les pertes ne sont annoncées que lorsqu'il y en a : une tuile « 0 perdue » en permanence
          finirait par ne plus être lue, et c'est précisément la valeur qu'il faut voir changer. */}
      {instance.logs.dropped > 0 && (
        <div className="flex items-start gap-2.5 rounded-[var(--radius-card)] border border-warning/40 bg-warning/10 px-4 py-3">
          <ShieldAlert size={16} aria-hidden="true" className="mt-0.5 shrink-0 text-warning" />
          <p className="text-sm text-ink">
            {formatCount(instance.logs.dropped)} entrées ont été perdues depuis le démarrage : le
            tampon d'écriture a débordé. Le journal est donc incomplet sur les périodes de forte
            charge.
          </p>
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel title="Instance" description="Ce que sert ce processus, et depuis quand.">
          <Line label="Nom" value={instance.appName} />
          <Line label="URL publique" value={instance.appUrl || '— non renseignée'} mono={Boolean(instance.appUrl)} />
          <Line label="Version" value={instance.version} mono />
          <Line label="Exécution" value={instance.runtime} mono />
          <Line label="Démarrée le" value={formatDateTime(instance.startedAt)} />
          <Line label="En service depuis" value={formatUptime(instance.uptimeSeconds)} />
          <Line label="Préfixe de l'API" value={instance.apiPrefix} mono />
          <Line label="Répertoire de données" value={instance.dataDirectory} mono />
        </Panel>

        <Panel
          title="Répartition des collections"
          description="Les collections système appartiennent au moteur et ne sont pas supprimables."
        >
          <ul className="space-y-2">
            {[
              { label: 'Données', value: instance.collections.data },
              { label: 'Comptes', value: instance.collections.auth },
              { label: 'Vues', value: instance.collections.view },
              { label: 'Système', value: instance.collections.system },
            ].map((row) => (
              <li key={row.label} className="flex items-center justify-between gap-3 text-sm">
                <span className="text-ink-muted">{row.label}</span>
                <span className="tabular-nums text-ink">{formatCount(row.value)}</span>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel
          title="Journalisation"
          description="État courant du journal des requêtes."
          actions={
            <Badge tone={instance.logs.enabled ? 'success' : 'warning'} dot>
              {instance.logs.enabled ? 'active' : 'suspendue'}
            </Badge>
          }
        >
          <Line label="Entrées conservées" value={formatCount(instance.logs.total)} />
          <Line
            label="Rétention"
            value={
              instance.logs.retentionDays === 0
                ? 'illimitée'
                : `${instance.logs.retentionDays} jours`
            }
          />
          <Line label="Entrées perdues" value={formatCount(instance.logs.dropped)} />
        </Panel>

        <Panel
          title="Fournisseurs d'identité"
          description="Configurés par l'hôte : leurs secrets ne transitent jamais par la console."
        >
          {instance.providers.length === 0 ? (
            <p className="text-sm text-ink-muted">Aucun fournisseur externe n'est configuré.</p>
          ) : (
            <ul className="space-y-2">
              {instance.providers.map((provider) => (
                <li key={provider.name} className="flex items-center justify-between gap-3 text-sm">
                  <span className="truncate text-ink">{provider.displayName}</span>
                  <Badge tone={provider.enabled ? 'success' : 'neutral'} dot>
                    {provider.enabled ? 'activé' : 'inactif'}
                  </Badge>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  )
}
