import { useState } from 'react'
import { Globe, Lock, SlidersHorizontal, TriangleAlert } from 'lucide-react'
import type { AccessRules, Collection, RuleAction } from '../api'
import { Badge, SegmentedControl, Textarea, cn } from '../ui'
import { analyseRules } from './CollectionHealth'

type RuleState = 'locked' | 'public' | 'conditional'

interface RuleDescriptor {
  key: RuleAction
  label: string
  violation: string
  note: string
  authOnly?: boolean
}

const ACTIONS: RuleDescriptor[] = [
  {
    key: 'list',
    label: 'Lister',
    violation: '200 avec une liste vide',
    note: "La règle est aussi un filtre : elle retire les lignes plutôt que de refuser l'appel.",
  },
  {
    key: 'view',
    label: 'Consulter',
    violation: '404',
    note: "404 plutôt que 403, pour ne pas divulguer l'existence de la ligne.",
  },
  {
    key: 'create',
    label: 'Créer',
    violation: '400',
    note: "Évaluée sur l'enregistrement tel qu'il sera écrit, après valeurs par défaut et crochets.",
  },
  { key: 'update', label: 'Modifier', violation: '404', note: 'Appliquée dans le WHERE de la mise à jour.' },
  { key: 'delete', label: 'Supprimer', violation: '404', note: 'Appliquée dans le WHERE de la suppression.' },
  {
    key: 'manage',
    label: 'Gérer',
    violation: '403',
    note: "Qui peut changer le mot de passe ou l'adresse d'un autre compte.",
    authOnly: true,
  },
]

const STATES: Record<
  RuleState,
  { label: string; icon: typeof Lock; tone: 'neutral' | 'danger' | 'brand'; summary: string }
> = {
  locked: {
    label: 'Verrouillée',
    icon: Lock,
    tone: 'neutral',
    summary: 'Superadministrateur uniquement — 403 pour tout le reste.',
  },
  public: {
    label: 'Ouverte à tous',
    icon: Globe,
    tone: 'danger',
    summary: 'Autorisée pour tout le monde, visiteurs anonymes compris.',
  },
  conditional: {
    label: 'Conditionnelle',
    icon: SlidersHorizontal,
    tone: 'brand',
    summary: "Autorisée quand l'expression est vraie.",
  },
}

/** Ordre des segments : du plus fermé au plus ouvert, puis le cas nuancé. */
const STATE_ORDER: RuleState[] = ['locked', 'public', 'conditional']

function stateOf(value: string | null, editing: boolean): RuleState {
  if (value === null || value === undefined) return 'locked'
  if (value !== '') return 'conditional'

  // Une expression vidée en cours de saisie reste affichée comme conditionnelle, avec son
  // avertissement : basculer l'écran sur « ouverte à tous » à la dernière touche effacée ferait
  // passer l'ouverture totale de la collection pour un effet de frappe.
  return editing ? 'conditional' : 'public'
}

/**
 * Éditeur des règles d'accès.
 *
 * L'écran doit rendre visible la distinction qui décide de tout : une règle **absente** verrouille
 * la collection au superadministrateur, une règle **vide** l'ouvre à tout le monde. Une simple zone
 * de texte ne montre pas la différence — d'où trois états nommés et exclusifs plutôt qu'un champ
 * qu'on laisse vide.
 */
