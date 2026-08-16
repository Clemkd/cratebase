import { createContext, useContext, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { cn } from '../ui'

/**
 * Slot for actions in the page header.
 *
 * A screen rendered in `children` can drop its buttons there even though they display above it,
 * next to the title. Without this relay, the screen's state would have to be lifted up to
 * `Page`'s caller so it could derive the actions from it — the opposite of what a self-contained
 * component is for.
 *
 * The gain isn't cosmetic: an actions bar on its own line costs a full line of usable height on
 * every screen, whereas the header has free space to the right of the title.
 */
const PageActionsSlot = createContext<HTMLElement | null>(null)

/** Page container: width, margins, and header consistent across the whole console. */
export function Page({
  title,
  description,
  actions,
  meta,
  children,
  wide,
}: {
  title: ReactNode
  description?: ReactNode
  /** Page actions. Their place is here, not in the content's toolbars. */
  actions?: ReactNode
  /** Reading cues placed under the title: collection kind, volume, identifier. */
  meta?: ReactNode
  children: ReactNode
  /** Table screens claim all the available width. */
  wide?: boolean
}) {
  // The element is kept in state, not in a ref: a ref doesn't trigger a re-render, so the
  // children would never know the destination exists.
  const [slot, setSlot] = useState<HTMLElement | null>(null)

  return (
    <div className={cn('mx-auto px-4 py-6 sm:px-6', wide ? 'max-w-[1600px]' : 'max-w-5xl')}>
      <div className="mb-4 flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
        <div className="min-w-0">
          <h1 className="text-xl font-semibold tracking-tight text-ink">{title}</h1>
          {description && <p className="mt-1 text-sm text-ink-muted">{description}</p>}
          {meta && <div className="mt-2 flex flex-wrap items-center gap-2">{meta}</div>}
        </div>

        <div ref={setSlot} className="flex flex-wrap items-center gap-2">
          {actions}
        </div>
      </div>

      <PageActionsSlot value={slot}>{children}</PageActionsSlot>
    </div>
  )
}

/**
 * Drops actions into the enclosing page's header.
 *
 * Without a `Page` above, renders nothing rather than throwing: a screen must remain mountable
 * on its own, in a test for example.
 */
export function PageActions({ children }: { children: ReactNode }) {
  const slot = useContext(PageActionsSlot)

  return slot ? createPortal(children, slot) : null
}

/** Grid of numeric tiles. */
export function StatGrid({ children }: { children: ReactNode }) {
  return <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">{children}</div>
}

const STAT_TONES = {
  neutral: 'text-ink',
  brand: 'text-brand',
  success: 'text-success',
  warning: 'text-warning',
  danger: 'text-danger',
} as const

/**
 * Numeric tile.
 *
 * Clickable, it becomes a real button rather than a `div` fitted with an `onClick`: it's the
 * only form that stays reachable by tabbing and actionable with the space bar.
 */
export function Stat({
  label,
  value,
  hint,
  icon,
  tone = 'neutral',
  onClick,
  actionLabel,
}: {
  label: string
  value: ReactNode
  hint?: ReactNode
  icon?: ReactNode
  tone?: keyof typeof STAT_TONES
  onClick?: () => void
  /** Accessible name of the button, which must say where the tile leads. */
  actionLabel?: string
}) {
  const body = (
    <>
      <div className="flex items-center justify-between gap-2">
        <p className="text-xs font-medium text-ink-muted">{label}</p>
        {icon && <span className={STAT_TONES[tone]}>{icon}</span>}
      </div>
      <p className={cn('mt-1.5 text-2xl font-semibold tabular-nums', STAT_TONES[tone])}>{value}</p>
      {/* `break-all` rather than `break-words`: the cues placed here are often paths or URLs,
          which have no spaces to break on. Without it, a data directory overflows the card
          instead of wrapping. */}
      {hint && <p className="mt-0.5 text-xs break-all text-ink-muted">{hint}</p>}
    </>
  )

  const shell = cn(
    // `min-w-0`: without it, a grid tile refuses to shrink below its content's width, and it's
    // the whole grid that overflows rather than the text wrapping.
    'min-w-0 rounded-[var(--radius-card)] border bg-surface px-4 py-3.5 text-left shadow-card',
    tone === 'danger' ? 'border-danger/40' : 'border-border-subtle',
  )

  if (!onClick) return <div className={shell}>{body}</div>

  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={actionLabel}
      className={cn(shell, 'w-full transition-colors hover:border-brand')}
    >
      {body}
    </button>
  )
}
