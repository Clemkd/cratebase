import type { HTMLAttributes, ReactNode } from 'react'
import { cn } from './utils'

/** Surface élevée. Sans en-tête, elle ne contient que ce que l'appelant y met. */
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

/** En-tête de carte : titre, sous-titre, actions alignées à droite. */
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
        {/* Pas de titre vide : un `h2` sans texte ajoute un repère de structure qui ne mène nulle
            part chez un lecteur d'écran. */}
        {title && <h2 className="truncate text-sm font-semibold text-ink">{title}</h2>}
        {description && <p className="mt-0.5 text-xs text-ink-muted">{description}</p>}
      </div>
      {action && <div className="flex shrink-0 flex-wrap items-center gap-2">{action}</div>}
    </div>
  )
}

/**
 * Carte titrée : le duo `Card` + `CardHeader`, avec le rembourrage du corps.
 *
 * La plupart des sections de la console ont un titre et un corps ; les composer à la main à chaque
 * fois laisserait dériver les espacements d'un écran à l'autre.
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
