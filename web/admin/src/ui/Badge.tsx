import type { ReactNode } from 'react'
import { cn } from './utils'

export type BadgeTone = 'neutral' | 'brand' | 'success' | 'warning' | 'danger'

const TONES: Record<BadgeTone, string> = {
  neutral: 'bg-surface-sunken text-ink-muted',
  brand: 'bg-brand-subtle text-brand',
  success: 'bg-success/15 text-success',
  warning: 'bg-warning/20 text-warning',
  danger: 'bg-danger-subtle text-danger',
}

export function Badge({
  tone = 'neutral',
  mono = false,
  dot = false,
  title,
  className,
  children,
}: {
  tone?: BadgeTone
  /** Monospace: identifiers, permissions, column names. */
  mono?: boolean
  /** Color dot, for a binary state that the tone alone wouldn't be enough to carry. */
  dot?: boolean
  title?: string
  className?: string
  children: ReactNode
}) {
  return (
    <span
      title={title}
      className={cn(
        'inline-flex max-w-full items-center gap-1 rounded-full px-2 py-0.5 text-xs leading-4 font-medium',
        mono && 'font-mono',
        TONES[tone],
        className,
      )}
    >
      {dot && <span aria-hidden="true" className="size-1.5 shrink-0 rounded-full bg-current" />}
      {/* Lucide icons are <svg> elements, which Tailwind's preamble sets to `display: block`:
          placed in front of text, they push it to a new line and the badge takes up two lines.
          They're set back inline here, once — fixing it at the call site would leave the next
          icon-bearing badge reproducing the same defect. */}
      <span className="truncate [&>svg]:me-1 [&>svg]:inline [&>svg]:align-[-0.115em]">
        {children}
      </span>
    </span>
  )
}
