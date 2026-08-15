import { useCallback, useEffect, useState } from 'react'
import { Boxes, Database, HardDrive, RotateCw, ScrollText, ShieldAlert } from 'lucide-react'
import { api, describeFailure, type Instance, type Usage } from '../api'
import { formatUptime } from '../lib/logs'
import { formatBytes, formatCount, formatDateTime } from '../lib/format'
import { PageActions, Stat, StatGrid } from '../layout/Page'
import { Badge, Button, ErrorBlock, LoadingBlock, Panel, Tooltip, cn } from '../ui'
import { UsageDonut, type UsageSlice } from './UsageDonut'

/** Teintes des parts, portées par les jetons du thème plutôt que par des valeurs figées. */
const SLICES = {
  database: { fill: 'var(--color-brand)', swatch: 'bg-brand' },
  files: { fill: 'var(--color-success)', swatch: 'bg-success' },
  other: { fill: 'var(--color-border-strong)', swatch: 'bg-border-strong' },
  free: { fill: 'var(--color-border-subtle)', swatch: 'bg-border-subtle' },
} as const

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
 * Volume dont la capacité n'est pas connue.
 *
 * Ni PostgreSQL ni S3 n'exposent de limite de façon portable : le premier n'a pas de requête pour
 * l'espace restant de son volume, le second n'impose aucune borne par seau. Plutôt qu'un anneau
 * bâti sur un dénominateur inventé — qui se lirait comme une mesure —, le chiffre est donné seul,
 * avec la variable qui rendrait la jauge possible.
 */
function UngaugedVolume({ bytes, variable }: { bytes: number; variable: string }) {
  return (
    <div className="space-y-2">
      <p className="text-2xl font-semibold tabular-nums text-ink">{formatBytes(bytes)}</p>
      <p className="text-xs text-ink-muted">
        Aucune capacité n'est déclarée, et ce volume n'en expose aucune que l'on puisse lire.
        Renseignez <code className="font-mono text-ink">{variable}</code> sur l'hôte pour obtenir une
        jauge ; sans elle, il n'y aurait qu'un anneau plein qui ne mesurerait rien.
      </p>
    </div>
  )
}

/**
 * Tableau de bord de l'instance.
 *
 * Le premier écran de l'administration ne modifie rien : il répond à « sur quoi suis-je en train de
 * travailler, et combien de place cela prend-il ». Moteur, stockage et répertoire de données y
 * figurent parce que ce sont les trois réponses qu'on cherche quand une console ressemble à une
 * autre — et se tromper d'instance est la façon la plus banale de perdre des données.
 *
 * L'occupation est mesurée par un appel distinct de la fiche d'instance : elle interroge le moteur
 * et parcourt le magasin, donc elle coûte, et son échec ne doit pas emporter le reste de l'écran.
 */
