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
  /** Chasse fixe : identifiants, permissions, noms de colonnes. */
  mono?: boolean
  /** Pastille de couleur, pour un état binaire que la seule teinte ne suffirait pas à porter. */
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
      <span className="truncate">{children}</span>
    </span>
  )
}
