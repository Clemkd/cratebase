import { useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  ArrowDown,
  ArrowUp,
  ChevronLeft,
  ChevronRight,
  CircleHelp,
  Columns3,
  Database,
  Globe,
  Lock,
  Pencil,
  Plus,
  Save,
  ShieldAlert,
  ShieldCheck,
  SlidersHorizontal,
  Trash2,
  TriangleAlert,
  X,
} from 'lucide-react'
import {
  api,
  describeFailure,
  validationErrors,
  type AccessRules,
  type Collection,
  type CollectionIndex,
  type CollectionKind,
  type CollectionPayload,
  type Field as FieldDefinition,
  type FieldOptions,
  type FieldType,
} from '../api'
import type { CollectionTab, SchemaTab } from '../hooks/useRoute'
import { PageActions, Stat, StatGrid } from '../layout/Page'
import { CREATABLE_TYPES, supportsMultiple, typeLabel } from '../lib/fields'
import { formatCount } from '../lib/format'
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Checkbox,
  ConfirmDialog,
  ErrorBlock,
  Field,
  Input,
  Panel,
  Select,
  SelectMenu,
  TBody,
  THead,
  Table,
  Td,
  Th,
  Tooltip,
  Tr,
  cn,
  useToast,
} from '../ui'
import {
  RULE_LABELS,
  analyseIndexes,
  analyseRules,
  isMultiple,
  type HealthField,
  type IndexIssue,
} from './CollectionHealth'
import {
  activeConstraints,
  constraintShort,
  constraintsFor,
  describeConstraint,
  retype,
  type ConstraintDescriptor,
  type ConstraintTarget,
} from './FieldConstraints'
import { RulesEditor } from './RulesEditor'

interface FieldDraft extends ConstraintTarget {
  /** Stable render key, never transmitted: a new field's identifier is empty. */
  key: string
  /** ⚠️ Empty for a new field. When set, designates the existing field to evolve. */
  id: string
  originalName: string | null
  originalType: FieldType | null
  name: string
  type: FieldType
  required: boolean
  maxSelect: number
  options: FieldOptions
}

interface Draft {
  name: string
  kind: CollectionKind
  fields: FieldDraft[]
  indexes: CollectionIndex[]
  rules: AccessRules
}

const LOCKED_RULES: AccessRules = {
  list: null, view: null, create: null, update: null, delete: null, manage: null,
}

let temporaryKeys = 0

function toDraft(collection: Collection | null): Draft {
  return {
    name: collection?.name ?? '',
    kind: collection?.kind ?? 'Base',
    fields: (collection?.fields ?? [])
      .filter((field) => !field.isSystem)
      .map((field) => ({
        key: field.id,
        id: field.id,
        originalName: field.name,
        originalType: field.type,
        name: field.name,
        type: field.type,
        required: field.required,
        maxSelect: field.maxSelect,
        options: field.options,
      })),
    indexes: collection?.indexes.filter((index) => !isSystemIndex(index, collection)) ?? [],
    rules: collection?.rules ?? LOCKED_RULES,
  }
}

/**
 * Indexes set by the engine on an auth collection (uniqueness of the email and the token key)
 * are reapplied on every write: showing them as editable would suggest they can be removed.
 */
function isSystemIndex(index: CollectionIndex, collection: Collection): boolean {
  return (
    collection.kind === 'Auth' &&
    (index.name === `idx_${collection.name}_email` ||
      index.name === `idx_${collection.name}_token_key`)
  )
}

function toPayload(draft: Draft): CollectionPayload {
  return {
    name: draft.name,
    type: draft.kind,
    rules: draft.rules,
    indexes: draft.indexes,
    fields: draft.fields.map((field) => ({
      // The empty identifier is omitted: that's how the server recognizes a new field. Sending
      // it back for an existing field is what turns a rename into a RENAME COLUMN instead of a
      // DROP followed by an ADD, and therefore what preserves the column's data.
      ...(field.id ? { id: field.id } : {}),
      name: field.name,
      type: field.type,
      required: field.required,
      maxSelect: supportsMultiple(field.type) ? field.maxSelect : 1,
      options: field.options,
    })),
  }
}

/**
 * Schema editor.
 *
 * Four views of a single draft — general, fields, indexes, rules — driven by the URL and
 * rendered by the page's tab bar. The component stays mounted from one view to the next: the
 * input therefore survives switching tabs, and a single button saves everything.
 */
