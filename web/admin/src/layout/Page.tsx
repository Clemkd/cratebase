import { createContext, useContext, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { cn } from '../ui'

/**
 * Emplacement des actions dans l'en-tête de page.
 *
 * Un écran rendu dans `children` peut y déposer ses boutons alors qu'ils s'affichent au-dessus de
 * lui, à côté du titre. Sans ce relais, il faudrait remonter l'état de l'écran jusqu'à l'appelant
 * de `Page` pour que celui-ci puisse en dériver les actions — l'inverse de ce que sert un composant
 * autonome.
 *
 * Le gain n'est pas cosmétique : une barre d'actions sur sa propre ligne coûte une ligne entière de
 * hauteur utile sur chaque écran, alors que l'en-tête a de la place libre à droite du titre.
 */
const PageActionsSlot = createContext<HTMLElement | null>(null)

/** Conteneur de page : largeur, marges et en-tête homogènes sur toute la console. */
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
  /** Actions de la page. Leur place est ici, pas dans les barres d'outils du contenu. */
  actions?: ReactNode
  /** Repères de lecture posés sous le titre : nature de la collection, volumétrie, identifiant. */
  meta?: ReactNode
  children: ReactNode
  /** Les écrans en tableau réclament toute la largeur disponible. */
  wide?: boolean
}) {
  // L'élément est gardé en état, et non dans une ref : une ref ne provoque pas de nouveau rendu,
  // donc les enfants ne sauraient jamais que la destination existe.
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
 * Dépose des actions dans l'en-tête de la page englobante.
 *
 * Sans `Page` au-dessus, ne rend rien plutôt que de lever : un écran doit rester montable seul,
 * dans un test par exemple.
 */
export function PageActions({ children }: { children: ReactNode }) {
  const slot = useContext(PageActionsSlot)

  return slot ? createPortal(children, slot) : null
}

/** Grille de tuiles chiffrées. */
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
 * Tuile chiffrée.
 *
 * Cliquable, elle devient un vrai bouton et non une `div` munie d'un `onClick` : c'est la seule
 * forme qui reste atteignable à la tabulation et actionnable à la barre d'espace.
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
  /** Nom accessible du bouton, qui doit dire où mène la tuile. */
  actionLabel?: string
}) {
  const body = (
    <>
      <div className="flex items-center justify-between gap-2">
        <p className="text-xs font-medium text-ink-muted">{label}</p>
        {icon && <span className={STAT_TONES[tone]}>{icon}</span>}
      </div>
      <p className={cn('mt-1.5 text-2xl font-semibold tabular-nums', STAT_TONES[tone])}>{value}</p>
      {hint && <p className="mt-0.5 text-xs text-ink-muted">{hint}</p>}
    </>
  )

  const shell = cn(
    'rounded-[var(--radius-card)] border bg-surface px-4 py-3.5 text-left shadow-card',
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
