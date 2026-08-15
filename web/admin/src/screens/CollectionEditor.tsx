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
  /** Clé de rendu stable, jamais transmise : l'identifiant d'un champ nouveau est vide. */
  key: string
  /** ⚠️ Vide pour un champ nouveau. Renseigné, il désigne le champ existant à faire évoluer. */
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
 * Les index posés par le moteur sur une collection d'auth (unicité de l'adresse et de la clé de
 * jeton) sont réappliqués à chaque écriture : les afficher comme modifiables laisserait croire
 * qu'on peut les retirer.
 */
function isSystemIndex(index: CollectionIndex, collection: Collection): boolean {
  return (
    collection.kind === 'Auth' &&
    (index.name === `idx_${collection.name}_email` ||
      index.name === `idx_${collection.name}_tokenKey`)
  )
}

function toPayload(draft: Draft): CollectionPayload {
  return {
    name: draft.name,
    type: draft.kind,
    rules: draft.rules,
    indexes: draft.indexes,
    fields: draft.fields.map((field) => ({
      // L'identifiant vide est omis : c'est ainsi que le serveur reconnaît un champ nouveau. Le
      // renvoyer pour un champ existant est ce qui transforme un renommage en RENAME COLUMN au
      // lieu d'un DROP suivi d'un ADD, donc ce qui préserve les données de la colonne.
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
 * Éditeur de schéma.
 *
 * Quatre vues d'un même brouillon — général, champs, index, règles — pilotées par l'URL et rendues
 * par la barre d'onglets de la page. Le composant reste monté d'une vue à l'autre : la saisie
 * survit donc au changement d'onglet, et un seul bouton enregistre l'ensemble.
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
  /** Ouvre une autre vue de la collection : les indicateurs mènent à ce qu'ils signalent. */
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
  /** Le schéma des collections du moteur se consulte, il ne se modifie pas. */
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

    // Le changement ne doit pas être silencieux : sans ce message, un aller-retour entre deux types
    // effacerait des réglages sans laisser de trace.
    if (dropped.length > 0) {
      const plural = dropped.length > 1
      toast.info(
        `« ${field.name || 'Champ sans nom'} » : ${dropped.join(', ')} ne s'applique${plural ? 'nt' : ''} pas au type ${typeLabel(type)} — contrainte${plural ? 's' : ''} retirée${plural ? 's' : ''}.`,
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
          key: `nouveau-${++temporaryKeys}`,
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
          ? `Collection « ${saved.name} » enregistrée.`
          : `Collection « ${saved.name} » créée.`,
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
      toast.success(`Collection « ${collection.name} » supprimée.`)
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
        // Les actions rejoignent l'en-tête de la page, à droite du nom de la collection, plutôt que
        // d'occuper une ligne à elles seules sous les onglets : l'espace vertical est ce qui manque
        // le plus à un éditeur de schéma, et l'en-tête a de la place libre.
        //
        // Un seul jeu de boutons pour les quatre vues, parce qu'elles n'ont qu'un brouillon :
        // « Enregistrer » écrit le nom, le type, les champs, les index et les règles d'un coup,
        // quel que soit l'onglet ouvert au moment du clic.
        <PageActions>
          <Button icon={<X size={15} aria-hidden="true" />} onClick={onCancel} disabled={saving}>
            Annuler
          </Button>
          <Button
            variant="primary"
            // Deux gestes différents, deux glyphes différents : créer une collection ajoute une
            // table, enregistrer le schéma modifie celle qui existe.
            icon={
              isNew ? <Plus size={15} aria-hidden="true" /> : <Save size={15} aria-hidden="true" />
            }
            onClick={() => void save()}
            loading={saving}
          >
            {isNew ? 'Créer la collection' : 'Enregistrer le schéma'}
          </Button>
        </PageActions>
      )}

      {failure && <ErrorBlock message={failure} />}

      {readOnly && (
        <div className="flex items-start gap-2.5 rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken px-4 py-3">
          <Lock size={15} className="mt-0.5 shrink-0 text-ink-faint" aria-hidden="true" />
          <p className="text-xs text-ink-muted">
            Collection du moteur : son schéma est affiché pour référence, il ne se modifie pas depuis
            la console.
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
              title="Renommage détecté"
            >
              {renamed.map((field) => field.originalName).join(', ')} — l'identifiant du champ est
              conservé, donc la colonne est renommée et les données restent en place.
            </Notice>
          )}

          {retyped.length > 0 && (
            <Notice
              tone="warning"
              icon={<TriangleAlert size={15} aria-hidden="true" />}
              title="Changement de type"
            >
              {retyped.map((field) => field.name).join(', ')} — les valeurs qui ne se convertissent
              pas dans le nouveau type seront perdues.
            </Notice>
          )}

          <Card className="overflow-hidden">
            <CardHeader
              title="Champs"
              description="Une ligne par colonne de la table. Les contraintes se posent depuis la liste au bout de chaque ligne."
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
                  Ajouter un champ
                </Button>
              </div>
            )}
          </Card>
        </div>
      )}

      {section === 'indexes' && (
        <Panel
          title="Index"
          description="Un index unique impose l'unicité côté base : la contrainte survit aux écritures concurrentes, contrairement à une vérification faite avant l'insertion."
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
        title={`Supprimer le champ « ${removing?.name ?? ''} » ?`}
        confirmLabel="Supprimer le champ"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        message={
          <span className="flex items-start gap-2.5">
            <ShieldAlert size={18} className="mt-0.5 shrink-0 text-danger" aria-hidden="true" />
            <span>
              La colonne sera retirée de la table à l'enregistrement et{' '}
              <strong className="font-semibold text-ink">toutes ses données seront perdues</strong>,
              sans possibilité de retour. Pour renommer le champ, modifiez son nom : les données
              suivent.
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
        title={`Supprimer la collection « ${collection?.name ?? ''} » ?`}
        message="La table et toutes ses données seront détruites. L'opération est définitive et ne peut pas être annulée."
        confirmLabel="Supprimer la collection"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        onConfirm={() => void drop()}
        onClose={() => setConfirmingDrop(false)}
      />
    </div>
  )
}

/* ------------------------------------------------------------------ Général */

/**
 * Configuration de la collection et indicateurs de santé.
 *
 * Les indicateurs portent sur le brouillon : ouvrir une règle à tous fait virer la tuile au rouge
 * avant même d'enregistrer, ce qui est le moment où l'information sert.
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
        description="Le schéma est une donnée : créer une collection crée sa table et expose son API CRUD immédiatement."
      >
        <div className="grid gap-4 sm:grid-cols-2">
          <Field
            label="Nom"
            required
            hint={
              isNew
                ? 'Devient le nom de la table. Lettres, chiffres, soulignés.'
                : 'Figé : la table porte déjà ce nom, et le changer reviendrait à en créer une autre.'
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
            label="Nature"
            hint={
              isNew
                ? "« Auth » ajoute l'adresse, le mot de passe et les jetons de session."
                : 'Figée : les champs système posés à la création en dépendent.'
            }
          >
            <Select<CollectionKind>
              value={draft.kind}
              disabled={!isNew}
              options={[
                { value: 'Base', label: 'Base — données', hint: 'Table simple, sans compte.' },
                {
                  value: 'Auth',
                  label: 'Auth — comptes',
                  hint: 'Adresse, mot de passe et jetons de session.',
                },
              ]}
              onChange={(kind) => onChange({ kind })}
            />
          </Field>
        </div>
      </Panel>

      <section className="space-y-3">
        <div>
          <h2 className="text-sm font-semibold text-ink">Indicateurs</h2>
          <p className="mt-0.5 text-xs text-ink-muted">
            Calculés sur le brouillon en cours. Chaque tuile mène à la vue concernée.
          </p>
        </div>

        <StatGrid>
          <Stat
            label="Règles ouvertes à tous"
            value={`${rules.open.length} / ${total}`}
            tone={rules.open.length > 0 ? 'danger' : 'success'}
            icon={<Globe size={15} aria-hidden="true" />}
            hint={
              rules.open.length === 0
                ? 'Aucune action accessible sans jeton.'
                : rules.openWrites.length > 0
                  ? `Dont ${rules.openWrites.length} en écriture : ${rules.openWrites.map((action) => RULE_LABELS[action]).join(', ')}.`
                  : `Lecture seule : ${rules.openReads.map((action) => RULE_LABELS[action]).join(', ')}.`
            }
            onClick={() => onNavigate('rules')}
            actionLabel="Voir les règles d'accès"
          />

          <Stat
            label="Règles verrouillées"
            value={`${rules.locked.length} / ${total}`}
            icon={<Lock size={15} aria-hidden="true" />}
            hint="Superadministrateur uniquement."
            onClick={() => onNavigate('rules')}
            actionLabel="Voir les règles d'accès"
          />

          <Stat
            label="Règles conditionnelles"
            value={`${rules.conditional.length} / ${total}`}
            tone="brand"
            icon={<SlidersHorizontal size={15} aria-hidden="true" />}
            hint="Autorisées quand leur expression est vraie."
            onClick={() => onNavigate('rules')}
            actionLabel="Voir les règles d'accès"
          />

          <Stat
            label="Problèmes d'index"
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
                ? `${draft.indexes.length} index déclaré${draft.indexes.length > 1 ? 's' : ''}, aucun défaut détecté.`
                : 'Détail sous les indicateurs.'
            }
            onClick={() => onNavigate('indexes')}
            actionLabel="Voir les index"
          />

          <Stat
            label="Champs"
            value={systemFields.length + draft.fields.length}
            icon={<Columns3 size={15} aria-hidden="true" />}
            hint={`Dont ${systemFields.length} posé${systemFields.length > 1 ? 's' : ''} par le moteur et ${required} obligatoire${required > 1 ? 's' : ''}.`}
            onClick={() => onNavigate('fields')}
            actionLabel="Voir les champs"
          />

          <Stat
            label="Enregistrements"
            value={records === null ? '—' : formatCount(records)}
            icon={<Database size={15} aria-hidden="true" />}
            hint={
              isNew
                ? 'La table sera créée à l’enregistrement.'
                : records === null
                  ? 'Nombre indisponible.'
                  : 'Lignes actuellement en base.'
            }
            onClick={isNew ? undefined : () => onNavigate('records')}
            actionLabel="Voir les enregistrements"
          />
        </StatGrid>

        {rules.open.length > 0 && (
          <div
            role="status"
            className="rounded-[var(--radius-card)] border border-danger/40 bg-danger-subtle px-4 py-3"
          >
            <p className="flex items-center gap-2 text-sm font-semibold text-ink">
              <Globe size={15} className="shrink-0 text-danger" aria-hidden="true" />
              Actions autorisées sans aucun jeton
            </p>
            <ul className="mt-2 space-y-1">
              {rules.open.map((action) => (
                <li key={action} className="flex flex-wrap items-center gap-2 text-xs text-ink">
                  <Badge tone={rules.openWrites.includes(action) ? 'danger' : 'warning'}>
                    {RULE_LABELS[action]}
                  </Badge>
                  {rules.openWrites.includes(action)
                    ? "N'importe quel visiteur anonyme peut modifier la base par cette action."
                    : "N'importe quel visiteur anonyme peut lire ces données."}
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
              <p className="text-sm font-semibold text-ink">Supprimer la collection</p>
              <p className="mt-0.5 text-xs text-ink-muted">
                La table et toutes ses données sont détruites. L'opération est définitive.
              </p>
            </div>
            <Button
              variant="danger"
              icon={<Trash2 size={15} aria-hidden="true" />}
              onClick={onRequestDrop}
            >
              Supprimer « {collection.name} »
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
        title="Anomalies d'indexation"
        description="Aucune n'empêche d'enregistrer : ce sont des index qui ne serviront pas, ou des requêtes qui balayeront la table."
        action={
          <Button size="sm" onClick={onNavigate}>
            Ouvrir les index
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
 * Nombre d'enregistrements de la collection.
 *
 * Une page d'un seul élément suffit : c'est `totalItems` qu'on veut, pas les lignes. L'échec est
 * avalé — un compteur indisponible ne doit pas transformer l'écran de configuration en écran
 * d'erreur.
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

/* --------------------------------------------------------------- Tableau des champs */

/**
 * Schéma en grille : une ligne par champ, une colonne par aspect.
 *
 * C'est la forme dans laquelle on conçoit réellement un schéma — on compare les champs entre eux,
 * on repère d'un coup d'œil ceux qui sont obligatoires, ceux qui portent des contraintes. Une
 * carte dépliée par champ obligeait à faire défiler pour répondre à ces questions.
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
    <Table bare caption={`Champs de la collection ${collectionName}`}>
      <THead>
        <tr>
          {!readOnly && (
            <Th className="w-14">
              <span className="sr-only">Ordre</span>
            </Th>
          )}
          <Th className="min-w-56">Champ</Th>
          <Th className="w-44">Type</Th>
          <Th className="w-24 text-center">Obligatoire</Th>
          <Th className="min-w-72">Contraintes</Th>
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
              Aucun champ déclaré. Le moteur posera tout de même{' '}
              <code className="font-mono">id</code>, <code className="font-mono">created</code> et{' '}
              <code className="font-mono">updated</code>.
            </td>
          </tr>
        )}
      </TBody>
    </Table>
  )
}

/**
 * Champ posé par le moteur.
 *
 * Affiché parce qu'il fait partie du schéma réel — le masquer laisse croire que la table n'a que
 * les colonnes déclarées ici. Non modifiable parce qu'il n'appartient pas au projet.
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
          <Badge>système</Badge>
        </span>
      </Td>

      <Td className="text-xs text-ink-muted">{typeLabel(field.type)}</Td>

      <Td className="text-center text-xs text-ink-muted">
        {field.required ? 'oui' : '—'}
        <span className="sr-only">{field.required ? ' — obligatoire' : ' — facultatif'}</span>
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
 * État d'identité d'un champ, réduit à un pictogramme.
 *
 * L'identifiant et son explication tenaient une seconde ligne sous le nom, sur chaque ligne du
 * tableau : la hauteur doublait et on ne voyait plus que trois ou quatre champs à la fois — dans un
 * écran dont l'intérêt est justement de comparer les champs d'un coup d'œil.
 *
 * Le pictogramme change de forme et de ton selon l'état, donc le signal reste visible sans lire :
 * un champ renommé se repère au crayon vert, un champ nouveau au plus. Seul le détail passe dans
 * l'infobulle, atteignable au survol comme au clavier.
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
        summary: 'Champ nouveau',
        detail: "Aucun identifiant : le serveur créera la colonne à l'enregistrement.",
      }
    : renamed
      ? {
          icon: <Pencil size={13} aria-hidden="true" />,
          tone: 'text-success',
          summary: `Renommé depuis « ${field.originalName} »`,
          detail:
            "L'identifiant est conservé, donc la colonne est renommée et les données restent en place.",
        }
      : {
          icon: <CircleHelp size={13} aria-hidden="true" />,
          tone: 'text-ink-faint',
          summary: `Identifiant ${field.id.slice(0, 8)}`,
          detail:
            "Renvoyé tel quel au serveur : c'est lui qui distingue un renommage d'un remplacement.",
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
        // Un bouton, et non une icône décorative : c'est la seule forme qui reçoive le focus, donc
        // la seule par laquelle l'infobulle s'ouvre au clavier.
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
  const label = field.name || `champ ${index + 1}`

  // Un champ existant peut porter un type que la console ne propose pas encore à la création
  // (fichier, date automatique). Sans son option, la liste afficherait le premier type venu et
  // ferait croire à une conversion qui n'a pas eu lieu.
  const types = CREATABLE_TYPES.includes(field.type)
    ? CREATABLE_TYPES
    : [field.type, ...CREATABLE_TYPES]

  const renamedHere = field.id !== '' && field.name !== field.originalName

  return (
    <Tr className={cn(error && 'bg-danger-subtle/60')}>
      {!readOnly && (
        <Td>
          {/* Les deux flèches côte à côte et non l'une sur l'autre : empilées, elles faisaient à
              elles seules 48 px et fixaient la hauteur de toute la ligne, au-dessus des 32 px des
              contrôles voisins. */}
          <div className="flex items-center">
            <Button
              variant="ghost"
              size="icon"
              className="size-6"
              aria-label={`Remonter le ${label}`}
              disabled={index === 0}
              onClick={() => onMove(-1)}
            >
              <ArrowUp size={13} aria-hidden="true" />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              className="size-6"
              aria-label={`Descendre le ${label}`}
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
            placeholder="nom_du_champ"
            disabled={readOnly}
            aria-label={`Nom du ${label}`}
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
          aria-label={`Type du ${label}`}
          options={types.map((type) => ({ value: type, label: typeLabel(type) }))}
          onChange={onChangeType}
        />
      </Td>

      <Td className="text-center">
        <input
          type="checkbox"
          checked={field.required}
          disabled={readOnly}
          aria-label={`${label} obligatoire`}
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
            aria-label={`Supprimer le ${label}`}
            onClick={onRemove}
          >
            <Trash2 size={15} aria-hidden="true" />
          </Button>
        </Td>
      )}
    </Tr>
  )
}