export function CollectionEditor({
  collection,
  collections,
  section,
  onNavigate,
  onSaved,
  onCancel,
  onDeleted,
}: {
  collection: Collection | null
  collections: Collection[]
  section: SchemaTab
  /** Opens another view of the collection: indicators lead to what they flag. */
  onNavigate: (tab: CollectionTab) => void
  onSaved: (name: string) => void
  onCancel: () => void
  onDeleted: () => void
}) {
  const toast = useToast()

  const [draft, setDraft] = useState<Draft>(() => toDraft(collection))
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [failure, setFailure] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [removing, setRemoving] = useState<FieldDraft | null>(null)
  const [confirmingDrop, setConfirmingDrop] = useState(false)
  const [dropping, setDropping] = useState(false)

  const isNew = collection === null
  /** The engine's collection schemas can be viewed, not edited. */
  const readOnly = collection?.isSystem ?? false
  const systemFields = useMemo(
    () => collection?.fields.filter((field) => field.isSystem) ?? [],
    [collection],
  )

  const renamed = useMemo(
    () => draft.fields.filter((field) => field.id !== '' && field.name !== field.originalName),
    [draft.fields],
  )

  const retyped = useMemo(
    () => draft.fields.filter((field) => field.id !== '' && field.type !== field.originalType),
    [draft.fields],
  )

  const patch = (key: string, changes: Partial<FieldDraft>) =>
    setDraft((current) => ({
      ...current,
      fields: current.fields.map((field) => (field.key === key ? { ...field, ...changes } : field)),
    }))

  const changeType = (field: FieldDraft, type: FieldType) => {
    const { target, dropped } = retype(field, field.type, type)

    patch(field.key, { type, maxSelect: target.maxSelect, options: target.options })

    // The change must not be silent: without this message, switching back and forth between two
    // types would erase settings without leaving a trace.
    if (dropped.length > 0) {
      const plural = dropped.length > 1
      toast.info(
        `"${field.name || 'Unnamed field'}": ${dropped.join(', ')} ${plural ? "don't" : "doesn't"} apply to type ${typeLabel(type)} — constraint${plural ? 's' : ''} removed.`,
      )
    }
  }

  const move = (index: number, offset: number) =>
    setDraft((current) => {
      const target = index + offset
      const fields = [...current.fields]
      const moved = fields[index]
      const swapped = fields[target]

      if (!moved || !swapped) return current

      fields[index] = swapped
      fields[target] = moved

      return { ...current, fields }
    })

  const addField = () =>
    setDraft((current) => ({
      ...current,
      fields: [
        ...current.fields,
        {
          key: `new-${++temporaryKeys}`,
          id: '',
          originalName: null,
          originalType: null,
          name: '',
          type: 'Text',
          required: false,
          maxSelect: 1,
          options: {},
        },
      ],
    }))

  const save = async () => {
    setSaving(true)
    setErrors({})
    setFailure(null)

    try {
      const payload = toPayload(draft)
      const saved = collection
        ? await api.collections.update(collection.name, payload)
        : await api.collections.create(payload)

      toast.success(
        collection
          ? `Collection "${saved.name}" saved.`
          : `Collection "${saved.name}" created.`,
      )
      onSaved(saved.name)
    } catch (error) {
      const fieldErrors = validationErrors(error)

      setErrors(fieldErrors)
      setFailure(describeFailure(error))
    } finally {
      setSaving(false)
    }
  }

  const drop = async () => {
    if (!collection) return

    setDropping(true)

    try {
      await api.collections.remove(collection.name)
      toast.success(`Collection "${collection.name}" deleted.`)
      onDeleted()
    } catch (error) {
      toast.error(describeFailure(error))
    } finally {
      setDropping(false)
      setConfirmingDrop(false)
    }
  }

  return (
    <div className="space-y-5">
      {!readOnly && (
        // Actions join the page header, to the right of the collection name, rather than
        // occupying a line of their own below the tabs: vertical space is what a schema editor
        // lacks the most, and the header has room to spare.
        //
        // A single set of buttons for the four views, because they share only one draft:
        // "Save" writes the name, the type, the fields, the indexes, and the rules all at once,
        // whichever tab is open at the moment of the click.
        <PageActions>
          <Button icon={<X size={15} aria-hidden="true" />} onClick={onCancel} disabled={saving}>
            Cancel
          </Button>
          <Button
            variant="primary"
            // Two different gestures, two different glyphs: creating a collection adds a table,
            // saving the schema modifies the one that exists.
            icon={
              isNew ? <Plus size={15} aria-hidden="true" /> : <Save size={15} aria-hidden="true" />
            }
            onClick={() => void save()}
            loading={saving}
          >
            {isNew ? 'Create collection' : 'Save schema'}
          </Button>
        </PageActions>
      )}

      {failure && <ErrorBlock message={failure} />}

      {readOnly && (
        <div className="flex items-start gap-2.5 rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken px-4 py-3">
          <Lock size={15} className="mt-0.5 shrink-0 text-ink-faint" aria-hidden="true" />
          <p className="text-xs text-ink-muted">
            Engine collection: its schema is shown for reference, it can't be edited from the
            console.
          </p>
        </div>
      )}

      {section === 'general' && (
        <GeneralSection
          draft={draft}
          collection={collection}
          systemFields={systemFields}
          errors={errors}
          isNew={isNew}
          readOnly={readOnly}
          onChange={(changes) => setDraft((current) => ({ ...current, ...changes }))}
          onNavigate={onNavigate}
          onRequestDrop={() => setConfirmingDrop(true)}
        />
      )}

      {section === 'fields' && (
        <div className="space-y-3">
          {renamed.length > 0 && (
            <Notice
              tone="success"
              icon={<Pencil size={15} aria-hidden="true" />}
              title="Rename detected"
            >
              {renamed.map((field) => field.originalName).join(', ')} — the field's identifier is
              preserved, so the column is renamed and the data stays in place.
            </Notice>
          )}

          {retyped.length > 0 && (
            <Notice
              tone="warning"
              icon={<TriangleAlert size={15} aria-hidden="true" />}
              title="Type change"
            >
              {retyped.map((field) => field.name).join(', ')} — values that don't convert to the
              new type will be lost.
            </Notice>
          )}

          <Card className="overflow-hidden">
            <CardHeader
              title="Fields"
              description="One row per table column. Constraints are added from the list at the end of each row."
            />

            <FieldsTable
              collectionName={draft.name}
              systemFields={systemFields}
              fields={draft.fields}
              collections={collections}
              errors={errors}
              readOnly={readOnly}
              onPatch={patch}
              onChangeType={changeType}
              onMove={move}
              onRemove={(field) => {
                if (field.id === '') {
                  setDraft((current) => ({
                    ...current,
                    fields: current.fields.filter((item) => item.key !== field.key),
                  }))
                  return
                }

                setRemoving(field)
              }}
            />

            {!readOnly && (
              <div className="border-t border-border-subtle px-5 py-4">
                <Button icon={<Plus size={15} aria-hidden="true" />} onClick={addField}>
                  Add field
                </Button>
              </div>
            )}
          </Card>
        </div>
      )}

      {section === 'indexes' && (
        <Panel
          title="Indexes"
          description="A unique index enforces uniqueness at the database level: the constraint survives concurrent writes, unlike a check done before insertion."
        >
          <IndexesEditor
            collectionName={draft.name}
            indexes={draft.indexes}
            fieldNames={draft.fields.map((field) => field.name).filter(Boolean)}
            readOnly={readOnly}
            onChange={(indexes) => setDraft({ ...draft, indexes })}
          />
        </Panel>
      )}

      {section === 'rules' && (
        <RulesEditor
          kind={draft.kind}
          rules={draft.rules}
          readOnly={readOnly}
          onChange={(rules) => setDraft({ ...draft, rules })}
        />
      )}

      <ConfirmDialog
        open={removing !== null}
        title={`Delete field "${removing?.name ?? ''}"?`}
        confirmLabel="Delete field"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        message={
          <span className="flex items-start gap-2.5">
            <ShieldAlert size={18} className="mt-0.5 shrink-0 text-danger" aria-hidden="true" />
            <span>
              The column will be removed from the table on save and{' '}
              <strong className="font-semibold text-ink">all its data will be lost</strong>, with
              no way back. To rename the field, change its name instead: the data follows.
            </span>
          </span>
        }
        onConfirm={() => {
          const target = removing

          if (target) {
            setDraft((current) => ({
              ...current,
              fields: current.fields.filter((item) => item.key !== target.key),
            }))
          }

          setRemoving(null)
        }}
        onClose={() => setRemoving(null)}
      />

      <ConfirmDialog
        open={confirmingDrop && collection !== null}
        busy={dropping}
        title={`Delete collection "${collection?.name ?? ''}"?`}
        message="The table and all its data will be destroyed. This operation is permanent and cannot be undone."
        confirmLabel="Delete collection"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        onConfirm={() => void drop()}
        onClose={() => setConfirmingDrop(false)}
      />
    </div>
  )
}

