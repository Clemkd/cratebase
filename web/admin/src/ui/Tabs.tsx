import { useRef, type ReactNode } from 'react'
import { cn } from './utils'

export interface TabDefinition<T extends string> {
  id: T
  label: ReactNode
  badge?: ReactNode
}

const tabId = (name: string, id: string) => `tab-${name}-${id}`
const panelId = (name: string, id: string) => `panel-${name}-${id}`

/**
 * Tabs conforming to the ARIA pattern: arrow-key navigation, `aria-controls` pointing to the
 * panel, only one tab in the tab order.
 *
 * `name` must be unique in the page: it links each tab to its `TabPanel`, which is rendered
 * elsewhere in the tree and therefore can't inherit an internal identifier.
 */
export function Tabs<T extends string>({
  name,
  tabs,
  active,
  onChange,
  label,
  className,
}: {
  name: string
  tabs: TabDefinition<T>[]
  active: T
  onChange: (id: T) => void
  label: string
  className?: string
}) {
  const listRef = useRef<HTMLDivElement>(null)

  const move = (offset: number) => {
    const index = tabs.findIndex((tab) => tab.id === active)
    const next = tabs[(index + offset + tabs.length) % tabs.length]

    if (!next) return

    onChange(next.id)
    listRef.current
      ?.querySelector<HTMLButtonElement>(`#${CSS.escape(tabId(name, next.id))}`)
      ?.focus()
  }

  return (
    <div
      ref={listRef}
      role="tablist"
      aria-label={label}
      className={cn(
        // `overflow-y-hidden` explicitly: as soon as one axis stops being `visible`, the other
        // defaults to `auto`, and the tab bar ends up with a vertical scrollbar for a few pixels
        // of focus ring. The tabs' content, meanwhile, scrolls normally.
        //
        // `pb-px` compensates for the tabs' `-mb-px`: without this pixel of inner margin, the
        // active tab's underline overflows the fill box and gets clipped by the masking.
        'flex items-center gap-1 overflow-x-auto overflow-y-hidden pb-px border-b border-border-subtle',
        className,
      )}
      onKeyDown={(event) => {
        if (event.key === 'ArrowRight') {
          event.preventDefault()
          move(1)
        } else if (event.key === 'ArrowLeft') {
          event.preventDefault()
          move(-1)
        }
      }}
    >
      {tabs.map((tab) => (
        <button
          key={tab.id}
          id={tabId(name, tab.id)}
          type="button"
          role="tab"
          aria-selected={tab.id === active}
          aria-controls={panelId(name, tab.id)}
          tabIndex={tab.id === active ? 0 : -1}
          onClick={() => onChange(tab.id)}
          className={cn(
            '-mb-px inline-flex shrink-0 items-center gap-2 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors',
            // Focus ring drawn inside the button: since the bar hides its vertical overflow, a
            // ring placed outside would be clipped — and the tab reached by keyboard would
            // become invisible.
            'focus-visible:-outline-offset-2',
            tab.id === active
              ? 'border-brand text-ink'
              : 'border-transparent text-ink-muted hover:text-ink',
          )}
        >
          {tab.label}
          {tab.badge}
        </button>
      ))}
    </div>
  )
}

export function TabPanel<T extends string>({
  name,
  id,
  active,
  className,
  children,
}: {
  name: string
  id: T
  active: T
  className?: string
  children: ReactNode
}) {
  if (id !== active) return null

  return (
    <div
      id={panelId(name, id)}
      role="tabpanel"
      aria-labelledby={tabId(name, id)}
      // The panel is reachable by keyboard — that's where the ARIA pattern sends the user after
      // the tabs. Its focus ring therefore stays visible: removing it would make the cursor
      // disappear at the exact moment it enters the content.
      tabIndex={0}
      className={cn('rounded-[var(--radius-control)] outline-offset-4', className)}
    >
      {children}
    </div>
  )
}