/** Contraintes d'un champ : des jetons retirables, plus la liste de celles qui restent à poser. */
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
    return <span className="text-xs text-ink-faint">Aucune contrainte pour ce type.</span>
  }

  return (
    <div className="flex items-center gap-1.5">
      {/* Les jetons défilent latéralement au lieu de passer à la ligne : un champ « Fichier » en
          porte cinq, et les empiler faisait une ligne quatre fois plus haute que celle du champ
          voisin — dans un tableau dont l'intérêt est de comparer les champs entre eux. */}
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

      {/* Hors de la zone défilante : l'ajout doit rester visible même quand les jetons débordent. */}
      {!readOnly && available.length > 0 && (
        <SelectMenu<string>
          value=""
          size="sm"
          placeholder="+ contrainte"
          aria-label={`Ajouter une contrainte au ${label}`}
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
  const controlLabel = `${constraint.label} du ${fieldLabel}`
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
          aria-label={`Retirer la contrainte « ${constraint.label} » du ${fieldLabel}`}
          onClick={onClear}
          className="rounded-full p-0.5 text-ink-faint transition-colors hover:text-danger"
        >
          <X size={11} aria-hidden="true" />
        </button>
      )}
    </span>
  )
}

/* --------------------------------------------------------------------- Index */

/**
 * Nom d'index déduit de la collection et des champs indexés, dans leur ordre.
 *
 * L'ordre fait partie du nom parce qu'il fait partie de l'index : sur `(a, b)`, une requête filtrant
 * `b` seul n'en tire rien. Deux index de mêmes champs dans deux ordres sont deux objets distincts, et
 * leurs noms doivent l'être aussi.
 */
