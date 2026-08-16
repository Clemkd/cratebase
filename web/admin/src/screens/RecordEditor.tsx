import { useEffect, useState } from 'react'
import { KeyRound, Plus, Save, X } from 'lucide-react'
import {
  api,
  describeFailure,
  validationErrors,
  type Collection,
  type Field as FieldDefinition,
  type RecordValue,
} from '../api'
import { initialFormValues, isEditable, toPayloadValue, typeLabel } from '../lib/fields'
import { formatDateTime } from '../lib/format'
import {
  Badge,
  Button,
  CopyButton,
  Dialog,
  Disclosure,
  ErrorBlock,
  Field,
  Input,
  useToast,
} from '../ui'
import { FieldControl } from './FieldControl'

/**
 * Creating and editing a record.
 *
 * A side panel rather than a page: the table stays visible behind it, so the context of the row
 * being fixed is preserved.
 *
 * The panel is ordered from most to least frequently edited: the project's own fields, then the
 * password for an accounts collection, then what the engine has set — collapsed, since it's only
 * opened to diagnose something.
 */
export function RecordEditor({
  collection,
  collections,
  record,
  open,
  onClose,
  onSaved,
}: {
  collection: Collection
  collections: Collection[]
  record: RecordValue | null
  open: boolean
  onClose: () => void
  onSaved: () => void
}) {
  const isCreation = record === null
  const recordId = record === null ? null : String(record.id ?? '')
  const toast = useToast()

  const [values, setValues] = useState<RecordValue>(() => initialFormValues(collection, record))
  const [password, setPassword] = useState('')
  const [passwordConfirm, setPasswordConfirm] = useState('')
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [failure, setFailure] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [engineOpen, setEngineOpen] = useState(false)

  // The panel stays permanently mounted so `<dialog>` keeps its node: values are therefore reset
  // on every open, otherwise the previous row would still be the one being edited.
  useEffect(() => {
    if (!open) return

    setValues(initialFormValues(collection, record))
    setPassword('')
    setPasswordConfirm('')
    setErrors({})
    setFailure(null)
    setEngineOpen(false)
  }, [open, collection, record])

  const isAuth = collection.kind === 'Auth'
  const editable = collection.fields.filter(isEditable)
  const fileFields = collection.fields.filter((field) => field.type === 'File')
  const readOnly = collection.fields.filter(
    (field) => !field.hidden && !isEditable(field) && field.type !== 'File',
  )

  const save = async () => {
    setSaving(true)
    setErrors({})
    setFailure(null)

    const payload: RecordValue = {}

    for (const field of editable) {
      payload[field.name] = toPayloadValue(field, values[field.name])
    }

    if (isAuth && password !== '') {
      payload.password = password
      payload.password_confirm = passwordConfirm
    }

    try {
      if (recordId === null) await api.records.create(collection.name, payload)
      else await api.records.update(collection.name, recordId, payload)

      toast.success(recordId === null ? 'Record created.' : 'Record updated.')
      onSaved()
    } catch (error) {
      const fieldErrors = validationErrors(error)

      setErrors(fieldErrors)
      // The generic detail is only shown at the top if no error can be attached to a field:
      // otherwise it would duplicate the messages already visible.
      setFailure(Object.keys(fieldErrors).length > 0 ? null : describeFailure(error))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      side="right"
      title={
        isCreation ? `New record — ${collection.name}` : 'Edit record'
      }
      description={
        recordId ? (
          // The identifier is what you come here to fetch in order to reuse it elsewhere — in a
          // request, a ticket, a filter. It's therefore copyable in one click.
          <span className="flex items-center gap-1">
            <code className="truncate font-mono">{recordId}</code>
            <CopyButton value={recordId} className="size-6" size="icon" />
          </span>
        ) : undefined
      }
      footer={
        <>
          <Button
            variant="outline"
            icon={<X size={15} aria-hidden="true" />}
            onClick={onClose}
            disabled={saving}
          >
            Cancel
          </Button>
          <Button
            variant="primary"
            icon={
              isCreation ? (
                <Plus size={15} aria-hidden="true" />
              ) : (
                <Save size={15} aria-hidden="true" />
              )
            }
            onClick={() => void save()}
            loading={saving}
          >
            {isCreation ? 'Create' : 'Save'}
          </Button>
        </>
      }
    >
      <div className="space-y-5">
        {failure && <ErrorBlock message={failure} />}

        {fileFields.length > 0 && (
          <p className="rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken px-4 py-3 text-xs text-ink-muted">
            File fields ({fileFields.map((field) => field.name).join(', ')}): uploading goes
            through the API via <code className="font-mono">multipart/form-data</code>, which the
            console doesn't yet offer. Existing values are preserved.
          </p>
        )}

        {editable.map((field) => (
          <Field
            key={field.id}
            label={
              <span className="flex items-center gap-2">
                <span className="font-mono text-xs text-ink">{field.name}</span>
                <Badge>{typeLabel(field.type)}</Badge>
                {field.multiple && <Badge>multiple</Badge>}
              </span>
            }
            required={field.required}
            error={errors[field.name]?.join(' ')}
          >
            <FieldControl
              field={field}
              value={values[field.name]}
              collections={collections}
              invalid={Boolean(errors[field.name])}
              onChange={(value) => setValues((current) => ({ ...current, [field.name]: value }))}
            />
          </Field>
        ))}

        {isAuth && (
          <fieldset className="space-y-4 rounded-[var(--radius-card)] border border-border-subtle p-4">
            <legend className="flex items-center gap-1.5 px-1 text-xs font-semibold text-ink">
              <KeyRound size={13} aria-hidden="true" />
              {isCreation ? 'Password' : 'Change password'}
            </legend>

            {!isCreation && (
              <p className="text-xs text-ink-muted">
                Leave blank to leave it unchanged. A change revokes the account's open sessions.
              </p>
            )}

            <Field label="Password" required={isCreation} error={errors.password?.join(' ')}>
              <Input
                type="password"
                autoComplete="new-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            </Field>

            <Field label="Confirmation" error={errors.password_confirm?.join(' ')}>
              <Input
                type="password"
                autoComplete="new-password"
                value={passwordConfirm}
                onChange={(event) => setPasswordConfirm(event.target.value)}
              />
            </Field>
          </fieldset>
        )}

        {record !== null && readOnly.length > 0 && (
          <Disclosure
            title="Fields set by the engine"
            description="Read-only: timestamps, identifiers, internal flags."
            open={engineOpen}
            onToggle={() => setEngineOpen((current) => !current)}
          >
            <EngineFields fields={readOnly} record={record} />
          </Disclosure>
        )}
      </div>
    </Dialog>
  )
}

/** Fields set by the engine: shown for diagnostics, never editable. */
function EngineFields({ fields, record }: { fields: FieldDefinition[]; record: RecordValue }) {
  return (
    <dl className="divide-y divide-border-subtle">
      {fields.map((field) => {
        const raw = record[field.name]

        return (
          <div key={field.id} className="flex gap-3 px-5 py-2 text-xs">
            <dt className="w-32 shrink-0 font-mono text-ink-muted">{field.name}</dt>
            <dd className="min-w-0 truncate font-mono text-ink">
              {field.type === 'AutoDate' || field.type === 'Date'
                ? formatDateTime(raw)
                : Array.isArray(raw)
                  ? raw.join(', ') || '—'
                  : String(raw ?? '—')}
            </dd>
          </div>
        )
      })}
    </dl>
  )
}
