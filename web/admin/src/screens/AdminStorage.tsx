import { useCallback, useEffect, useState } from 'react'
import {
  ArrowRightLeft,
  CheckCircle2,
  CircleAlert,
  Download,
  HardDrive,
  MinusCircle,
  PlugZap,
  RotateCw,
  Cloud,
} from 'lucide-react'
import { api, describeFailure, type Storage, type StorageProbe } from '../api'
import { formatBytes, formatCount } from '../lib/format'
import { PageActions, Stat, StatGrid } from '../layout/Page'
import {
  Badge,
  Button,
  CopyButton,
  ErrorBlock,
  LoadingBlock,
  Panel,
  Tooltip,
  cn,
  useToast,
} from '../ui'

/** Host variables that decide the store, in the order they're set. */
const S3_VARIABLES = [
  'Cratebase__S3__Bucket',
  'Cratebase__S3__Endpoint',
  'Cratebase__S3__AccessKey',
  'Cratebase__S3__SecretKey',
  'Cratebase__S3__Region',
  'Cratebase__S3__PublicEndpoint',
]

/** A line in the info card. */
function Line({
  label,
  value,
  mono = false,
  copy,
}: {
  label: string
  value: string
  mono?: boolean
  copy?: boolean
}) {
  return (
    <div className="flex items-start justify-between gap-4 border-b border-border-subtle py-2 last:border-0">
      <span className="shrink-0 text-xs text-ink-muted">{label}</span>
      <span className={cn('min-w-0 text-right text-xs break-all text-ink', mono && 'font-mono')}>
        {value || <span className="text-ink-faint">— not set</span>}
        {copy && value !== '' && (
          <CopyButton value={value} size="icon" className="ml-1 size-6 align-middle" />
        )}
      </span>
    </div>
  )
}

const STEP_ICONS = {
  ok: CheckCircle2,
  skipped: MinusCircle,
  failed: CircleAlert,
} as const

const STEP_INK = {
  ok: 'text-success',
  skipped: 'text-ink-faint',
  failed: 'text-danger',
} as const

/** Result of the connection test, step by step. */
function ProbeReport({ probe }: { probe: StorageProbe }) {
  return (
    <ul className="space-y-1.5">
      {probe.steps.map((step) => {
        const Icon = STEP_ICONS[step.state]

        return (
          <li key={step.name} className="flex items-start gap-2 text-xs">
            <Icon
              size={14}
              aria-hidden="true"
              className={cn('mt-0.5 shrink-0', STEP_INK[step.state])}
            />
            <span className="min-w-0 flex-1">
              <span className="text-ink">{step.name}</span>
              {step.detail !== '' && (
                <span className="ml-1.5 break-all text-ink-muted">— {step.detail}</span>
              )}
              {step.state === 'skipped' && step.detail === '' && (
                <span className="ml-1.5 text-ink-faint">— not applicable for this store</span>
              )}
            </span>
            <span className="shrink-0 tabular-nums text-ink-faint">
              {step.milliseconds.toFixed(0)} ms
            </span>
          </li>
        )
      })}
    </ul>
  )
}

/**
 * File storage.
 *
 * The screen <b>shows</b> and <b>exercises</b> the configuration; it doesn't write it. The store
 * is decided by the host configuration, just like the database engine: anything carrying a
 * secret never enters the database the console can read, nor that database's backups. The
 * tradeoff — you don't switch to S3 from a form — is deliberate, and offset by what's actually
 * needed on switchover day: the exact variables to set, and a test that says whether they're right.
 */
