import { X } from 'lucide-react'
import type { Collection } from '../api'
import { Button, Dialog } from '../ui'

interface Entry {
  syntax: string
  description: string
}

const OPERATORS: Entry[] = [
  { syntax: '=', description: 'equal' },
  { syntax: '!=', description: 'not equal' },
  { syntax: '>  >=  <  <=', description: 'comparison, including on dates' },
  { syntax: '~', description: 'contains — case-insensitive' },
  { syntax: '!~', description: 'does not contain' },
  { syntax: '?=  ?~  ?>  …', description: 'on a multi-value field: "at least one element" instead of "all"' },
  { syntax: '&&  ||', description: 'logical and, logical or — parentheses allowed' },
  { syntax: '// comment', description: 'end-of-line comment' },
]

const MODIFIERS: Entry[] = [
  { syntax: ':isset', description: 'was the field submitted? — reserved for @request.* paths' },
  { syntax: ':length', description: 'number of elements in a multi-value field' },
  { syntax: ':each', description: 'the condition applies to every element' },
  { syntax: ':lower', description: 'lowercase comparison' },
  { syntax: ':changed', description: 'was the field submitted and changed?' },
]

const REQUEST: Entry[] = [
  { syntax: '@request.auth.id', description: 'caller identifier — empty string if anonymous' },
  { syntax: '@request.auth.collectionName', description: "caller's auth collection" },
  { syntax: '@request.auth.<field>', description: 'any field of the signed-in account' },
  { syntax: '@request.method', description: 'HTTP verb' },
  { syntax: '@request.context', description: 'default, oauth2, otp, password, realtime, protectedFile' },
  { syntax: '@request.body.<field>', description: 'submitted value, after default values and hooks' },
  { syntax: '@request.query.<key>', description: 'query parameter' },
  { syntax: '@request.headers.x_forwarded_for', description: 'header, lowercased with dashes replaced by underscores' },
]

const MACROS: Entry[] = [
  { syntax: '@now  @yesterday  @tomorrow', description: 'relative instants, normalized to UTC' },
  { syntax: '@todayStart  @todayEnd', description: "bounds of today" },
  { syntax: '@monthStart  @monthEnd', description: 'bounds of the current month' },
  { syntax: '@yearStart  @yearEnd', description: 'bounds of the current year' },
  { syntax: '@second @minute @hour @day @month @year', description: 'numeric components of the current instant' },
]

function Section({ title, entries }: { title: string; entries: Entry[] }) {
  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold tracking-wide text-ink-muted uppercase">{title}</h3>
      <dl className="divide-y divide-border-subtle rounded-[var(--radius-card)] border border-border-subtle">
        {entries.map((entry) => (
          <div key={entry.syntax} className="flex flex-col gap-1 px-4 py-2.5 sm:flex-row sm:gap-4">
            <dt className="shrink-0 font-mono text-xs text-brand sm:w-56">{entry.syntax}</dt>
            <dd className="text-xs text-ink-muted">{entry.description}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}

/** Contextual help on filter syntax, with examples built on the open collection. */
export function FilterHelp({
  open,
  collection,
  onClose,
  onUseExample,
}: {
  open: boolean
  collection: Collection
  onClose: () => void
  onUseExample: (expression: string) => void
}) {
  const sample = collection.fields.find((field) => !field.hidden && !field.isSystem)
  const textual = collection.fields.find(
    (field) => !field.hidden && !field.isSystem && (field.type === 'Text' || field.type === 'Email'),
  )

  const examples = [
    `created >= @todayStart`,
    textual ? `${textual.name} ~ 'example'` : `id != ''`,
    sample?.multiple ? `${sample.name}:length > 0` : `created < @monthStart && created >= @yearStart`,
    `@request.auth.id != ''`,
  ]

  return (
    <Dialog
      open={open}
      onClose={onClose}
      width="lg"
      title="Filter syntax"
      description="The same language serves the ?filter= parameter and access rules."
      footer={
        <Button variant="outline" icon={<X size={15} aria-hidden="true" />} onClick={onClose}>
          Close
        </Button>
      }
    >
      <div className="space-y-6">
        <section>
          <h3 className="mb-2 text-xs font-semibold tracking-wide text-ink-muted uppercase">
            Examples — click to apply
          </h3>
          <div className="flex flex-wrap gap-2">
            {examples.map((example) => (
              <button
                key={example}
                type="button"
                onClick={() => {
                  onUseExample(example)
                  onClose()
                }}
                className="rounded-[var(--radius-control)] border border-border-subtle bg-surface-sunken px-2.5 py-1.5 font-mono text-xs text-ink transition-colors hover:border-brand hover:text-brand"
              >
                {example}
              </button>
            ))}
          </div>
        </section>

        <Section title="Operators" entries={OPERATORS} />
        <Section title="Path modifiers" entries={MODIFIERS} />
        <Section title="Request meta-fields" entries={REQUEST} />
        <Section title="Date macros" entries={MACROS} />

        <p className="text-xs text-ink-muted">
          An identifier absent from the schema is rejected with a 400 error, never interpolated:
          that's the engine's injection boundary. Macros are resolved on the application side and
          become parameters, so they yield the same value on SQLite and on PostgreSQL.
        </p>
      </div>
    </Dialog>
  )
}
