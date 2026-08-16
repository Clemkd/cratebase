import { useEffect, useState, type KeyboardEvent } from 'react'
import { X } from 'lucide-react'
import { api, type Collection, type Field, type RecordValue } from '../api'
import { asGeoPoint, asStringList, labelField } from '../lib/fields'
import { fromLocalInputValue, toLocalInputValue } from '../lib/format'
import { Checkbox, Input, Select, Textarea, cn, controlClasses } from '../ui'

/** Input for a list of free-form strings, as chips. */
export function StringListInput({
  value,
  onChange,
  placeholder,
  disabled,
  invalid,
}: {
  value: string[]
  onChange: (value: string[]) => void
  placeholder?: string
  disabled?: boolean
  invalid?: boolean
}) {
  const [draft, setDraft] = useState('')

  const commit = () => {
    const entry = draft.trim()

    if (entry === '' || value.includes(entry)) {
      setDraft('')
      return
    }

    onChange([...value, entry])
    setDraft('')
  }

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter' || event.key === ',') {
      event.preventDefault()
      commit()
      return
    }

    if (event.key === 'Backspace' && draft === '' && value.length > 0) {
      onChange(value.slice(0, -1))
    }
  }

  return (
    <div
      aria-invalid={invalid}
      className={cn(controlClasses, 'flex min-h-10 flex-wrap items-center gap-1.5 py-1.5')}
    >
      {value.map((entry) => (
        <span
          key={entry}
          className="inline-flex items-center gap-1 rounded-full bg-surface-sunken py-0.5 pr-0.5 pl-2 text-xs"
        >
          {entry}
          <button
            type="button"
            disabled={disabled}
            aria-label={`Remove "${entry}"`}
            onClick={() => onChange(value.filter((item) => item !== entry))}
            className="rounded-full p-0.5 text-ink-faint hover:text-danger"
          >
            <X size={11} aria-hidden="true" />
          </button>
        </span>
      ))}

      <input
        value={draft}
        disabled={disabled}
        placeholder={value.length === 0 ? (placeholder ?? 'Type then Enter') : undefined}
        onChange={(event) => setDraft(event.target.value)}
        onKeyDown={onKeyDown}
        onBlur={commit}
        className="min-w-24 flex-1 bg-transparent text-sm outline-none placeholder:text-ink-faint"
      />
    </div>
  )
}

function SelectList({
  values,
  selected,
  onChange,
  maxSelect,
}: {
  values: string[]
  selected: string[]
  onChange: (value: string[]) => void
  maxSelect: number
}) {
  return (
    <div className="space-y-2 rounded-[var(--radius-control)] border border-border-subtle bg-surface p-3">
      {values.length === 0 && (
        <p className="text-xs text-ink-muted">No value declared on this field.</p>
      )}

      {values.map((option) => {
        const checked = selected.includes(option)

        return (
          <Checkbox
            key={option}
            label={option}
            checked={checked}
            disabled={!checked && selected.length >= maxSelect}
            onChange={(event) =>
              onChange(
                event.target.checked
                  ? [...selected, option]
                  : selected.filter((item) => item !== option),
              )
            }
          />
        )
      })}

      <p className="pt-1 text-xs text-ink-muted">
        {selected.length} / {maxSelect} value{maxSelect > 1 ? 's' : ''}
      </p>
    </div>
  )
}

function RelationControl({
  field,
  value,
  onChange,
  collections,
}: {
  field: Field
  value: unknown
  onChange: (value: unknown) => void
  collections: Collection[]
}) {
  const target = field.options.targetCollection ?? ''
  const targetCollection = collections.find((item) => item.name === target)
  const [options, setOptions] = useState<RecordValue[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    if (target === '') return

    let abandoned = false

    api.records
      .list(target, { perPage: 200, skipTotal: true })
      .then((page) => {
        if (!abandoned) setOptions(page.items)
      })
      .catch(() => {
        // The target collection may be inaccessible: free-form input remains possible, rather
        // than blocking editing over a permission that doesn't concern this field.
        if (!abandoned) setFailed(true)
      })

    return () => {
      abandoned = true
    }
  }, [target])

  const label = targetCollection ? labelField(targetCollection) : undefined

  const describe = (record: RecordValue): string => {
    const id = String(record.id ?? '')
    const text = label ? String(record[label.name] ?? '') : ''

    return text === '' ? id : `${text} — ${id.slice(0, 8)}`
  }

  if (target === '') {
    return <p className="text-xs text-ink-muted">No target collection declared on this field.</p>
  }

  if (failed || options === null) {
    const list = asStringList(value)

    return field.multiple ? (
      <StringListInput
        value={list}
        onChange={onChange}
        placeholder="Record IDs"
      />
    ) : (
      <Input
        value={list[0] ?? ''}
        placeholder="Record ID"
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }

  if (field.multiple) {
    const selected = asStringList(value)

    return (
      <div className="max-h-56 space-y-2 overflow-y-auto rounded-[var(--radius-control)] border border-border-subtle bg-surface p-3">
        {options.map((record) => {
          const id = String(record.id ?? '')
          const checked = selected.includes(id)

          return (
            <Checkbox
              key={id}
              label={describe(record)}
              checked={checked}
              disabled={!checked && selected.length >= field.maxSelect}
              onChange={(event) =>
                onChange(
                  event.target.checked ? [...selected, id] : selected.filter((item) => item !== id),
                )
              }
            />
          )
        })}
      </div>
    )
  }

  return (
    <Select<string>
      value={String(value ?? '')}
      placeholder="— none —"
      options={[
        { value: '', label: '— none —' },
        ...options.map((record) => ({
          value: String(record.id),
          label: describe(record),
        })),
      ]}
      onChange={onChange}
    />
  )
}