export function RulesEditor({
  kind,
  rules,
  readOnly = false,
  onChange,
}: {
  kind: Collection['kind']
  rules: AccessRules
  readOnly?: boolean
  onChange: (rules: AccessRules) => void
}) {
  const actions = ACTIONS.filter((action) => !action.authOnly || kind === 'Auth')
  // Le décompte vient du diagnostic partagé : l'indicateur de l'onglet « Général » et
  // l'avertissement de cet écran doivent toujours annoncer le même nombre.
  const publicCount = analyseRules(kind, rules).open.length

  // Règles que l'utilisateur a explicitement placées en mode conditionnel pendant cette session
  // d'édition. Sert uniquement à ne pas requalifier une expression vidée par la frappe.
  const [editing, setEditing] = useState<RuleAction[]>([])

  const setState = (key: RuleAction, state: RuleState) => {
    setEditing((current) =>
      state === 'conditional'
        ? [...current.filter((item) => item !== key), key]
        : current.filter((item) => item !== key),
    )

    if (state === 'locked') return onChange({ ...rules, [key]: null })
    if (state === 'public') return onChange({ ...rules, [key]: '' })

    // Passage en conditionnel : on amorce avec le test d'authentification, l'idiome le plus
    // courant, plutôt qu'une chaîne vide qui vaudrait « ouverte à tous ».
    onChange({ ...rules, [key]: rules[key] || "@request.auth.id != ''" })
  }

  return (
    <div className="space-y-3">
      {publicCount > 0 && (
        <div
          role="status"
          className="flex items-start gap-2.5 rounded-[var(--radius-card)] border border-danger/30 bg-danger-subtle px-4 py-3"
        >
          <TriangleAlert size={16} className="mt-0.5 shrink-0 text-danger" aria-hidden="true" />
          <p className="text-sm text-ink">
            <strong className="font-semibold">
              {publicCount} règle{publicCount > 1 ? 's' : ''} ouverte{publicCount > 1 ? 's' : ''} à
              tous.
            </strong>{' '}
            N'importe qui, sans jeton, peut effectuer ces actions sur cette collection.
          </p>
        </div>
      )}

      {actions.map((action) => {
        const value = rules[action.key]
        const state = stateOf(value, editing.includes(action.key))
        const descriptor = STATES[state]
        const Icon = descriptor.icon
        const emptyExpression = state === 'conditional' && value === ''

        return (
          <div
            key={action.key}
            className={cn(
              'rounded-[var(--radius-card)] border bg-surface p-4',
              state === 'public' || emptyExpression ? 'border-danger/40' : 'border-border-subtle',
            )}
          >
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-2">
                <Icon
                  size={14}
                  aria-hidden="true"
                  className={cn(
                    state === 'public'
                      ? 'text-danger'
                      : state === 'conditional'
                        ? 'text-brand'
                        : 'text-ink-faint',
                  )}
                />
                <span className="text-sm font-medium text-ink">{action.label}</span>
                <Badge tone={descriptor.tone}>{descriptor.label}</Badge>
                <Badge>refus → {action.violation}</Badge>
              </div>

              {readOnly ? null : (
                <SegmentedControl<RuleState>
                  label={`État de la règle « ${action.label} »`}
                  value={state}
                  onChange={(next) => setState(action.key, next)}
                  // Chaque état porte son icône : le cadenas, le globe et les curseurs disent
                  // l'ouverture avant que le mot ne soit lu, et ce sont les mêmes glyphes que la
                  // ligne d'en-tête juste au-dessus — un seul code à apprendre pour l'écran entier.
                  options={STATE_ORDER.map((value) => {
                    const meta = STATES[value]

                    return {
                      value,
                      title: meta.summary,
                      label: (
                        <>
                          <meta.icon size={13} aria-hidden="true" className="shrink-0" />
                          {meta.label}
                        </>
                      ),
                    }
                  })}
                />
              )}
            </div>

            <p
              className={cn(
                'mt-2 text-xs',
                state === 'public' ? 'font-medium text-danger' : 'text-ink-muted',
              )}
            >
              {descriptor.summary}
            </p>

            <p className="mt-0.5 text-xs text-ink-faint">{action.note}</p>

            {state === 'conditional' && (
              <>
                <Textarea
                  rows={2}
                  aria-label={`Expression de la règle « ${action.label} »`}
                  aria-invalid={emptyExpression}
                  disabled={readOnly}
                  className="mt-2.5 font-mono text-xs"
                  value={value ?? ''}
                  spellCheck={false}
                  placeholder="ex. : owner = @request.auth.id"
                  onChange={(event) => onChange({ ...rules, [action.key]: event.target.value })}
                />

                {emptyExpression && (
                  <p role="alert" className="mt-1.5 text-xs font-medium text-danger">
                    Expression vide : enregistrée telle quelle, cette règle ouvre l'action à tous,
                    visiteurs anonymes compris.
                  </p>
                )}
              </>
            )}
          </div>
        )
      })}
    </div>
  )
}
