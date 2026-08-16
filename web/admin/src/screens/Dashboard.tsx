import { useCallback, useEffect, useState } from 'react'
import { Boxes, Database, HardDrive, RotateCw, ScrollText, ShieldAlert } from 'lucide-react'
import { api, describeFailure, type Instance, type Usage } from '../api'
import { formatUptime } from '../lib/logs'
import { formatBytes, formatCount, formatDateTime } from '../lib/format'
import { PageActions, Stat, StatGrid } from '../layout/Page'
import { Badge, Button, ErrorBlock, LoadingBlock, Panel, Tooltip, cn } from '../ui'
import { UsageDonut, type UsageSlice } from './UsageDonut'

/** Slice tones, carried by theme tokens rather than fixed values. */
const SLICES = {
  database: { fill: 'var(--color-brand)', swatch: 'bg-brand' },
  files: { fill: 'var(--color-success)', swatch: 'bg-success' },
  other: { fill: 'var(--color-border-strong)', swatch: 'bg-border-strong' },
  free: { fill: 'var(--color-border-subtle)', swatch: 'bg-border-subtle' },
} as const

/** A line in the instance info card. */
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
 * A volume whose capacity is unknown.
 *
 * Neither PostgreSQL nor S3 expose a limit in a portable way: the former has no query for its
 * volume's remaining space, the latter imposes no per-bucket bound. Rather than a ring built on
 * an invented denominator — which would read as a measurement —, the figure is given alone, with
 * the variable that would make a gauge possible.
 */
function UngaugedVolume({ bytes, variable }: { bytes: number; variable: string }) {
  return (
    <div className="space-y-2">
      <p className="text-2xl font-semibold tabular-nums text-ink">{formatBytes(bytes)}</p>
      <p className="text-xs text-ink-muted">
        No capacity is declared, and this volume exposes none that can be read. Set{' '}
        <code className="font-mono text-ink">{variable}</code> on the host to get a gauge;
        without it, there would only be a full ring measuring nothing.
      </p>
    </div>
  )
}

