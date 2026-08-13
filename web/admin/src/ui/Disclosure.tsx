import { useId, type ReactNode } from 'react'
import { ChevronDown } from 'lucide-react'
import { Card } from './Card'
import { cn } from './utils'

/**
 * Section dépliable.
 *
 * Sert à poser plusieurs volets d'un même formulaire sur une seule page plutôt que derrière un
 * second niveau d'onglets : l'état de saisie reste unique, et un avertissement affiché dans une
 * section reste visible pendant qu'on travaille dans une autre.
 *
 * L'ouverture appartient à l'appelant : c'est souvent le contenu qui décide — une section qui porte
 * une alerte doit s'ouvrir d'elle-même.
 */
export function Disclosure({
  title,
  description,
  badge,
  actions,
  open,
  onToggle,
  className,
  children,
}: {
  title: ReactNode
  description?: ReactNode
  badge?: ReactNode
  /** Actions propres à la section, rendues hors du bouton pour rester atteignables au clavier. */
  actions?: ReactNode
  open: boolean
  onToggle: () => void
  className?: string
  children: ReactNode
}) {
  const panelId = useId()

  return (
    <Card className={cn('overflow-hidden', className)}>
      <div
        className={cn(
          'flex items-center gap-3 pr-4',
          open && 'border-b border-border-subtle bg-surface-sunken',
        )}
      >
        {/* Le bouton est enveloppé d'un titre : c'est ce qui fait de la section une étape dans la
            table des matières d'un lecteur d'écran, et non un bouton perdu dans le flux. */}
        <h2 className="min-w-0 flex-1">
          <button
            type="button"
            onClick={onToggle}
            aria-expanded={open}
            // Sans `aria-controls`, un lecteur d'écran annonce un bouton « développé » sans jamais
            // dire ce qu'il développe.
            aria-controls={panelId}
            className="flex w-full items-center gap-2.5 px-5 py-4 text-left"
          >
            <ChevronDown
              size={16}
              aria-hidden="true"
              className={cn('shrink-0 text-ink-faint transition-transform', open && 'rotate-180')}
            />
            <span className="min-w-0">
              <span className="block truncate text-sm font-semibold text-ink">{title}</span>
              {description && (
                <span className="mt-0.5 block text-xs font-normal text-ink-muted">
                  {description}
                </span>
              )}
            </span>
            {badge}
          </button>
        </h2>

        {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
      </div>

      {open && <div id={panelId}>{children}</div>}
    </Card>
  )
}