/* ------------------------------------------------------------------ General */

/**
 * Collection configuration and health indicators.
 *
 * The indicators reflect the draft: opening a rule to everyone turns the tile red before it's
 * even saved, which is the moment the information is useful.
 */
function GeneralSection({
  draft,
  collection,
  systemFields,
  errors,
  isNew,
  readOnly,
  onChange,
  onNavigate,
  onRequestDrop,
}: {
  draft: Draft
  collection: Collection | null
  systemFields: FieldDefinition[]
  errors: Record<string, string[]>
  isNew: boolean
  readOnly: boolean
  onChange: (changes: Partial<Draft>) => void
  onNavigate: (tab: CollectionTab) => void
  onRequestDrop: () => void
}) {
  const rules = useMemo(() => analyseRules(draft.kind, draft.rules), [draft.kind, draft.rules])

  const healthFields = useMemo<HealthField[]>(
    () => [
      ...systemFields.map((field) => ({
        name: field.name,
        type: field.type,
        multiple: field.multiple,
      })),
      ...draft.fields.map((field) => ({
        name: field.name,
        type: field.type,
        multiple: isMultiple(field.type, field.maxSelect),
      })),
    ],
    [systemFields, draft.fields],
  )

  const issues = useMemo(
    () =>
      analyseIndexes({
        indexes: draft.indexes,
        savedIndexes: collection?.indexes ?? [],
        fields: healthFields,
        kind: draft.kind,
        collectionName: draft.name,
        isNew,
      }),
    [draft.indexes, draft.kind, draft.name, collection, healthFields, isNew],
  )

  const records = useRecordCount(collection?.name ?? null)
  const required = draft.fields.filter((field) => field.required).length
  const total = rules.applicable.length

  return (
    <div className="space-y-5">
      <Panel
        title="Configuration"
        description="The schema is data: creating a collection creates its table and exposes its CRUD API immediately."
      >
        <div className="grid gap-4 sm:grid-cols-2">
          <Field
            label="Name"
            required
            hint={
              isNew
                ? 'Becomes the table name. Letters, digits, underscores.'
                : 'Fixed: the table already carries this name, and changing it would amount to creating another one.'
            }
            error={errors.name?.join(' ')}
          >
            <Input
              value={draft.name}
              disabled={!isNew}
              spellCheck={false}
              className="font-mono"
              onChange={(event) => onChange({ name: event.target.value })}
            />
          </Field>

          <Field
            label="Kind"
            hint={
              isNew
                ? '"Auth" adds the email, password, and session tokens.'
                : 'Fixed: the system fields set at creation depend on it.'
            }
          >
            <Select<CollectionKind>
              value={draft.kind}
              disabled={!isNew}
              options={[
                { value: 'Base', label: 'Base — data', hint: 'Plain table, no account.' },
                {
                  value: 'Auth',
                  label: 'Auth — accounts',
                  hint: 'Email, password, and session tokens.',
                },
              ]}
              onChange={(kind) => onChange({ kind })}
            />
          </Field>
        </div>
      </Panel>

      <section className="space-y-3">
        <div>
          <h2 className="text-sm font-semibold text-ink">Indicators</h2>
          <p className="mt-0.5 text-xs text-ink-muted">
            Computed on the current draft. Each tile leads to the relevant view.
          </p>
        </div>

        <StatGrid>
          <Stat
            label="Rules open to everyone"
            value={`${rules.open.length} / ${total}`}
            tone={rules.open.length > 0 ? 'danger' : 'success'}
            icon={<Globe size={15} aria-hidden="true" />}
            hint={
              rules.open.length === 0
                ? 'No action accessible without a token.'
                : rules.openWrites.length > 0
                  ? `Including ${rules.openWrites.length} write: ${rules.openWrites.map((action) => RULE_LABELS[action]).join(', ')}.`
                  : `Read-only: ${rules.openReads.map((action) => RULE_LABELS[action]).join(', ')}.`
            }
            onClick={() => onNavigate('rules')}
            actionLabel="View access rules"
          />

          <Stat
            label="Locked rules"
            value={`${rules.locked.length} / ${total}`}
            icon={<Lock size={15} aria-hidden="true" />}
            hint="Superuser only."
            onClick={() => onNavigate('rules')}
            actionLabel="View access rules"
          />

          <Stat
            label="Conditional rules"
            value={`${rules.conditional.length} / ${total}`}
            tone="brand"
            icon={<SlidersHorizontal size={15} aria-hidden="true" />}
            hint="Allowed when their expression is true."
            onClick={() => onNavigate('rules')}
            actionLabel="View access rules"
          />

          <Stat
            label="Index issues"
            value={issues.length}
            tone={
              issues.length === 0
                ? 'success'
                : issues.some((issue) => issue.tone === 'danger')
                  ? 'danger'
                  : 'warning'
            }
            icon={
              issues.length === 0 ? (
                <ShieldCheck size={15} aria-hidden="true" />
              ) : (
                <TriangleAlert size={15} aria-hidden="true" />
              )
            }
            hint={
              issues.length === 0
                ? `${draft.indexes.length} index${draft.indexes.length > 1 ? 'es' : ''} declared, no issues detected.`
                : 'Detail below the indicators.'
            }
            onClick={() => onNavigate('indexes')}
            actionLabel="View indexes"
          />

          <Stat
            label="Fields"
            value={systemFields.length + draft.fields.length}
            icon={<Columns3 size={15} aria-hidden="true" />}
            hint={`Including ${systemFields.length} set by the engine and ${required} required.`}
            onClick={() => onNavigate('fields')}
            actionLabel="View fields"
          />

          <Stat
            label="Records"
            value={records === null ? '—' : formatCount(records)}
            icon={<Database size={15} aria-hidden="true" />}
            hint={
              isNew
                ? 'The table will be created on save.'
                : records === null
                  ? 'Count unavailable.'
                  : 'Rows currently in the database.'
            }
            onClick={isNew ? undefined : () => onNavigate('records')}
            actionLabel="View records"
          />
        </StatGrid>

        {rules.open.length > 0 && (
          <div
            role="status"
            className="rounded-[var(--radius-card)] border border-danger/40 bg-danger-subtle px-4 py-3"
          >
            <p className="flex items-center gap-2 text-sm font-semibold text-ink">
              <Globe size={15} className="shrink-0 text-danger" aria-hidden="true" />
              Actions allowed without any token
            </p>
            <ul className="mt-2 space-y-1">
              {rules.open.map((action) => (
                <li key={action} className="flex flex-wrap items-center gap-2 text-xs text-ink">
                  <Badge tone={rules.openWrites.includes(action) ? 'danger' : 'warning'}>
                    {RULE_LABELS[action]}
                  </Badge>
                  {rules.openWrites.includes(action)
                    ? 'Any anonymous visitor can modify the database through this action.'
                    : 'Any anonymous visitor can read this data.'}
                </li>
              ))}
            </ul>
          </div>
        )}

        {issues.length > 0 && <IssueList issues={issues} onNavigate={() => onNavigate('indexes')} />}
      </section>

      {!isNew && !readOnly && collection && (
        <Card className="border-danger/30">
          <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4">
            <div className="min-w-0">
              <p className="text-sm font-semibold text-ink">Delete collection</p>
              <p className="mt-0.5 text-xs text-ink-muted">
                The table and all its data are destroyed. This operation is permanent.
              </p>
            </div>
            <Button
              variant="danger"
              icon={<Trash2 size={15} aria-hidden="true" />}
              onClick={onRequestDrop}
            >
              Delete "{collection.name}"
            </Button>
          </div>
        </Card>
      )}
    </div>
  )
}

