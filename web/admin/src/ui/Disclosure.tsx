import { useId, type ReactNode } from 'react'
import { ChevronDown } from 'lucide-react'
import { Card } from './Card'
import { cn } from './utils'

/**
 * Collapsible section.
 *
 * Used to lay out several panels of a single form on one page rather than behind a second level
 * of tabs: the input state stays unified, and a warning shown in one section remains visible
 * while working in another.
 *
 * Openness belongs to the caller: it's often the content that decides — a section carrying an
 * alert should open itself.
 */
export function Disclosure({
  title,
  description,
  badge,
  actions,
  open,
  onToggle,
  className,
  children,
}: {
  title: ReactNode
  description?: ReactNode
  badge?: ReactNode
  /** Section-specific actions, rendered outside the button to stay reachable by keyboard. */
  actions?: ReactNode
  open: boolean
  onToggle: () => void
  className?: string
  children: ReactNode
}) {
  const panelId = useId()

  return (
    <Card className={cn('overflow-hidden', className)}>
      <div
        className={cn(
          'flex items-center gap-3 pr-4',
          open && 'border-b border-border-subtle bg-surface-sunken',
        )}
      >
        {/* The button is wrapped in a heading: it's what makes the section a step in a screen
            reader's table of contents, rather than a button lost in the flow. */}
        <h2 className="min-w-0 flex-1">
          <button
            type="button"
            onClick={onToggle}
            aria-expanded={open}
            // Without `aria-controls`, a screen reader announces an "expanded" button without
            // ever saying what it expands.
            aria-controls={panelId}
            className="flex w-full items-center gap-2.5 px-5 py-4 text-left"
          >
            <ChevronDown
              size={16}
              aria-hidden="true"
              className={cn('shrink-0 text-ink-faint transition-transform', open && 'rotate-180')}
            />
            <span className="min-w-0">
              <span className="block truncate text-sm font-semibold text-ink">{title}</span>
              {description && (
                <span className="mt-0.5 block text-xs font-normal text-ink-muted">
                  {description}
                </span>
              )}
            </span>
            {badge}
          </button>
        </h2>

        {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
      </div>

      {open && <div id={panelId}>{children}</div>}
    </Card>
  )
}
