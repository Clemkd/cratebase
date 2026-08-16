import { useId, useState, type ReactNode } from 'react'
import { cn } from './utils'

/**
 * Tooltip on hover and keyboard focus.
 *
 * It stays in the DOM permanently: `hidden` or `display: none` would remove it from the
 * accessibility tree, so `aria-describedby` would no longer point to anything for a screen reader.
 *
 * At rest it's collapsed the way `sr-only` does, rather than made transparent: a transparent
 * bubble still occupies its real size, and those of buttons hugging the right edge — top bar,
 * a table's actions column — widened the document by a few dozen pixels.
 */
export function Tooltip({
  content,
  side = 'top',
  children,
  className,
}: {
  content: ReactNode
  side?: 'top' | 'bottom'
  children: ReactNode
  className?: string
}) {
  const id = useId()
  const [open, setOpen] = useState(false)

  return (
    <span
      className={cn('relative inline-flex', className)}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onFocus={() => setOpen(true)}
      onBlur={() => setOpen(false)}
    >
      <span aria-describedby={id} className="inline-flex">
        {children}
      </span>

      <span
        id={id}
        role="tooltip"
        className={
          open
            ? cn(
                'pointer-events-none absolute left-1/2 z-50 w-max max-w-[min(16rem,80vw)] -translate-x-1/2',
                'rounded-[var(--radius-control)] border border-border-subtle bg-surface-raised px-2 py-1',
                'text-xs font-normal text-ink shadow-popover',
                side === 'top' ? 'bottom-[calc(100%+6px)]' : 'top-[calc(100%+6px)]',
              )
            : 'sr-only'
        }
      >
        {content}
      </span>
    </span>
  )
}
