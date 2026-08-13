import { useEffect, useId, useRef, type ReactNode } from 'react'
import { X } from 'lucide-react'
import { Button } from './Button'
import { cn } from './utils'

export type DialogSide = 'center' | 'right'

/**
 * Modale accessible.
 *
 * Bâtie sur `<dialog>` natif : le piège de focus, la restauration du focus à la fermeture, la
 * touche Échap et le calque supérieur sont fournis par le navigateur. Les réimplémenter en React
 * revient à réécrire — moins bien — ce que la plateforme fait déjà correctement.
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
        // La fermeture reste pilotée par le parent : sans cela, l'état React et l'état du DOM
        // divergent dès la première pression sur Échap.
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
        {/* Ni `header` ni `footer` ici : à l'intérieur d'une modale, ils s'annoncent comme des
            repères « banner » et « contentinfo », ce qui ajoute de faux repères de page. */}
        <div className="flex items-start justify-between gap-4 border-b border-border-subtle px-5 py-4">
          <div className="min-w-0">
            <h2 id={titleId} className="text-sm font-semibold text-ink">
              {title}
            </h2>
            {description && <div className="mt-0.5 text-xs text-ink-muted">{description}</div>}
          </div>

          <Button variant="ghost" size="icon" aria-label="Fermer" onClick={onClose}>
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
 * Confirmation d'une action destructrice.
 *
 * Le focus part sur l'annulation, jamais sur la confirmation : ces dialogues gardent une action
 * irréversible, et une entrée frappée par réflexe ne doit pas l'exécuter.
 */
export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = 'Confirmer',
  busy = false,
  onConfirm,
  onClose,
}: {
  open: boolean
  title: string
  message: ReactNode
  confirmLabel?: string
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
          <Button variant="outline" onClick={onClose} disabled={busy} autoFocus>
            Annuler
          </Button>
          <Button variant="danger" onClick={onConfirm} loading={busy}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="text-sm text-ink-muted">{message}</div>
    </Dialog>
  )
}
