import { useRef, type ReactNode } from 'react'
import { cn } from './utils'

export interface TabDefinition<T extends string> {
  id: T
  label: ReactNode
  badge?: ReactNode
}

const tabId = (name: string, id: string) => `onglet-${name}-${id}`
const panelId = (name: string, id: string) => `panneau-${name}-${id}`

/**
 * Onglets conformes au motif ARIA : navigation par flèches, `aria-controls` vers le panneau,
 * un seul onglet dans l'ordre de tabulation.
 *
 * `name` doit être unique dans la page : il relie chaque onglet à son `TabPanel`, qui est rendu
 * ailleurs dans l'arbre et ne peut donc pas hériter d'un identifiant interne.
 */
export function Tabs<T extends string>({
  name,
  tabs,
  active,
  onChange,
  label,
  className,
}: {
  name: string
  tabs: TabDefinition<T>[]
  active: T
  onChange: (id: T) => void
  label: string
  className?: string
}) {
  const listRef = useRef<HTMLDivElement>(null)

  const move = (offset: number) => {
    const index = tabs.findIndex((tab) => tab.id === active)
    const next = tabs[(index + offset + tabs.length) % tabs.length]

    if (!next) return

    onChange(next.id)
    listRef.current
      ?.querySelector<HTMLButtonElement>(`#${CSS.escape(tabId(name, next.id))}`)
      ?.focus()
  }

  return (
    <div
      ref={listRef}
      role="tablist"
      aria-label={label}
      className={cn(
        // `overflow-y-hidden` explicitement : dès qu'un axe cesse d'être `visible`, l'autre passe
        // à `auto` par défaut, et la barre d'onglets se retrouve avec un ascenseur vertical pour
        // quelques pixels d'anneau de focus. Le contenu des onglets, lui, défile normalement.
        //
        // `pb-px` compense le `-mb-px` des onglets : sans ce pixel de marge intérieure, le trait de
        // l'onglet actif déborde de la boîte de remplissage et se fait rogner par le masquage.
        'flex items-center gap-1 overflow-x-auto overflow-y-hidden pb-px border-b border-border-subtle',
        className,
      )}
      onKeyDown={(event) => {
        if (event.key === 'ArrowRight') {
          event.preventDefault()
          move(1)
        } else if (event.key === 'ArrowLeft') {
          event.preventDefault()
          move(-1)
        }
      }}
    >
      {tabs.map((tab) => (
        <button
          key={tab.id}
          id={tabId(name, tab.id)}
          type="button"
          role="tab"
          aria-selected={tab.id === active}
          aria-controls={panelId(name, tab.id)}
          tabIndex={tab.id === active ? 0 : -1}
          onClick={() => onChange(tab.id)}
          className={cn(
            '-mb-px inline-flex shrink-0 items-center gap-2 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors',
            // Anneau de focus tracé à l'intérieur du bouton : la barre masquant son débordement
            // vertical, un anneau posé au-dehors serait rogné — et l'onglet parcouru au clavier
            // deviendrait invisible.
            'focus-visible:-outline-offset-2',
            tab.id === active
              ? 'border-brand text-ink'
              : 'border-transparent text-ink-muted hover:text-ink',
          )}
        >
          {tab.label}
          {tab.badge}
        </button>
      ))}
    </div>
  )
}

export function TabPanel<T extends string>({
  name,
  id,
  active,
  className,
  children,
}: {
  name: string
  id: T
  active: T
  className?: string
  children: ReactNode
}) {
  if (id !== active) return null

  return (
    <div
      id={panelId(name, id)}
      role="tabpanel"
      aria-labelledby={tabId(name, id)}
      // Le panneau est atteignable au clavier — c'est là que le motif ARIA envoie l'utilisateur
      // après les onglets. Son anneau de focus reste donc visible : le supprimer ferait disparaître
      // le curseur au moment précis où il entre dans le contenu.
      tabIndex={0}
      className={cn('rounded-[var(--radius-control)] outline-offset-4', className)}
    >
      {children}
    </div>
  )
}
