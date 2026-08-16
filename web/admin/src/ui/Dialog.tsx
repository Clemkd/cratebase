import { useEffect, useId, useRef, type ReactNode } from 'react'
import { X } from 'lucide-react'
import { Button } from './Button'
import { cn } from './utils'

export type DialogSide = 'center' | 'right'

/**
 * Accessible modal.
 *
 * Built on the native `<dialog>`: focus trapping, focus restoration on close, the Escape key, and
 * the top layer are all provided by the browser. Reimplementing them in React amounts to
 * rewriting — worse — what the platform already does correctly.
 */
export function Dialog({
  open,
  onClose,
  title,
  description,
  footer,
  side = 'center',
  width = 'md',
  children,
}: {
  open: boolean
  onClose: () => void
  title: string
  description?: ReactNode
  footer?: ReactNode
  side?: DialogSide
  width?: 'sm' | 'md' | 'lg'
  children: ReactNode
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()

  useEffect(() => {
    const dialog = ref.current

    if (!dialog) return

    if (open && !dialog.open) dialog.showModal()
    else if (!open && dialog.open) dialog.close()
  }, [open])

  const widths = { sm: 'max-w-md', md: 'max-w-2xl', lg: 'max-w-4xl' }[width]

  return (
    <dialog
      ref={ref}
      aria-labelledby={titleId}
      onCancel={(event) => {
        // Closing stays driven by the parent: without this, React state and DOM state diverge
        // on the first press of Escape.
        event.preventDefault()
        onClose()
      }}
      onClick={(event) => {
        if (event.target === ref.current) onClose()
      }}
      className={cn(
        'w-full backdrop:transition-opacity',
        side === 'center'
          ? cn('m-auto px-4', widths)
          : 'my-0 ml-auto h-full max-h-full w-full max-w-xl p-0',
      )}
    >
      <div
        className={cn(
          'flex flex-col overflow-hidden border border-border-subtle bg-surface-raised text-ink',
          side === 'center'
            ? 'max-h-[85vh] rounded-[var(--radius-card)] shadow-popover'
            : 'h-full border-y-0 border-r-0 shadow-popover',
        )}
      >
        {/* Neither `header` nor `footer` here: inside a modal, they announce themselves as
            "banner" and "contentinfo" landmarks, which adds false page landmarks. */}
        <div className="flex items-start justify-between gap-4 border-b border-border-subtle px-5 py-4">
          <div className="min-w-0">
            <h2 id={titleId} className="text-sm font-semibold text-ink">
              {title}
            </h2>
            {description && <div className="mt-0.5 text-xs text-ink-muted">{description}</div>}
          </div>

          <Button variant="ghost" size="icon" aria-label="Close" onClick={onClose}>
            <X size={16} aria-hidden="true" />
          </Button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-5 py-5">{children}</div>

        {footer && (
          <div className="flex flex-wrap items-center justify-end gap-2 border-t border-border-subtle bg-surface-sunken px-5 py-3.5">
            {footer}
          </div>
        )}
      </div>
    </dialog>
  )
}

/**
 * Confirmation of a destructive action.
 *
 * Focus starts on cancel, never on confirm: these dialogs guard an irreversible action, and an
 * Enter pressed by reflex must not trigger it.
 */
export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = 'Confirm',
  confirmIcon,
  busy = false,
  onConfirm,
  onClose,
}: {
  open: boolean
  title: string
  message: ReactNode
  confirmLabel?: string
  /**
   * Icon for the confirm button.
   *
   * Provided by the caller rather than fixed here: these dialogs confirm both a deletion and an
   * abandoned edit, and a trash icon on both would end up no longer meaning "destruction".
   */
  confirmIcon?: ReactNode
  busy?: boolean
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={title}
      width="sm"
      footer={
        <>
          <Button
            variant="outline"
            icon={<X size={15} aria-hidden="true" />}
            onClick={onClose}
            disabled={busy}
            autoFocus
          >
            Cancel
          </Button>
          <Button variant="danger" icon={confirmIcon} onClick={onConfirm} loading={busy}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="text-sm text-ink-muted">{message}</div>
    </Dialog>
  )
}
