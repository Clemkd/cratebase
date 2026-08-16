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
    label: 'List',
    violation: '200 with an empty list',
    note: "The rule is also a filter: it removes rows rather than refusing the call.",
  },
  {
    key: 'view',
    label: 'View',
    violation: '404',
    note: "404 rather than 403, to avoid disclosing the row's existence.",
  },
  {
    key: 'create',
    label: 'Create',
    violation: '400',
    note: 'Evaluated on the record as it will be written, after default values and hooks.',
  },
  { key: 'update', label: 'Update', violation: '404', note: "Applied in the update's WHERE clause." },
  { key: 'delete', label: 'Delete', violation: '404', note: "Applied in the delete's WHERE clause." },
  {
    key: 'manage',
    label: 'Manage',
    violation: '403',
    note: "Who can change another account's password or address.",
    authOnly: true,
  },
]

const STATES: Record<
  RuleState,
  { label: string; icon: typeof Lock; tone: 'neutral' | 'danger' | 'brand'; summary: string }
> = {
  locked: {
    label: 'Locked',
    icon: Lock,
    tone: 'neutral',
    summary: 'Superuser only — 403 for everything else.',
  },
  public: {
    label: 'Open to everyone',
    icon: Globe,
    tone: 'danger',
    summary: 'Allowed for everyone, anonymous visitors included.',
  },
  conditional: {
    label: 'Conditional',
    icon: SlidersHorizontal,
    tone: 'brand',
    summary: 'Allowed when the expression is true.',
  },
}

/** Order of the segments: from most closed to most open, then the nuanced case. */
const STATE_ORDER: RuleState[] = ['locked', 'public', 'conditional']

function stateOf(value: string | null, editing: boolean): RuleState {
  if (value === null || value === undefined) return 'locked'
  if (value !== '') return 'conditional'

  // An expression cleared mid-typing keeps displaying as conditional, with its warning:
  // switching the screen to "open to everyone" on the last character deleted would make the
  // collection's total exposure look like a typo.
  return editing ? 'conditional' : 'public'
}

/**
 * Access rules editor.
 *
 * The screen must make visible the distinction that decides everything: an **absent** rule locks
 * the collection to the superuser, an **empty** rule opens it to everyone. A plain text box
 * doesn't show the difference — hence three named, mutually exclusive states rather than a field
 * left blank.
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
  // The count comes from the shared diagnostic: the "General" tab's indicator and this screen's
  // warning must always report the same number.
  const publicCount = analyseRules(kind, rules).open.length

  // Rules the user has explicitly set to conditional mode during this editing session. Only
  // serves to avoid requalifying an expression that got cleared by typing.
  const [editing, setEditing] = useState<RuleAction[]>([])

  const setState = (key: RuleAction, state: RuleState) => {
    setEditing((current) =>
      state === 'conditional'
        ? [...current.filter((item) => item !== key), key]
        : current.filter((item) => item !== key),
    )

    if (state === 'locked') return onChange({ ...rules, [key]: null })
    if (state === 'public') return onChange({ ...rules, [key]: '' })

    // Switching to conditional: seeded with the authentication check, the most common idiom,
    // rather than an empty string which would mean "open to everyone".
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
              {publicCount} rule{publicCount > 1 ? 's' : ''} open to everyone.
            </strong>{' '}
            Anyone, without a token, can perform these actions on this collection.
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
                <Badge>denied → {action.violation}</Badge>
              </div>

              {readOnly ? null : (
                <SegmentedControl<RuleState>
                  label={`State of the "${action.label}" rule`}
                  value={state}
                  onChange={(next) => setState(action.key, next)}
                  // Each state carries its own icon: the lock, the globe, and the sliders convey
                  // openness before the word is even read, and they're the same glyphs as the
                  // header row just above — a single code to learn for the whole screen.
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
                  aria-label={`Expression for the "${action.label}" rule`}
                  aria-invalid={emptyExpression}
                  disabled={readOnly}
                  className="mt-2.5 font-mono text-xs"
                  value={value ?? ''}
                  spellCheck={false}
                  placeholder="e.g. owner = @request.auth.id"
                  onChange={(event) => onChange({ ...rules, [action.key]: event.target.value })}
                />

                {emptyExpression && (
                  <p role="alert" className="mt-1.5 text-xs font-medium text-danger">
                    Empty expression: saved as-is, this rule opens the action to everyone,
                    anonymous visitors included.
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