function suggestIndexName(collectionName: string, fields: string[]): string {
  if (fields.length === 0) return ''

  const sanitise = (value: string) => value.replace(/[^A-Za-z0-9]+/g, '_').replace(/^_+|_+$/g, '')
  const proposed = ['idx', sanitise(collectionName), ...fields.map(sanitise)]
    .filter(Boolean)
    .join('_')

  // PostgreSQL tronque les identifiants à 63 octets : au-delà, deux index aux noms voisins
  // deviendraient le même objet, et la création du second échouerait pour une raison illisible.
  return proposed.length <= 63 ? proposed : proposed.slice(0, 63).replace(/_+$/, '')
}

/**
 * Éditeur d'index.
 *
 * Les champs se choisissent dans une liste, jamais en les tapant : un index porte sur des colonnes
 * qui existent, et une saisie libre laissait écrire un nom approximatif que seule la base rejetterait,
 * à l'enregistrement, sans dire lequel des index était en cause.
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
   * Applique une nouvelle liste de champs et rafraîchit le nom s'il n'a pas été saisi à la main.
   *
   * « À la main » se déduit plutôt que se retient : un nom vaut auto s'il est vide ou s'il coïncide
   * avec ce que la composition précédente aurait produit. Pas de drapeau à tenir à jour, donc pas de
   * drapeau à désynchroniser.
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
        <p className="text-xs text-ink-muted">Aucun index déclaré sur cette collection.</p>
      )}

      {indexes.map((index, position) => {
        const available = fieldNames.filter((name) => !index.fields.includes(name))

        return (
          <div
            key={position}
            className="grid gap-3 rounded-[var(--radius-card)] border border-border-subtle p-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)_auto_auto]"
          >
            <Field
              label="Champs indexés"
              hint={
                index.fields.length > 1
                  ? "L'ordre compte : un filtre portant sur le seul second champ n'utilise pas l'index."
                  : 'Choisis dans les champs de la collection.'
              }
            >
              <FieldTokens
                tokens={index.fields}
                available={available}
                readOnly={readOnly}
                label={`Champs de l'index ${index.name || position + 1}`}
                onChange={(fields) => changeFields(position, fields)}
              />
            </Field>

            <Field
              label="Nom"
              hint={
                index.name === suggestIndexName(collectionName, index.fields)
                  ? 'Déduit des champs. Saisir un nom fige celui-ci.'
                  : 'Saisi à la main.'
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
                aria-label={`Supprimer l'index ${index.name || position + 1}`}
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
          Ajouter un index
        </Button>
      )}
    </div>
  )
}

/**
 * Suite ordonnée de champs, présentée comme des jetons dans une zone de saisie.
 *
 * Le conteneur imite un champ de texte — bordure, fond, anneau de focus — mais n'accepte aucune
 * frappe : on ajoute par la liste déroulante à droite, on retire par la croix. C'est ce qui garantit
 * que les champs indexés existent, sans avoir à valider une chaîne après coup.
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
        <span className="px-0.5 text-xs text-ink-faint">Aucun champ sélectionné</span>
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
                aria-label={`Avancer ${name}`}
                disabled={position === 0}
                onClick={() => move(position, -1)}
                className="inline-flex size-4 items-center justify-center rounded-full text-ink-muted hover:text-ink disabled:opacity-30 focus-visible:-outline-offset-1"
              >
                <ChevronLeft size={11} aria-hidden="true" />
              </button>
              <button
                type="button"
                aria-label={`Reculer ${name}`}
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
              aria-label={`Retirer ${name}`}
              onClick={() => onChange(tokens.filter((entry) => entry !== name))}
              className="inline-flex size-4 items-center justify-center rounded-full text-ink-muted hover:bg-danger hover:text-danger-ink focus-visible:-outline-offset-1"
            >
              <X size={11} aria-hidden="true" />
            </button>
          )}
        </span>
      ))}

      {!readOnly && available.length > 0 && (
        // La liste revient toujours à sa valeur neutre : elle sert à ajouter, pas à représenter un
        // choix courant — ce sont les jetons qui portent l'état.
        <SelectMenu<string>
          value=""
          size="sm"
          placeholder="+ champ"
          aria-label={`Ajouter un champ à ${label}`}
          className="ml-auto h-6 w-auto border-0 bg-transparent px-1.5 text-xs text-ink-muted hover:text-brand focus-visible:-outline-offset-1"
          options={available.map((name) => ({ value: name, label: name }))}
          onChange={(name) => onChange([...tokens, name])}
        />
      )}
    </div>
  )
}