function IssueList({ issues, onNavigate }: { issues: IndexIssue[]; onNavigate: () => void }) {
  return (
    <Card className="overflow-hidden">
      <CardHeader
        title="Indexing issues"
        description="None of these prevent saving: they're indexes that won't be used, or queries that will scan the table."
        action={
          <Button size="sm" onClick={onNavigate}>
            Open indexes
          </Button>
        }
      />
      <ul className="divide-y divide-border-subtle">
        {issues.map((issue) => (
          <li key={issue.id} className="flex items-start gap-3 px-5 py-3">
            <TriangleAlert
              size={15}
              aria-hidden="true"
              className={cn('mt-0.5 shrink-0', issue.tone === 'danger' ? 'text-danger' : 'text-warning')}
            />
            <div className="min-w-0">
              <p className="text-sm font-medium text-ink">{issue.title}</p>
              <p className="mt-0.5 text-xs text-ink-muted">{issue.detail}</p>
            </div>
          </li>
        ))}
      </ul>
    </Card>
  )
}

/**
 * Number of records in the collection.
 *
 * A single-item page is enough: it's `totalItems` that's wanted, not the rows. The failure is
 * swallowed — an unavailable counter must not turn the configuration screen into an error screen.
 */
function useRecordCount(name: string | null): number | null {
  const [count, setCount] = useState<number | null>(null)

  useEffect(() => {
    if (!name) {
      setCount(null)
      return
    }

    let abandoned = false

    api.records
      .list(name, { perPage: 1 })
      .then((page) => {
        if (!abandoned) setCount(page.totalItems)
      })
      .catch(() => {
        if (!abandoned) setCount(null)
      })

    return () => {
      abandoned = true
    }
  }, [name])

  return count
}