/**
 * Instance dashboard.
 *
 * The first admin screen changes nothing: it answers "what am I working on, and how much space
 * does it take?" Engine, storage, and data directory appear here because they're the three
 * answers you look for when one console looks like another — and mistaking one instance for
 * another is the most mundane way to lose data.
 *
 * Usage is measured by a call distinct from the instance info: it queries the engine and walks
 * the store, so it genuinely costs something, and its failure must not take down the rest of the
 * screen.
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
  if (!instance) return <LoadingBlock label="Loading instance…" />

  const host = usage?.host
  const database = usage?.database
  const files = usage?.files

  // The host disk carries what the instance writes to it and everything that preceded it there.
  // The "other usage" slice isn't padding: without it, a disk shared with the rest of the system
  // would look empty when it isn't.
  const hostSlices: UsageSlice[] = []

  if (host?.available) {
    if (database?.onHostDisk) {
      hostSlices.push({ label: `${database.engine} database`, bytes: database.bytes, ...SLICES.database })
    }

    if (files?.onHostDisk) {
      hostSlices.push({ label: 'Files', bytes: files.bytes, ...SLICES.files })
    }

    const known = hostSlices.reduce((sum, slice) => sum + slice.bytes, 0)
    const used = Math.max(0, host.totalBytes - host.freeBytes)

    hostSlices.push({ label: 'Other usage', bytes: Math.max(0, used - known), ...SLICES.other })
    hostSlices.push({ label: 'Free', bytes: host.freeBytes, ...SLICES.free })
  }

  const remoteDatabase = database !== undefined && !database.onHostDisk
  const bucket = files?.kind === 's3' ? files : null

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Reload">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Reload dashboard"
            loading={loading}
            onClick={() => void reload()}
          >
            <RotateCw size={16} aria-hidden="true" />
          </Button>
        </Tooltip>
      </PageActions>

      <StatGrid>
        <Stat
          label="Database engine"
          value={instance.engine}
          hint={database ? formatBytes(database.bytes) : 'Changeable only in host configuration'}
          icon={<Database size={16} aria-hidden="true" />}
          tone="brand"
        />
        <Stat
          label="File storage"
          value={instance.storage}
          hint={files ? `${formatCount(files.objects)} objects, ${formatBytes(files.bytes)}` : instance.dataDirectory}
          icon={<HardDrive size={16} aria-hidden="true" />}
        />
        <Stat
          label="Collections"
          value={formatCount(instance.collections.total)}
          hint={`${instance.collections.system} system, ${instance.collections.auth} accounts`}
          icon={<Boxes size={16} aria-hidden="true" />}
        />
        <Stat
          label="Log entries"
          value={formatCount(instance.logs.total)}
          hint={
            instance.logs.retentionDays === 0
              ? 'Unlimited retention'
              : `Kept ${instance.logs.retentionDays} days`
          }
          icon={<ScrollText size={16} aria-hidden="true" />}
          onClick={onOpenLogs}
          actionLabel="Open logs"
        />
      </StatGrid>

      {/* Losses are only announced when there are some: a "0 dropped" tile displayed forever
          would eventually stop being read, and that's precisely the value that needs watching. */}
      {instance.logs.dropped > 0 && (
        <div className="flex items-start gap-2.5 rounded-[var(--radius-card)] border border-warning/40 bg-warning/10 px-4 py-3">
          <ShieldAlert size={16} aria-hidden="true" className="mt-0.5 shrink-0 text-warning" />
          <p className="text-sm text-ink">
            {formatCount(instance.logs.dropped)} entries have been dropped since startup: the
            write buffer overflowed. The log is therefore incomplete over periods of heavy load.
          </p>
        </div>
      )}

      {usageError && (
        <div className="rounded-[var(--radius-card)] border border-border-subtle bg-surface px-4 py-3 text-sm text-ink-muted">
          Usage could not be measured: {usageError}
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        {host?.available && (
          <Panel
            title="Host disk"
            description={`Volume ${host.path} — what the instance writes to it, and what preceded it there.`}
          >
            <UsageDonut slices={hostSlices} total={host.totalBytes} caption="total" />
          </Panel>
        )}

        {host && !host.available && (
          <Panel title="Host disk" description="Volume not measurable.">
            <p className="text-sm text-ink-muted">
              The capacity of <code className="font-mono">{host.path}</code> could not be read —
              network mount, unrecognized file system, or missing permission.
            </p>
          </Panel>
        )}

        {remoteDatabase && (
          <Panel
            title={`${database.engine} database`}
            description="Server separate from the host: its capacity can't be inferred from the local disk."
            actions={
              database.capacityBytes > 0 ? undefined : <Badge tone="neutral">no gauge</Badge>
            }
          >
            {database.capacityBytes > 0 ? (
              <UsageDonut
                total={database.capacityBytes}
                caption="declared"
                slices={[
                  { label: 'Data', bytes: database.bytes, ...SLICES.database },
                  {
                    label: 'Free',
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
            title="S3 bucket"
            description="Objects stored off the host, thumbnails included."
            actions={bucket.capacityBytes > 0 ? undefined : <Badge tone="neutral">no gauge</Badge>}
          >
            {bucket.capacityBytes > 0 ? (
              <UsageDonut
                total={bucket.capacityBytes}
                caption="declared"
                slices={[
                  { label: 'Files', bytes: bucket.bytes, ...SLICES.files },
                  {
                    label: 'Free',
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

        <Panel title="Instance" description="What this process serves, and since when.">
          <Line label="Name" value={instance.appName} />
          <Line
            label="Public URL"
            value={instance.appUrl || '— not set'}
            mono={Boolean(instance.appUrl)}
          />
          <Line label="Version" value={instance.version} mono />
          <Line label="Runtime" value={instance.runtime} mono />
          <Line label="Started" value={formatDateTime(instance.startedAt)} />
          <Line label="Uptime" value={formatUptime(instance.uptimeSeconds)} />
          <Line label="API prefix" value={instance.apiPrefix} mono />
          <Line label="Data directory" value={instance.dataDirectory} mono />
        </Panel>

        <Panel
          title="Collection breakdown"
          description="System collections belong to the engine and cannot be deleted."
        >
          <ul className="space-y-2">
            {[
              { label: 'Data', value: instance.collections.data },
              { label: 'Accounts', value: instance.collections.auth },
              { label: 'Views', value: instance.collections.view },
              { label: 'System', value: instance.collections.system },
            ].map((row) => (
              <li key={row.label} className="flex items-center justify-between gap-3 text-sm">
                <span className="text-ink-muted">{row.label}</span>
                <span className="tabular-nums text-ink">{formatCount(row.value)}</span>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel
          title="Logging"
          description="Current state of the request log."
          actions={
            <Badge tone={instance.logs.enabled ? 'success' : 'warning'} dot>
              {instance.logs.enabled ? 'active' : 'suspended'}
            </Badge>
          }
        >
          <Line label="Entries kept" value={formatCount(instance.logs.total)} />
          <Line
            label="Retention"
            value={
              instance.logs.retentionDays === 0 ? 'unlimited' : `${instance.logs.retentionDays} days`
            }
          />
          <Line label="Entries dropped" value={formatCount(instance.logs.dropped)} />
        </Panel>

        <Panel
          title="Identity providers"
          description="Configured by the host: their secrets never pass through the console."
        >
          {instance.providers.length === 0 ? (
            <p className="text-sm text-ink-muted">No external provider is configured.</p>
          ) : (
            <ul className="space-y-2">
              {instance.providers.map((provider) => (
                <li key={provider.name} className="flex items-center justify-between gap-3 text-sm">
                  <span className="truncate text-ink">{provider.displayName}</span>
                  <Badge tone={provider.enabled ? 'success' : 'neutral'} dot>
                    {provider.enabled ? 'enabled' : 'inactive'}
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