function JsonControl({ value, onChange }: { value: unknown; onChange: (value: unknown) => void }) {
  const [text, setText] = useState(() =>
    value === null || value === undefined ? '' : JSON.stringify(value, null, 2),
  )
  const [invalid, setInvalid] = useState(false)

  return (
    <div className="space-y-1">
      <Textarea
        value={text}
        rows={5}
        spellCheck={false}
        aria-invalid={invalid}
        className="font-mono text-xs"
        placeholder="null"
        onChange={(event) => {
          const next = event.target.value
          setText(next)

          if (next.trim() === '') {
            setInvalid(false)
            onChange(null)
            return
          }

          try {
            onChange(JSON.parse(next))
            setInvalid(false)
          } catch {
            // The value passed up stays the last valid form: the screen flags the error, but
            // doesn't overwrite the field with a string the server would reject.
            setInvalid(true)
          }
        }}
      />

      {invalid && (
        <p role="alert" className="text-xs font-medium text-danger">
          Invalid JSON — the last valid value will be saved.
        </p>
      )}
    </div>
  )
}

function GeoPointControl({
  value,
  onChange,
}: {
  value: unknown
  onChange: (value: unknown) => void
}) {
  const point = asGeoPoint(value)

  return (
    <div className="grid grid-cols-2 gap-2">
      <Input
        type="number"
        step="any"
        aria-label="Longitude"
        placeholder="Longitude"
        value={point.longitude}
        onChange={(event) => onChange({ ...point, longitude: Number(event.target.value) })}
      />
      <Input
        type="number"
        step="any"
        aria-label="Latitude"
        placeholder="Latitude"
        value={point.latitude}
        onChange={(event) => onChange({ ...point, latitude: Number(event.target.value) })}
      />
    </div>
  )
}

/** Input control adapted to the field's logical type. */
export function FieldControl({
  field,
  value,
  onChange,
  collections,
  invalid,
}: {
  field: Field
  value: unknown
  onChange: (value: unknown) => void
  collections: Collection[]
  invalid?: boolean
}) {
  switch (field.type) {
    case 'Bool':
      return (
        <Checkbox
          label={value === true ? 'Yes' : 'No'}
          checked={value === true}
          onChange={(event) => onChange(event.target.checked)}
        />
      )

    case 'Number':
      return (
        <Input
          type="number"
          step={field.options.integerOnly ? '1' : 'any'}
          min={field.options.min ?? undefined}
          max={field.options.max ?? undefined}
          aria-invalid={invalid}
          value={typeof value === 'number' || typeof value === 'string' ? String(value) : ''}
          onChange={(event) => onChange(event.target.value)}
        />
      )

    case 'Editor':
      return (
        <Textarea
          rows={6}
          aria-invalid={invalid}
          value={String(value ?? '')}
          onChange={(event) => onChange(event.target.value)}
        />
      )

    case 'Date':
      return (
        <Input
          type="datetime-local"
          aria-invalid={invalid}
          value={toLocalInputValue(value)}
          onChange={(event) => onChange(fromLocalInputValue(event.target.value))}
        />
      )

    case 'Select':
      return field.multiple ? (
        <SelectList
          values={field.options.values ?? []}
          selected={asStringList(value)}
          onChange={onChange}
          maxSelect={field.maxSelect}
        />
      ) : (
        <Select<string>
          invalid={invalid}
          value={String(value ?? '')}
          placeholder="— none —"
          options={[
            { value: '', label: '— none —' },
            ...(field.options.values ?? []).map((option) => ({ value: option, label: option })),
          ]}
          onChange={onChange}
        />
      )

    case 'Relation':
      return (
        <RelationControl
          field={field}
          value={value}
          onChange={onChange}
          collections={collections}
        />
      )

    case 'Json':
      return <JsonControl value={value} onChange={onChange} />

    case 'GeoPoint':
      return <GeoPointControl value={value} onChange={onChange} />

    case 'Email':
    case 'Url':
    case 'Text':
    default:
      return field.multiple ? (
        <StringListInput value={asStringList(value)} onChange={onChange} invalid={invalid} />
      ) : (
        <Input
          type={field.type === 'Email' ? 'email' : field.type === 'Url' ? 'url' : 'text'}
          aria-invalid={invalid}
          value={String(value ?? '')}
          onChange={(event) => onChange(event.target.value)}
        />
      )
  }
}