function Notice({
  tone,
  icon,
  title,
  children,
}: {
  tone: 'success' | 'warning'
  icon: ReactNode
  title: string
  children: ReactNode
}) {
  return (
    <div
      className={cn(
        'flex items-start gap-2.5 rounded-[var(--radius-card)] border px-4 py-3',
        tone === 'success' ? 'border-success/40 bg-success/10' : 'border-warning/40 bg-warning/10',
      )}
    >
      <span className={cn('mt-0.5 shrink-0', tone === 'success' ? 'text-success' : 'text-warning')}>
        {icon}
      </span>
      <p className="text-xs text-ink">
        <strong className="font-semibold">{title}</strong> sur {children}
      </p>
    </div>
  )
}

/* --------------------------------------------------------------- Fields table */

/**
 * Schema as a grid: one row per field, one column per aspect.
 *
 * This is the shape in which a schema is actually designed — fields get compared against each
 * other, the required ones and the constrained ones stand out at a glance. A card unfolded per
 * field forced scrolling to answer those questions.
 */
function FieldsTable({
  collectionName,
  systemFields,
  fields,
  collections,
  errors,
  readOnly,
  onPatch,
  onChangeType,
  onMove,
  onRemove,
}: {
  collectionName: string
  systemFields: FieldDefinition[]
  fields: FieldDraft[]
  collections: Collection[]
  errors: Record<string, string[]>
  readOnly: boolean
  onPatch: (key: string, changes: Partial<FieldDraft>) => void
  onChangeType: (field: FieldDraft, type: FieldType) => void
  onMove: (index: number, offset: number) => void
  onRemove: (field: FieldDraft) => void
}) {
  return (
    <Table bare caption={`Fields of the ${collectionName} collection`}>
      <THead>
        <tr>
          {!readOnly && (
            <Th className="w-14">
              <span className="sr-only">Order</span>
            </Th>
          )}
          <Th className="min-w-56">Field</Th>
          <Th className="w-44">Type</Th>
          <Th className="w-24 text-center">Required</Th>
          <Th className="min-w-72">Constraints</Th>
          {!readOnly && (
            <Th className="w-12">
              <span className="sr-only">Actions</span>
            </Th>
          )}
        </tr>
      </THead>

      <TBody>
        {systemFields.map((field) => (
          <SystemFieldRow key={field.id} field={field} readOnly={readOnly} />
        ))}

        {fields.map((field, index) => (
          <FieldRow
            key={field.key}
            field={field}
            index={index}
            total={fields.length}
            collections={collections}
            readOnly={readOnly}
            error={field.name ? errors[field.name]?.join(' ') : undefined}
            onPatch={(changes) => onPatch(field.key, changes)}
            onChangeType={(type) => onChangeType(field, type)}
            onMove={(offset) => onMove(index, offset)}
            onRemove={() => onRemove(field)}
          />
        ))}

        {systemFields.length === 0 && fields.length === 0 && (
          <tr>
            <td colSpan={readOnly ? 4 : 6} className="px-5 py-8 text-center text-sm text-ink-muted">
              No field declared. The engine will still set{' '}
              <code className="font-mono">id</code>, <code className="font-mono">created</code>, and{' '}
              <code className="font-mono">updated</code>.
            </td>
          </tr>
        )}
      </TBody>
    </Table>
  )
}

/**
 * Field set by the engine.
 *
 * Shown because it's part of the real schema — hiding it would suggest the table only has the
 * columns declared here. Not editable because it doesn't belong to the project.
 */
function SystemFieldRow({ field, readOnly }: { field: FieldDefinition; readOnly: boolean }) {
  const target: ConstraintTarget = { maxSelect: field.maxSelect, options: field.options }
  const constraints = activeConstraints(field.type, target)

  return (
    <Tr muted>
      {!readOnly && <Td />}

      <Td>
        <span className="flex items-center gap-2">
          <Lock size={12} className="shrink-0 text-ink-faint" aria-hidden="true" />
          <span className="truncate font-mono text-xs text-ink-muted">{field.name}</span>
          <Badge>system</Badge>
        </span>
      </Td>

      <Td className="text-xs text-ink-muted">{typeLabel(field.type)}</Td>

      <Td className="text-center text-xs text-ink-muted">
        {field.required ? 'yes' : '—'}
        <span className="sr-only">{field.required ? ' — required' : ' — optional'}</span>
      </Td>

      <Td>
        {constraints.length === 0 ? (
          <span className="text-xs text-ink-faint">—</span>
        ) : (
          <span className="flex flex-wrap gap-1">
            {constraints.map((constraint) => (
              <Badge key={constraint.id}>{describeConstraint(constraint, target)}</Badge>
            ))}
          </span>
        )}
      </Td>

      {!readOnly && <Td />}
    </Tr>
  )
}

/**
 * Identity state of a field, reduced to a pictogram.
 *
 * The identifier and its explanation used to take a second line under the name, on every row of
 * the table: the height doubled and only three or four fields remained visible at once — in a
 * screen whose whole point is comparing fields at a glance.
 *
 * The pictogram changes shape and tone with the state, so the signal stays visible without
 * reading: a renamed field is spotted by its green pencil, a new field by its plus sign. Only the
 * detail moves into the tooltip, reachable on hover as on keyboard focus.
 */
