import type { ReactNode } from 'react'
import { Boxes } from 'lucide-react'

/**
 * Frame for out-of-session screens.
 *
 * The product identity and the form fit on a single surface: separating them — a logo floating
 * above a card — makes two blocks where the user only has one thing to do.
 */
export function AuthLayout({
  title,
  description,
  children,
  footer,
  aside,
}: {
  title: string
  description?: string
  children: ReactNode
  footer?: ReactNode
  /** Secondary control placed at the top of the screen, outside the card: theme toggle. */
  aside?: ReactNode
}) {
  return (
    <div className="flex min-h-dvh flex-col bg-canvas">
      {aside && <div className="flex justify-end p-4">{aside}</div>}

      <div className="flex flex-1 flex-col items-center justify-center gap-4 px-4 pb-16">
        <div className="w-full max-w-md rounded-[var(--radius-card)] border border-border-subtle bg-surface p-7 shadow-card">
          <div className="mb-6 flex items-center gap-2">
            <span className="grid size-8 place-items-center rounded-lg bg-brand text-brand-ink">
              <Boxes size={17} aria-hidden="true" />
            </span>
            <span className="text-sm font-semibold tracking-tight text-ink">Cratebase</span>
          </div>

          <h1 className="text-xl font-semibold tracking-tight text-ink">{title}</h1>
          {description && <p className="mt-1 mb-6 text-sm text-ink-muted">{description}</p>}

          {children}
        </div>

        {footer && <div className="max-w-md text-center text-xs text-ink-faint">{footer}</div>}
      </div>
    </div>
  )
}
