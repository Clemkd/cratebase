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
 * Instance settings.
 *
 * Only the settings the engine actually honors are exposed here. The database engine, storage,
 * and provider secrets therefore don't appear here: they belong to the host configuration, and
 * displaying them as read-write would suggest a redeploy isn't necessary.
 */
export function AdminSettings({ onSaved }: { onSaved: (settings: AppSettings) => void }) {
  const toast = useToast()

  // Two copies: the server's and the one being edited. Comparing them is enough to know whether
  // there's something to save, without a "dirty" flag to maintain on every field.
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
  if (!draft || !saved) return <LoadingBlock label="Loading settings…" />

  const dirty = JSON.stringify(draft) !== JSON.stringify(saved)

  const submit = async () => {
    setSaving(true)
    setErrors({})

    try {
      // The payload is complete because the screen knows every setting it carries; the server,
      // for its part, accepts a partial one — that's what will let a future screen change a
      // single one without resetting the others.
      const result = await api.settings.update({
        appName: draft.appName,
        appUrl: draft.appUrl,
        logs: draft.logs,
      })

      setSaved(result)
      setDraft(result)
      onSaved(result)
      toast.success('Settings saved.')
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
          Cancel
        </Button>
        <Button
          variant="primary"
          icon={<Save size={15} aria-hidden="true" />}
          loading={saving}
          disabled={!dirty}
          onClick={() => void submit()}
        >
          Save
        </Button>
      </PageActions>

      <Panel title="Identity" description="What the console and the engine's messages display.">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field
            label="Instance name"
            error={first('appName')}
            hint="Displayed at the top of the navigation column."
            required
          >
            <Input
              value={draft.appName}
              maxLength={100}
              onChange={(event) => setDraft({ ...draft, appName: event.target.value })}
            />
          </Field>

          <Field
            label="Public URL"
            error={first('appUrl')}
            hint="Address at which clients reach the instance. Left empty, nothing guesses it."
          >
            <Input
              value={draft.appUrl}
              inputMode="url"
              spellCheck={false}
              placeholder="https://example.org"
              className="font-mono text-xs"
              onChange={(event) => setDraft({ ...draft, appUrl: event.target.value })}
            />
          </Field>
        </div>
      </Panel>

      <Panel
        title="Logs"
        description="What gets written, and for how long it's kept."
      >
        <div className="space-y-4">
          <Checkbox
            label="Log API requests"
            hint="Unchecked, no more requests are recorded — entries already written remain viewable."
            checked={draft.logs.enabled}
            onChange={(event) =>
              setDraft({ ...draft, logs: { ...draft.logs, enabled: event.target.checked } })
            }
          />

          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              label="Minimum severity"
              hint="Served requests are information; 4xx are warnings, 5xx are errors."
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
              label="Retention (days)"
              error={first('logs.retentionDays')}
              hint="Zero retains indefinitely. Beyond that, older entries are deleted every hour."
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
            label="Retain origin address"
            hint="An IP address is personal data: uncheck if the instance has no use for it."
            checked={draft.logs.logIp}
            disabled={!draft.logs.enabled}
            onChange={(event) =>
              setDraft({ ...draft, logs: { ...draft.logs, logIp: event.target.checked } })
            }
          />
        </div>
      </Panel>

      <Panel
        title="Realtime"
        description="Event stream pushed to subscribed clients, the console included."
      >
        <div className="space-y-4">
          <Checkbox
            label="Broadcast writes to subscribers"
            hint="Unchecked, open streams go quiet instead of being cut off, and no new one opens."
            checked={draft.realtime.enabled}
            onChange={(event) =>
              setDraft({
                ...draft,
                realtime: { ...draft.realtime, enabled: event.target.checked },
              })
            }
          />

          <Field
            label="Concurrent streams"
            error={first('realtime.maxClients')}
            hint="Each stream holds a connection for its whole duration: without a cap, a client that keeps reopening exhausts the server."
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
