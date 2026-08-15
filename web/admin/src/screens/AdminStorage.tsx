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

/** Variables d'hôte qui décident du magasin, dans l'ordre où on les pose. */
const S3_VARIABLES = [
  'Cratebase__S3__Bucket',
  'Cratebase__S3__Endpoint',
  'Cratebase__S3__AccessKey',
  'Cratebase__S3__SecretKey',
  'Cratebase__S3__Region',
  'Cratebase__S3__PublicEndpoint',
]

/** Une ligne de la fiche. */
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
        {value || <span className="text-ink-faint">— non renseigné</span>}
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

/** Résultat du test de connexion, étape par étape. */
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
                <span className="ml-1.5 text-ink-faint">— sans objet pour ce magasin</span>
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
 * Stockage des fichiers.
 *
 * L'écran <b>montre</b> et <b>éprouve</b> la configuration ; il ne l'écrit pas. Le magasin est
 * décidé par la configuration de l'hôte, comme le moteur de base : ce qui porte un secret n'entre
 * jamais dans la base que la console peut lire, ni dans les sauvegardes de cette base. La contrepartie
 * — on ne bascule pas vers S3 depuis un formulaire — est assumée, et compensée par ce dont on a
 * réellement besoin le jour de la bascule : les variables exactes à poser, et un test qui dit si
 * elles sont bonnes.
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

      if (result.ok) toast.success('Le magasin répond : écriture, relecture et suppression vérifiées.')
      else toast.error('Le magasin ne répond pas comme attendu. Voir le détail des étapes.')
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
  if (!storage) return <LoadingBlock label="Lecture du magasin…" />

  const isS3 = storage.kind === 's3'
  const total = storage.objects.fileBytes + storage.objects.thumbBytes

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Recharger">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Recharger le magasin"
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
          Tester la connexion
        </Button>
      </PageActions>

      <StatGrid>
        <Stat
          label="Magasin actif"
          value={isS3 ? 'S3' : 'Disque local'}
          hint={isS3 ? storage.bucket : storage.directory}
          icon={isS3 ? <Cloud size={16} aria-hidden="true" /> : <HardDrive size={16} aria-hidden="true" />}
          tone="brand"
        />
        <Stat
          label="Fichiers"
          value={formatCount(storage.objects.files)}
          hint={formatBytes(storage.objects.fileBytes)}
        />
        <Stat
          label="Vignettes"
          value={formatCount(storage.objects.thumbs)}
          hint={`${formatBytes(storage.objects.thumbBytes)} — régénérables`}
        />
      </StatGrid>

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel
          title="Configuration en vigueur"
          description="Posée par l'hôte au démarrage. La console la lit ; elle ne l'écrit jamais."
          actions={<Badge tone={isS3 ? 'brand' : 'neutral'}>{storage.name}</Badge>}
        >
          {isS3 ? (
            <>
              <Line label="Seau" value={storage.bucket} mono copy />
              <Line label="Point de terminaison" value={storage.endpoint} mono copy />
              <Line
                label="Point de terminaison public"
                value={storage.publicEndpoint}
                mono
                copy
              />
              <Line label="Région" value={storage.region} mono />
              <Line label="Style de chemin" value={storage.forcePathStyle ? 'oui' : 'non'} />
              <Line label="Clé d'accès" value={storage.accessKeyHint} mono />
              <Line label="Clé secrète" value={storage.hasSecretKey ? 'renseignée' : 'absente'} />
            </>
          ) : (
            <>
              <Line label="Répertoire" value={storage.directory} mono copy />
              <Line label="URL signées" value="non — l'API sert les octets elle-même" />
            </>
          )}
        </Panel>

        <Panel
          title="Basculer vers S3"
          description="Une bascule de configuration : ni le code, ni les collections, ni les clients ne changent."
        >
          <p className="mb-2 text-xs text-ink-muted">
            Posez ces variables d'environnement sur l'hôte, puis redémarrez. Le seau suffit à
            déclencher la bascule ; sans lui, le disque local reste en service.
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
            <strong>Piège à connaître.</strong> Une URL présignée est signée pour un hôte donné. Si
            l'API signe pour <code className="font-mono">http://minio:9000</code> et que le
            navigateur appelle un autre nom, tous les liens sont rejetés — l'API restant, elle,
            parfaitement saine. C'est ce que « Tester la connexion » vérifie en suivant réellement
            une URL signée.
          </p>
        </Panel>

        <Panel
          title="Test de connexion"
          description="Écriture, relecture, description, URL signée suivie, suppression."
          actions={
            probe ? (
              <Badge tone={probe.ok ? 'success' : 'danger'} dot>
                {probe.ok ? 'conforme' : 'en échec'}
              </Badge>
            ) : undefined
          }
        >
          {probe ? (
            <ProbeReport probe={probe} />
          ) : (
            <p className="text-sm text-ink-muted">
              Aucun test lancé. Il écrit un objet témoin, le relit, le décrit, suit son URL signée
              quand le magasin en produit, puis le supprime.
            </p>
          )}
        </Panel>

        <Panel
          title="Extraction"
          description="Les fichiers du magasin, en une archive."
        >
          <p className="mb-3 text-xs text-ink-muted">
            {formatCount(storage.objects.files)} fichiers, {formatBytes(total)} au total vignettes
            comprises. Les vignettes sont exclues par défaut : elles se régénèrent à la demande, donc
            les archiver revient à archiver un cache.
          </p>

          <div className="flex flex-wrap gap-2">
            <Button
              variant="outline"
              icon={<Download size={15} aria-hidden="true" />}
              onClick={() => void download(false)}
            >
              Fichiers seuls
            </Button>
            <Button
              variant="ghost"
              icon={<Download size={15} aria-hidden="true" />}
              onClick={() => void download(true)}
            >
              Vignettes comprises
            </Button>
          </div>

          <p className="mt-3 text-xs text-ink-muted">
            L'export des <em>données</em> — définitions de collections, enregistrements, réglages —
            est décrit dans <code className="font-mono">docs/SAUVEGARDE.md</code> et n'est pas encore
            implémenté.
          </p>
        </Panel>

        <Panel
          title="Migration"
          description="Déplacer un magasin déjà peuplé vers un autre."
          actions={<Badge tone="neutral">à venir</Badge>}
        >
          <div className="flex items-start gap-2.5">
            <ArrowRightLeft size={16} aria-hidden="true" className="mt-0.5 shrink-0 text-ink-faint" />
            <p className="text-xs text-ink-muted">
              Poser la configuration S3 fait basculer les <em>nouveaux</em> fichiers ; les fichiers
              déjà écrits restent où ils sont. Leur déplacement — énumération depuis les
              enregistrements et non depuis le magasin, copie en flux, vérification des empreintes,
              contrôle de l'URL publique à l'arrivée — fait l'objet d'un plan à part,{' '}
              <code className="font-mono">docs/MIGRATION.md</code>. Rien ici ne le déclenche pour
              l'instant, et aucun bouton ne le laisse croire.
            </p>
          </div>
        </Panel>
      </div>
    </div>
  )
}
