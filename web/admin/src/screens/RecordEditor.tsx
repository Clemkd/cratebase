import { useEffect, useState } from 'react'
import { KeyRound } from 'lucide-react'
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
 * Création et modification d'un enregistrement.
 *
 * Panneau latéral plutôt que page : la table reste visible derrière, donc on garde le contexte de
 * la ligne qu'on est en train de corriger.
 *
 * Le panneau est ordonné du plus au moins saisi : les champs du projet, puis le mot de passe pour
 * une collection de comptes, puis ce que le moteur a posé — replié, parce qu'on ne l'ouvre que pour
 * diagnostiquer.
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

  // Le panneau est monté en permanence pour que `<dialog>` conserve son nœud : les valeurs sont
  // donc réinitialisées à chaque ouverture, sinon on éditerait la ligne précédente.
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
      payload.passwordConfirm = passwordConfirm
    }

    try {
      if (recordId === null) await api.records.create(collection.name, payload)
      else await api.records.update(collection.name, recordId, payload)

      toast.success(recordId === null ? 'Enregistrement créé.' : 'Enregistrement modifié.')
      onSaved()
    } catch (error) {
      const fieldErrors = validationErrors(error)

      setErrors(fieldErrors)
      // Le détail générique n'est affiché en tête que si aucune erreur ne peut être posée sur un
      // champ : sinon il ferait doublon avec les messages déjà visibles.
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
        isCreation ? `Nouvel enregistrement — ${collection.name}` : 'Modifier un enregistrement'
      }
      description={
        recordId ? (
          // L'identifiant est ce qu'on vient chercher ici pour le reporter ailleurs — dans une
          // requête, un ticket, un filtre. Il se copie donc d'un clic.
          <span className="flex items-center gap-1">
            <code className="truncate font-mono">{recordId}</code>
            <CopyButton value={recordId} className="size-6" size="icon" />
          </span>
        ) : undefined
      }
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={saving}>
            Annuler
          </Button>
          <Button variant="primary" onClick={() => void save()} loading={saving}>
            {isCreation ? 'Créer' : 'Enregistrer'}
          </Button>
        </>
      }
    >
      <div className="space-y-5">
        {failure && <ErrorBlock message={failure} />}

        {fileFields.length > 0 && (
          <p className="rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken px-4 py-3 text-xs text-ink-muted">
            Champs fichier ({fileFields.map((field) => field.name).join(', ')}) : le téléversement
            passe par l'API en <code className="font-mono">multipart/form-data</code>, la console ne
            le propose pas encore. Les valeurs existantes sont conservées.
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
              {isCreation ? 'Mot de passe' : 'Changer le mot de passe'}
            </legend>

            {!isCreation && (
              <p className="text-xs text-ink-muted">
                Laisser vide pour ne pas y toucher. Un changement révoque les sessions ouvertes du
                compte.
              </p>
            )}

            <Field label="Mot de passe" required={isCreation} error={errors.password?.join(' ')}>
              <Input
                type="password"
                autoComplete="new-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            </Field>

            <Field label="Confirmation" error={errors.passwordConfirm?.join(' ')}>
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
            title="Champs posés par le moteur"
            description="En lecture seule : horodatages, identifiants, drapeaux internes."
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

/** Champs renseignés par le moteur : affichés pour le diagnostic, jamais saisissables. */
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
