import { useCallback, useEffect, useState } from 'react'
import { RotateCcw, Save } from 'lucide-react'
import {
  api,
  describeFailure,
  validationErrors,
  LOG_LEVELS,
  type AppSettings,
  type LogLevel,
} from '../api'
import { LEVEL_INK, LEVEL_META } from '../lib/logs'
import { PageActions } from '../layout/Page'
import {
  Button,
  Checkbox,
  ErrorBlock,
  Field,
  Input,
  LoadingBlock,
  Panel,
  Select,
  useToast,
} from '../ui'

/**
 * Paramètres de l'instance.
 *
 * Ne sont exposés ici que les réglages que le moteur honore réellement. Le moteur de base, le
 * stockage et les secrets des fournisseurs n'y figurent donc pas : ils appartiennent à la
 * configuration d'hôte, et les afficher en lecture-écriture ferait croire qu'un redéploiement n'est
 * pas nécessaire.
 */
export function AdminSettings({ onSaved }: { onSaved: (settings: AppSettings) => void }) {
  const toast = useToast()

  // Deux copies : celle du serveur et celle en cours de saisie. Leur comparaison suffit à savoir
  // s'il y a quelque chose à enregistrer, sans drapeau « modifié » à maintenir à chaque champ.
  const [saved, setSaved] = useState<AppSettings | null>(null)
  const [draft, setDraft] = useState<AppSettings | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string[]>>({})

  const load = useCallback(async () => {
    try {
      const settings = await api.settings.get()

      setSaved(settings)
      setDraft(settings)
      setError(null)
    } catch (failure) {
      setError(describeFailure(failure))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />
  if (!draft || !saved) return <LoadingBlock label="Lecture des réglages…" />

  const dirty = JSON.stringify(draft) !== JSON.stringify(saved)

  const submit = async () => {
    setSaving(true)
    setErrors({})

    try {
      // La charge est complète parce que l'écran connaît tous les réglages qu'elle porte ; le
      // serveur, lui, accepte le partiel — c'est ce qui permettra à un futur écran d'en modifier un
      // seul sans réinitialiser les autres.
      const result = await api.settings.update({
        appName: draft.appName,
        appUrl: draft.appUrl,
        logs: draft.logs,
      })

      setSaved(result)
      setDraft(result)
      onSaved(result)
      toast.success('Réglages enregistrés.')
    } catch (failure) {
      setErrors(validationErrors(failure))
      toast.error(describeFailure(failure))
    } finally {
      setSaving(false)
    }
  }

  const first = (key: string) => errors[key]?.[0]

  return (
    <div className="space-y-4">
      <PageActions>
        <Button
          variant="outline"
          icon={<RotateCcw size={15} aria-hidden="true" />}
          disabled={!dirty || saving}
          onClick={() => {
            setDraft(saved)
            setErrors({})
          }}
        >
          Annuler
        </Button>
        <Button
          variant="primary"
          icon={<Save size={15} aria-hidden="true" />}
          loading={saving}
          disabled={!dirty}
          onClick={() => void submit()}
        >
          Enregistrer
        </Button>
      </PageActions>

      <Panel title="Identité" description="Ce que la console et les messages du moteur affichent.">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field
            label="Nom de l'instance"
            error={first('appName')}
            hint="Affiché en tête de la colonne de navigation."
            required
          >
            <Input
              value={draft.appName}
              maxLength={100}
              onChange={(event) => setDraft({ ...draft, appName: event.target.value })}
            />
          </Field>

          <Field
            label="URL publique"
            error={first('appUrl')}
            hint="Adresse par laquelle les clients atteignent l'instance. Laissée vide, rien ne la devine."
          >
            <Input
              value={draft.appUrl}
              inputMode="url"
              spellCheck={false}
              placeholder="https://exemple.org"
              className="font-mono text-xs"
              onChange={(event) => setDraft({ ...draft, appUrl: event.target.value })}
            />
          </Field>
        </div>
      </Panel>

      <Panel
        title="Journal"
        description="Ce qui est écrit, et pendant combien de temps c'est conservé."
      >
        <div className="space-y-4">
          <Checkbox
            label="Journaliser les requêtes de l'API"
            hint="Décoché, plus aucune requête n'est enregistrée — les entrées déjà écrites restent consultables."
            checked={draft.logs.enabled}
            onChange={(event) =>
              setDraft({ ...draft, logs: { ...draft.logs, enabled: event.target.checked } })
            }
          />

          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              label="Gravité minimale"
              hint="Les requêtes servies sont des informations ; les 4xx des avertissements, les 5xx des erreurs."
            >
              <Select<LogLevel>
                value={draft.logs.minLevel}
                disabled={!draft.logs.enabled}
                options={LOG_LEVELS.map((level) => ({
                  value: level,
                  label: LEVEL_META[level].label,
                  text: LEVEL_META[level].label,
                  icon: (() => {
                    const Icon = LEVEL_META[level].icon

                    return <Icon size={13} aria-hidden="true" className={LEVEL_INK[level]} />
                  })(),
                }))}
                onChange={(minLevel) => setDraft({ ...draft, logs: { ...draft.logs, minLevel } })}
              />
            </Field>

            <Field
              label="Rétention (jours)"
              error={first('logs.retentionDays')}
              hint="Zéro conserve sans limite. Au-delà, les entrées plus anciennes sont supprimées chaque heure."
            >
              <Input
                type="number"
                min={0}
                max={365}
                value={draft.logs.retentionDays}
                onChange={(event) =>
                  setDraft({
                    ...draft,
                    logs: { ...draft.logs, retentionDays: Number(event.target.value) },
                  })
                }
              />
            </Field>
          </div>

          <Checkbox
            label="Conserver l'adresse d'origine"
            hint="Une adresse IP est une donnée personnelle : à décocher si l'instance n'en a pas l'usage."
            checked={draft.logs.logIp}
            disabled={!draft.logs.enabled}
            onChange={(event) =>
              setDraft({ ...draft, logs: { ...draft.logs, logIp: event.target.checked } })
            }
          />
        </div>
      </Panel>

      <Panel
        title="Temps réel"
        description="Flux d'évènements poussés aux clients abonnés, la console comprise."
      >
        <div className="space-y-4">
          <Checkbox
            label="Diffuser les écritures aux abonnés"
            hint="Décoché, les flux ouverts se taisent au lieu d'être coupés, et aucun nouveau ne s'ouvre."
            checked={draft.realtime.enabled}
            onChange={(event) =>
              setDraft({
                ...draft,
                realtime: { ...draft.realtime, enabled: event.target.checked },
              })
            }
          />

          <Field
            label="Flux simultanés"
            error={first('realtime.maxClients')}
            hint="Chaque flux retient une connexion pour toute sa durée : sans plafond, un client qui rouvre en boucle épuise le serveur."
          >
            <Input
              type="number"
              min={1}
              max={10000}
              value={draft.realtime.maxClients}
              disabled={!draft.realtime.enabled}
              onChange={(event) =>
                setDraft({
                  ...draft,
                  realtime: { ...draft.realtime, maxClients: Number(event.target.value) },
                })
              }
            />
          </Field>
        </div>
      </Panel>
    </div>
  )
}