function FieldIdentity({
  field,
  renamed,
  label,
}: {
  field: FieldDraft
  renamed: boolean
  label: string
}) {
  const state = !field.id
    ? {
        icon: <Plus size={13} aria-hidden="true" />,
        tone: 'text-brand',
        summary: 'New field',
        detail: 'No identifier: the server will create the column on save.',
      }
    : renamed
      ? {
          icon: <Pencil size={13} aria-hidden="true" />,
          tone: 'text-success',
          summary: `Renamed from "${field.originalName}"`,
          detail:
            'The identifier is preserved, so the column is renamed and the data stays in place.',
        }
      : {
          icon: <CircleHelp size={13} aria-hidden="true" />,
          tone: 'text-ink-faint',
          summary: `Identifier ${field.id.slice(0, 8)}`,
          detail:
            "Sent back to the server as-is: it's what distinguishes a rename from a replacement.",
        }

  return (
    <Tooltip
      content={
        <>
          <span className="font-medium">{state.summary}</span>
          <span className="mt-0.5 block text-ink-muted">{state.detail}</span>
        </>
      }
    >
      <button
        type="button"
        // A button, not a decorative icon: it's the only form that receives focus, and
        // therefore the only one through which the tooltip opens by keyboard.
        aria-label={`${state.summary} — ${label}`}
        className={cn(
          'inline-flex size-6 shrink-0 items-center justify-center rounded-full',
          'transition-colors hover:bg-surface-hover focus-visible:-outline-offset-1',
          state.tone,
        )}
      >
        {state.icon}
      </button>
    </Tooltip>
  )
}

function FieldRow({
  field,
  index,
  total,
  collections,
  readOnly,
  error,
  onPatch,
  onChangeType,
  onMove,
  onRemove,
}: {
  field: FieldDraft
  index: number
  total: number
  collections: Collection[]
  readOnly: boolean
  error?: string
  onPatch: (changes: Partial<FieldDraft>) => void
  onChangeType: (type: FieldType) => void
  onMove: (offset: number) => void
  onRemove: () => void
}) {
  const label = field.name || `field ${index + 1}`

  // An existing field can carry a type the console doesn't yet offer at creation (file, auto
  // date). Without its option, the list would show whatever type comes first and suggest a
  // conversion that never happened.
  const types = CREATABLE_TYPES.includes(field.type)
    ? CREATABLE_TYPES
    : [field.type, ...CREATABLE_TYPES]

  const renamedHere = field.id !== '' && field.name !== field.originalName

  return (
    <Tr className={cn(error && 'bg-danger-subtle/60')}>
      {!readOnly && (
        <Td>
          {/* The two arrows side by side rather than stacked: stacked, they alone measured
              48 px and fixed the whole row's height, above the 32 px of the neighboring
              controls. */}
          <div className="flex items-center">
            <Button
              variant="ghost"
              size="icon"
              className="size-6"
              aria-label={`Move ${label} up`}
              disabled={index === 0}
              onClick={() => onMove(-1)}
            >
              <ArrowUp size={13} aria-hidden="true" />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              className="size-6"
              aria-label={`Move ${label} down`}
              disabled={index === total - 1}
              onClick={() => onMove(1)}
            >
              <ArrowDown size={13} aria-hidden="true" />
            </Button>
          </div>
        </Td>
      )}

      <Td>
        <div className="flex items-center gap-1.5">
          <Input
            value={field.name}
            spellCheck={false}
            placeholder="field_name"
            disabled={readOnly}
            aria-label={`Name of ${label}`}
            aria-invalid={Boolean(error)}
            className="h-8 font-mono text-xs"
            onChange={(event) => onPatch({ name: event.target.value })}
          />

          <FieldIdentity field={field} renamed={renamedHere} label={label} />
        </div>

        {error && (
          <p role="alert" className="mt-1 text-xs font-medium text-danger">
            {error}
          </p>
        )}
      </Td>

      <Td>
        <SelectMenu<FieldType>
          value={field.type}
          disabled={readOnly}
          size="sm"
          aria-label={`Type of ${label}`}
          options={types.map((type) => ({ value: type, label: typeLabel(type) }))}
          onChange={onChangeType}
        />
      </Td>

      <Td className="text-center">
        <input
          type="checkbox"
          checked={field.required}
          disabled={readOnly}
          aria-label={`${label} required`}
          className="size-4 cursor-pointer rounded border-border-strong accent-brand disabled:cursor-not-allowed disabled:opacity-50"
          onChange={(event) => onPatch({ required: event.target.checked })}
        />
      </Td>

      <Td>
        <ConstraintCell
          field={field}
          label={label}
          collections={collections}
          readOnly={readOnly}
          onPatch={onPatch}
        />
      </Td>

      {!readOnly && (
        <Td>
          <Button
            variant="ghost"
            size="icon"
            className="size-8 hover:text-danger"
            aria-label={`Delete ${label}`}
            onClick={onRemove}
          >
            <Trash2 size={15} aria-hidden="true" />
          </Button>
        </Td>
      )}
    </Tr>
  )
}

/** Constraints for a field: removable chips, plus the list of ones still available to add. */
function ConstraintCell({
  field,
  label,
  collections,
  readOnly,
  onPatch,
}: {
  field: FieldDraft
  label: string
  collections: Collection[]
  readOnly: boolean
  onPatch: (changes: Partial<FieldDraft>) => void
}) {
  const applicable = constraintsFor(field.type)
  const active = applicable.filter((constraint) => constraint.isSet(field))
  const available = applicable.filter((constraint) => !constraint.isSet(field))

  const apply = (next: ConstraintTarget) =>
    onPatch({ maxSelect: next.maxSelect, options: next.options })

  if (applicable.length === 0) {
    return <span className="text-xs text-ink-faint">No constraints for this type.</span>
  }

  return (
    <div className="flex items-center gap-1.5">
      {/* Chips scroll horizontally instead of wrapping: a "File" field carries five of them, and
          stacking them made a row four times taller than the neighboring one — in a table whose
          whole point is comparing fields against each other. */}
      <div className="scrollbar-none flex min-w-0 flex-1 items-center gap-1.5 overflow-x-auto">
        {active.map((constraint) => (
          <ConstraintChip
            key={constraint.id}
            constraint={constraint}
            field={field}
            fieldLabel={label}
            collections={collections}
            readOnly={readOnly}
            onWrite={(raw) => apply(constraint.write(field, raw))}
            onClear={() => apply(constraint.clear(field))}
          />
        ))}

        {active.length === 0 && <span className="text-xs text-ink-faint">—</span>}
      </div>

      {/* Outside the scrollable area: the add control must stay visible even when chips overflow. */}
      {!readOnly && available.length > 0 && (
        <SelectMenu<string>
          value=""
          size="sm"
          placeholder="+ constraint"
          aria-label={`Add a constraint to ${label}`}
          className="h-7 w-auto shrink-0 border-dashed bg-transparent px-2 text-xs text-ink-muted hover:border-brand hover:text-brand"
          options={available.map((constraint) => ({
            value: constraint.id,
            label: constraint.label,
          }))}
          onChange={(id) => {
            const chosen = available.find((constraint) => constraint.id === id)

            if (chosen) apply(chosen.add(field))
          }}
        />
      )}
    </div>
  )
}