export function AdminStorage() {
  const toast = useToast()

  const [storage, setStorage] = useState<Storage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [probe, setProbe] = useState<StorageProbe | null>(null)
  const [probing, setProbing] = useState(false)

  const reload = useCallback(async () => {
    setLoading(true)

    try {
      setStorage(await api.storage.get())
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

  const check = async () => {
    setProbing(true)

    try {
      const result = await api.storage.check()

      setProbe(result)

      if (result.ok) toast.success('The store responds: write, read-back, and delete verified.')
      else toast.error('The store isn\'t responding as expected. See the step details.')
    } catch (failure) {
      toast.error(describeFailure(failure))
    } finally {
      setProbing(false)
    }
  }

  const download = async (thumbs: boolean) => {
    try {
      globalThis.location.href = await api.storage.archive({ thumbs })
    } catch (failure) {
      toast.error(describeFailure(failure))
    }
  }

  if (error) return <ErrorBlock message={error} onRetry={() => void reload()} />
  if (!storage) return <LoadingBlock label="Loading store…" />

  const isS3 = storage.kind === 's3'
  const total = storage.objects.fileBytes + storage.objects.thumbBytes

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Reload">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Reload store"
            loading={loading}
            onClick={() => void reload()}
          >
            <RotateCw size={16} aria-hidden="true" />
          </Button>
        </Tooltip>

        <Button
          variant="outline"
          icon={<PlugZap size={15} aria-hidden="true" />}
          loading={probing}
          onClick={() => void check()}
        >
          Test connection
        </Button>
      </PageActions>

      <StatGrid>
        <Stat
          label="Active store"
          value={isS3 ? 'S3' : 'Local disk'}
          hint={isS3 ? storage.bucket : storage.directory}
          icon={isS3 ? <Cloud size={16} aria-hidden="true" /> : <HardDrive size={16} aria-hidden="true" />}
          tone="brand"
        />
        <Stat
          label="Files"
          value={formatCount(storage.objects.files)}
          hint={formatBytes(storage.objects.fileBytes)}
        />
        <Stat
          label="Thumbnails"
          value={formatCount(storage.objects.thumbs)}
          hint={`${formatBytes(storage.objects.thumbBytes)} — regenerable`}
        />
      </StatGrid>

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel
          title="Active configuration"
          description="Set by the host at startup. The console reads it; it never writes it."
          actions={<Badge tone={isS3 ? 'brand' : 'neutral'}>{storage.name}</Badge>}
        >
          {isS3 ? (
            <>
              <Line label="Bucket" value={storage.bucket} mono copy />
              <Line label="Endpoint" value={storage.endpoint} mono copy />
              <Line
                label="Public endpoint"
                value={storage.publicEndpoint}
                mono
                copy
              />
              <Line label="Region" value={storage.region} mono />
              <Line label="Path style" value={storage.forcePathStyle ? 'yes' : 'no'} />
              <Line label="Access key" value={storage.accessKeyHint} mono />
              <Line label="Secret key" value={storage.hasSecretKey ? 'set' : 'absent'} />
            </>
          ) : (
            <>
              <Line label="Directory" value={storage.directory} mono copy />
              <Line label="Signed URLs" value="no — the API serves the bytes itself" />
            </>
          )}
        </Panel>

        <Panel
          title="Switch to S3"
          description="A configuration switch: neither the code, the collections, nor the clients change."
        >
          <p className="mb-2 text-xs text-ink-muted">
            Set these environment variables on the host, then restart. The bucket alone is
            enough to trigger the switch; without it, the local disk stays in service.
          </p>

          <ul className="space-y-1">
            {S3_VARIABLES.map((name) => (
              <li key={name} className="flex items-center justify-between gap-2">
                <code className="truncate font-mono text-xs text-ink">{name}</code>
                <CopyButton value={name} size="icon" className="size-6 shrink-0" />
              </li>
            ))}
          </ul>

          <p className="mt-3 rounded-[var(--radius-control)] border border-warning/40 bg-warning/10 px-3 py-2 text-xs text-ink">
            <strong>A pitfall worth knowing.</strong> A presigned URL is signed for a given host.
            If the API signs for <code className="font-mono">http://minio:9000</code> and the
            browser calls a different name, every link is rejected — while the API itself stays
            perfectly healthy. That's exactly what "Test connection" checks by actually following
            a signed URL.
          </p>
        </Panel>

        <Panel
          title="Connection test"
          description="Write, read-back, describe, follow signed URL, delete."
          actions={
            probe ? (
              <Badge tone={probe.ok ? 'success' : 'danger'} dot>
                {probe.ok ? 'passing' : 'failing'}
              </Badge>
            ) : undefined
          }
        >
          {probe ? (
            <ProbeReport probe={probe} />
          ) : (
            <p className="text-sm text-ink-muted">
              No test run yet. It writes a probe object, reads it back, describes it, follows its
              signed URL when the store produces one, then deletes it.
            </p>
          )}
        </Panel>

        <Panel
          title="Export"
          description="The store's files, as a single archive."
        >
          <p className="mb-3 text-xs text-ink-muted">
            {formatCount(storage.objects.files)} files, {formatBytes(total)} total including
            thumbnails. Thumbnails are excluded by default: they regenerate on demand, so
            archiving them amounts to archiving a cache.
          </p>

          <div className="flex flex-wrap gap-2">
            <Button
              variant="outline"
              icon={<Download size={15} aria-hidden="true" />}
              onClick={() => void download(false)}
            >
              Files only
            </Button>
            <Button
              variant="ghost"
              icon={<Download size={15} aria-hidden="true" />}
              onClick={() => void download(true)}
            >
              Include thumbnails
            </Button>
          </div>

          <p className="mt-3 text-xs text-ink-muted">
            Exporting <em>data</em> — collection definitions, records, settings — is described in{' '}
            <code className="font-mono">docs/BACKUP.md</code> and isn't implemented yet.
          </p>
        </Panel>

        <Panel
          title="Migration"
          description="Move an already-populated store to another one."
          actions={<Badge tone="neutral">coming soon</Badge>}
        >
          <div className="flex items-start gap-2.5">
            <ArrowRightLeft size={16} aria-hidden="true" className="mt-0.5 shrink-0 text-ink-faint" />
            <p className="text-xs text-ink-muted">
              Setting the S3 configuration switches over <em>new</em> files; files already
              written stay where they are. Moving them — enumeration from the records rather than
              from the store, streamed copy, checksum verification, checking the public URL on
              arrival — is the subject of a separate plan,{' '}
              <code className="font-mono">docs/MIGRATION.md</code>. Nothing here triggers it for
              now, and no button suggests otherwise.
            </p>
          </div>
        </Panel>
      </div>
    </div>
  )
}
