import { useId, useState, type ReactNode } from 'react'
import { cn } from './utils'

/**
 * Infobulle au survol et au clavier.
 *
 * Elle reste dans le DOM en permanence : `hidden` ou `display: none` la retirerait de l'arbre
 * d'accessibilité, donc `aria-describedby` ne désignerait plus rien pour un lecteur d'écran.
 *
 * Au repos elle est repliée à la manière d'un `sr-only` plutôt que rendue transparente : une bulle
 * transparente occupe sa taille réelle, et celles des boutons collés au bord droit — barre du haut,
 * colonne d'actions d'un tableau — élargissaient le document de quelques dizaines de pixels.
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