const CHIP_INPUT =
  'h-5 rounded border border-border-subtle bg-surface px-1 font-mono text-[11px] text-ink focus:border-brand'

function ConstraintChip({
  constraint,
  field,
  fieldLabel,
  collections,
  readOnly,
  onWrite,
  onClear,
}: {
  constraint: ConstraintDescriptor
  field: FieldDraft
  fieldLabel: string
  collections: Collection[]
  readOnly: boolean
  onWrite: (raw: string) => void
  onClear: () => void
}) {
  const controlLabel = `${constraint.label} of ${fieldLabel}`
  const value = constraint.read(field)

  return (
    <span
      title={constraint.label}
      className="inline-flex shrink-0 items-center gap-1 rounded-full bg-surface-sunken py-0.5 pr-0.5 pl-2 text-xs"
    >
      <span className="whitespace-nowrap text-ink-muted">{constraintShort(constraint)}</span>

      {constraint.kind === 'number' && (
        <input
          type="number"
          min={0}
          value={value}
          disabled={readOnly}
          aria-label={controlLabel}
          className={cn(CHIP_INPUT, 'w-16')}
          onChange={(event) => onWrite(event.target.value)}
        />
      )}

      {(constraint.kind === 'text' || constraint.kind === 'list') && (
        <input
          type="text"
          value={value}
          spellCheck={false}
          disabled={readOnly}
          aria-label={controlLabel}
          placeholder={constraint.kind === 'list' ? 'a, b, c' : '^[a-z]+$'}
          className={cn(CHIP_INPUT, constraint.kind === 'list' ? 'w-40' : 'w-32')}
          onChange={(event) => onWrite(event.target.value)}
        />
      )}

      {constraint.kind === 'collection' && (
        <SelectMenu<string>
          value={value}
          disabled={readOnly}
          size="sm"
          invalid={value === ''}
          aria-label={controlLabel}
          className={cn(CHIP_INPUT, 'w-32', value === '' && 'border-danger')}
          options={collections.map((candidate) => ({
            value: candidate.name,
            label: candidate.name,
          }))}
          onChange={onWrite}
        />
      )}

      {readOnly ? (
        <span className="w-1" />
      ) : (
        <button
          type="button"
          aria-label={`Remove constraint "${constraint.label}" from ${fieldLabel}`}
          onClick={onClear}
          className="rounded-full p-0.5 text-ink-faint transition-colors hover:text-danger"
        >
          <X size={11} aria-hidden="true" />
        </button>
      )}
    </span>
  )
}

/* --------------------------------------------------------------------- Indexes */

/**
 * Index name derived from the collection and the indexed fields, in their order.
 *
 * The order is part of the name because it's part of the index: on `(a, b)`, a query filtering
 * on `b` alone gains nothing from it. Two indexes on the same fields in two different orders are
 * two distinct objects, and their names must be too.
 */
function suggestIndexName(collectionName: string, fields: string[]): string {
  if (fields.length === 0) return ''

  const sanitise = (value: string) => value.replace(/[^A-Za-z0-9]+/g, '_').replace(/^_+|_+$/g, '')
  const proposed = ['idx', sanitise(collectionName), ...fields.map(sanitise)]
    .filter(Boolean)
    .join('_')

  // PostgreSQL truncates identifiers to 63 bytes: beyond that, two indexes with similar names
  // would become the same object, and creating the second would fail for an unreadable reason.
  return proposed.length <= 63 ? proposed : proposed.slice(0, 63).replace(/_+$/, '')
}

/**
 * Index editor.
 *
 * Fields are picked from a list, never typed: an index targets columns that exist, and free-form
 * input let you write an approximate name that only the database would reject, on save, without
 * saying which index was at fault.
 */
