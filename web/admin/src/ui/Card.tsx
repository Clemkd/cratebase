import type { HTMLAttributes, ReactNode } from 'react'
import { cn } from './utils'

/** Elevated surface. Without a header, it contains only what the caller puts in it. */
export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        'rounded-[var(--radius-card)] border border-border-subtle bg-surface shadow-card',
        className,
      )}
      {...props}
    />
  )
}

/** Card header: title, subtitle, actions aligned to the right. */
export function CardHeader({
  title,
  description,
  action,
}: {
  title?: ReactNode
  description?: ReactNode
  action?: ReactNode
}) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-border-subtle px-5 py-4">
      <div className="min-w-0">
        {/* No empty title: an `h2` with no text adds a structural landmark that leads nowhere
            for a screen reader. */}
        {title && <h2 className="truncate text-sm font-semibold text-ink">{title}</h2>}
        {description && <p className="mt-0.5 text-xs text-ink-muted">{description}</p>}
      </div>
      {action && <div className="flex shrink-0 flex-wrap items-center gap-2">{action}</div>}
    </div>
  )
}

/**
 * Titled card: the `Card` + `CardHeader` duo, with body padding.
 *
 * Most sections of the console have a title and a body; composing them by hand every time would
 * let the spacing drift from one screen to another.
 */
export function Panel({
  title,
  description,
  actions,
  padded = true,
  className,
  bodyClassName,
  children,
}: {
  title?: ReactNode
  description?: ReactNode
  actions?: ReactNode
  padded?: boolean
  className?: string
  bodyClassName?: string
  children?: ReactNode
}) {
  return (
    <Card className={cn('overflow-hidden', className)}>
      {(title || actions) && (
        <CardHeader title={title} description={description} action={actions} />
      )}
      {children && <div className={cn(padded && 'px-5 py-4', bodyClassName)}>{children}</div>}
    </Card>
  )
}