export function Dashboard({ onOpenLogs }: { onOpenLogs: () => void }) {
  const [instance, setInstance] = useState<Instance | null>(null)
  const [usage, setUsage] = useState<Usage | null>(null)
  const [usageError, setUsageError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)

    const [described, measured] = await Promise.allSettled([api.instance(), api.usage()])

    if (described.status === 'fulfilled') {
      setInstance(described.value)
      setError(null)
    } else {
      setError(describeFailure(described.reason))
    }

    if (measured.status === 'fulfilled') {
      setUsage(measured.value)
      setUsageError(null)
    } else {
      setUsageError(describeFailure(measured.reason))
    }

    setLoading(false)
  }, [])

  useEffect(() => {
    void reload()
  }, [reload])

  if (error) return <ErrorBlock message={error} onRetry={() => void reload()} />
  if (!instance) return <LoadingBlock label="Lecture de l'instance…" />

  const host = usage?.host
  const database = usage?.database
  const files = usage?.files

  // Le disque de l'hôte porte ce que l'instance y écrit et tout ce qui l'y précédait. La part
  // « autres usages » n'est pas du remplissage : sans elle, un disque partagé avec le reste du
  // système paraîtrait vide alors qu'il ne l'est pas.
  const hostSlices: UsageSlice[] = []

  if (host?.available) {
    if (database?.onHostDisk) {
      hostSlices.push({ label: `Base ${database.engine}`, bytes: database.bytes, ...SLICES.database })
    }

    if (files?.onHostDisk) {
      hostSlices.push({ label: 'Fichiers', bytes: files.bytes, ...SLICES.files })
    }

    const known = hostSlices.reduce((sum, slice) => sum + slice.bytes, 0)
    const used = Math.max(0, host.totalBytes - host.freeBytes)

    hostSlices.push({ label: 'Autres usages', bytes: Math.max(0, used - known), ...SLICES.other })
    hostSlices.push({ label: 'Libre', bytes: host.freeBytes, ...SLICES.free })
  }

  const remoteDatabase = database !== undefined && !database.onHostDisk
  const bucket = files?.kind === 's3' ? files : null

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Recharger">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Recharger le tableau de bord"
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
          hint={database ? formatBytes(database.bytes) : "Modifiable en configuration d'hôte uniquement"}
          icon={<Database size={16} aria-hidden="true" />}
          tone="brand"
        />
        <Stat
          label="Stockage des fichiers"
          value={instance.storage}
          hint={files ? `${formatCount(files.objects)} objets, ${formatBytes(files.bytes)}` : instance.dataDirectory}
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

      {usageError && (
        <div className="rounded-[var(--radius-card)] border border-border-subtle bg-surface px-4 py-3 text-sm text-ink-muted">
          L'occupation n'a pas pu être mesurée : {usageError}
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        {host?.available && (
          <Panel
            title="Disque de l'hôte"
            description={`Volume ${host.path} — ce que l'instance y écrit, et ce qui l'y précédait.`}
          >
            <UsageDonut slices={hostSlices} total={host.totalBytes} caption="au total" />
          </Panel>
        )}

        {host && !host.available && (
          <Panel title="Disque de l'hôte" description="Volume non mesurable.">
            <p className="text-sm text-ink-muted">
              La capacité de <code className="font-mono">{host.path}</code> n'a pas pu être lue —
              montage réseau, système de fichiers non reconnu, ou droit manquant.
            </p>
          </Panel>
        )}

        {remoteDatabase && (
          <Panel
            title={`Base ${database.engine}`}
            description="Serveur distinct de l'hôte : sa capacité ne se déduit pas du disque local."
            actions={
              database.capacityBytes > 0 ? undefined : <Badge tone="neutral">sans jauge</Badge>
            }
          >
            {database.capacityBytes > 0 ? (
              <UsageDonut
                total={database.capacityBytes}
                caption="déclarés"
                slices={[
                  { label: 'Données', bytes: database.bytes, ...SLICES.database },
                  {
                    label: 'Libre',
                    bytes: Math.max(0, database.capacityBytes - database.bytes),
                    ...SLICES.free,
                  },
                ]}
              />
            ) : (
              <UngaugedVolume
                bytes={database.bytes}
                variable="Cratebase__Postgres__CapacityBytes"
              />
            )}
          </Panel>
        )}

        {bucket && (
          <Panel
            title="Seau S3"
            description="Objets stockés hors de l'hôte, vignettes comprises."
            actions={bucket.capacityBytes > 0 ? undefined : <Badge tone="neutral">sans jauge</Badge>}
          >
            {bucket.capacityBytes > 0 ? (
              <UsageDonut
                total={bucket.capacityBytes}
                caption="déclarés"
                slices={[
                  { label: 'Fichiers', bytes: bucket.bytes, ...SLICES.files },
                  {
                    label: 'Libre',
                    bytes: Math.max(0, bucket.capacityBytes - bucket.bytes),
                    ...SLICES.free,
                  },
                ]}
              />
            ) : (
              <UngaugedVolume bytes={bucket.bytes} variable="Cratebase__S3__CapacityBytes" />
            )}
          </Panel>
        )}

        <Panel title="Instance" description="Ce que sert ce processus, et depuis quand.">
          <Line label="Nom" value={instance.appName} />
          <Line
            label="URL publique"
            value={instance.appUrl || '— non renseignée'}
            mono={Boolean(instance.appUrl)}
          />
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
              instance.logs.retentionDays === 0 ? 'illimitée' : `${instance.logs.retentionDays} jours`
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