function IndexesEditor({
  collectionName,
  indexes,
  fieldNames,
  readOnly,
  onChange,
}: {
  collectionName: string
  indexes: CollectionIndex[]
  fieldNames: string[]
  readOnly: boolean
  onChange: (indexes: CollectionIndex[]) => void
}) {
  const patch = (index: number, changes: Partial<CollectionIndex>) =>
    onChange(
      indexes.map((entry, position) => (position === index ? { ...entry, ...changes } : entry)),
    )

  /**
   * Applies a new list of fields and refreshes the name if it wasn't typed by hand.
   *
   * "By hand" is inferred rather than tracked: a name counts as automatic if it's empty or if it
   * matches what the previous composition would have produced. No flag to keep up to date, so no
   * flag to fall out of sync.
   */
  const changeFields = (position: number, fields: string[]) => {
    const current = indexes[position]

    if (!current) return

    const wasAuto =
      current.name === '' || current.name === suggestIndexName(collectionName, current.fields)

    patch(position, {
      fields,
      name: wasAuto ? suggestIndexName(collectionName, fields) : current.name,
    })
  }

  return (
    <div className="space-y-3">
      {indexes.length === 0 && (
        <p className="text-xs text-ink-muted">No index declared on this collection.</p>
      )}

      {indexes.map((index, position) => {
        const available = fieldNames.filter((name) => !index.fields.includes(name))

        return (
          <div
            key={position}
            className="grid gap-3 rounded-[var(--radius-card)] border border-border-subtle p-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)_auto_auto]"
          >
            <Field
              label="Indexed fields"
              hint={
                index.fields.length > 1
                  ? "Order matters: a filter targeting only the second field doesn't use the index."
                  : "Choose from the collection's fields."
              }
            >
              <FieldTokens
                tokens={index.fields}
                available={available}
                readOnly={readOnly}
                label={`Fields of index ${index.name || position + 1}`}
                onChange={(fields) => changeFields(position, fields)}
              />
            </Field>

            <Field
              label="Name"
              hint={
                index.name === suggestIndexName(collectionName, index.fields)
                  ? 'Derived from the fields. Typing a name fixes it.'
                  : 'Typed by hand.'
              }
            >
              <Input
                value={index.name}
                spellCheck={false}
                disabled={readOnly}
                placeholder={suggestIndexName(collectionName, index.fields) || 'idx_…'}
                className="font-mono text-xs"
                onChange={(event) => patch(position, { name: event.target.value })}
              />
            </Field>

            <div className="self-center lg:pt-5">
              <Checkbox
                label="Unique"
                checked={index.unique}
                disabled={readOnly}
                onChange={(event) => patch(position, { unique: event.target.checked })}
              />
            </div>

            <div className="self-center lg:pt-5">
              <Button
                variant="ghost"
                size="icon"
                disabled={readOnly}
                aria-label={`Delete index ${index.name || position + 1}`}
                className="hover:text-danger"
                onClick={() => onChange(indexes.filter((_, entry) => entry !== position))}
              >
                <Trash2 size={15} aria-hidden="true" />
              </Button>
            </div>
          </div>
        )
      })}

      {!readOnly && (
        <Button
          icon={<Plus size={15} aria-hidden="true" />}
          onClick={() => onChange([...indexes, { name: '', fields: [], unique: false }])}
        >
          Add index
        </Button>
      )}
    </div>
  )
}

/**
 * Ordered sequence of fields, presented as chips in an input area.
 *
 * The container mimics a text field — border, background, focus ring — but accepts no
 * keystrokes: fields are added through the dropdown on the right, removed with the cross. This
 * is what guarantees that indexed fields actually exist, without having to validate a string
 * after the fact.
 */
function FieldTokens({
  tokens,
  available,
  readOnly,
  label,
  onChange,
}: {
  tokens: string[]
  available: string[]
  readOnly: boolean
  label: string
  onChange: (tokens: string[]) => void
}) {
  const move = (position: number, offset: number) => {
    const target = position + offset
    const next = [...tokens]
    const moved = next[position]
    const swapped = next[target]

    if (moved === undefined || swapped === undefined) return

    next[position] = swapped
    next[target] = moved
    onChange(next)
  }

  return (
    <div
      role="group"
      aria-label={label}
      className={cn(
        'flex min-h-9 flex-wrap items-center gap-1.5 rounded-[var(--radius-control)] border px-2 py-1.5',
        'border-border-strong bg-surface focus-within:border-brand',
        readOnly && 'opacity-60',
      )}
    >
      {tokens.length === 0 && (
        <span className="px-0.5 text-xs text-ink-faint">No field selected</span>
      )}

      {tokens.map((name, position) => (
        <span
          key={name}
          className="inline-flex items-center gap-0.5 rounded-full bg-brand-subtle py-0.5 pr-0.5 pl-2 text-xs font-medium text-ink"
        >
          <span className="font-mono">{name}</span>

          {!readOnly && tokens.length > 1 && (
            <>
              <button
                type="button"
                aria-label={`Move ${name} earlier`}
                disabled={position === 0}
                onClick={() => move(position, -1)}
                className="inline-flex size-4 items-center justify-center rounded-full text-ink-muted hover:text-ink disabled:opacity-30 focus-visible:-outline-offset-1"
              >
                <ChevronLeft size={11} aria-hidden="true" />
              </button>
              <button
                type="button"
                aria-label={`Move ${name} later`}
                disabled={position === tokens.length - 1}
                onClick={() => move(position, 1)}
                className="inline-flex size-4 items-center justify-center rounded-full text-ink-muted hover:text-ink disabled:opacity-30 focus-visible:-outline-offset-1"
              >
                <ChevronRight size={11} aria-hidden="true" />
              </button>
            </>
          )}

          {!readOnly && (
            <button
              type="button"
              aria-label={`Remove ${name}`}
              onClick={() => onChange(tokens.filter((entry) => entry !== name))}
              className="inline-flex size-4 items-center justify-center rounded-full text-ink-muted hover:bg-danger hover:text-danger-ink focus-visible:-outline-offset-1"
            >
              <X size={11} aria-hidden="true" />
            </button>
          )}
        </span>
      ))}

      {!readOnly && available.length > 0 && (
        // The list always returns to its neutral value: it's used to add, not to represent a
        // current choice — it's the chips that carry the state.
        <SelectMenu<string>
          value=""
          size="sm"
          placeholder="+ field"
          aria-label={`Add a field to ${label}`}
          className="ml-auto h-6 w-auto border-0 bg-transparent px-1.5 text-xs text-ink-muted hover:text-brand focus-visible:-outline-offset-1"
          options={available.map((name) => ({ value: name, label: name }))}
          onChange={(name) => onChange([...tokens, name])}
        />
      )}
    </div>
  )
}
