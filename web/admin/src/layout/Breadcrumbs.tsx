import { ChevronRight, Home } from 'lucide-react'
import type { Collection } from '../api'
import { routeHref, type Route } from '../hooks/useRoute'
import { cn } from '../ui'
import { TAB_LABELS, adminLabel } from './navigation'

/** Une étape du fil d'Ariane. Sans `href`, elle situe la page sans y mener. */
interface Crumb {
  label: string
  href?: string
  mono?: boolean
}

/**
 * Chemin de la page courante.
 *
 * Il suit la colonne de navigation étape pour étape. Une étape de plus — la nature de la
 * collection, par exemple — ferait dire au fil ce que le menu de gauche ne dit pas.
 */
function trail(route: Route, collection: Collection | null): Crumb[] {
  if (route.kind === 'new') return [{ label: 'Nouvelle collection' }]
  if (route.kind === 'logs') return [{ label: 'Journaux' }]
  if (route.kind === 'files') return [{ label: 'Fichiers' }]

  if (route.kind === 'admin') {
    return [
      { label: 'Administration', href: routeHref({ kind: 'admin', section: 'overview' }) },
      { label: adminLabel(route.section) },
    ]
  }

  if (route.kind !== 'collection' || !collection) return []

  return [
    { label: 'Collections' },
    {
      label: collection.name,
      mono: true,
      href: routeHref({ kind: 'collection', name: collection.name, tab: 'records' }),
    },
    { label: TAB_LABELS[route.tab] },
  ]
}

/**
 * Fil d'Ariane de la barre du haut.
 *
 * Il s'ouvre sur une icône maison qui ramène à la racine depuis n'importe quel écran : elle y est
 * ainsi à un clic, sans consommer la largeur d'un libellé. Sous `sm`, la place ne permet pas
 * d'afficher le chemin complet : seule la dernière étape reste, ce qui suffit à situer.
 */
export function Breadcrumbs({
  route,
  collection,
}: {
  route: Route
  collection: Collection | null
}) {
  const crumbs = trail(route, collection)
  const atRoot = crumbs.length === 0

  return (
    <nav aria-label="Fil d'Ariane" className="min-w-0">
      <ol className="flex items-center gap-1 text-sm">
        <li className="flex shrink-0 items-center">
          {atRoot ? (
            <span
              aria-current="page"
              className="grid size-7 place-items-center rounded-[var(--radius-control)] text-ink"
            >
              <Home size={15} aria-hidden="true" />
              <span className="sr-only">Console</span>
            </span>
          ) : (
            <a
              href={routeHref({ kind: 'home' })}
              title="Console"
              className="grid size-7 place-items-center rounded-[var(--radius-control)] text-ink-muted transition-colors hover:bg-surface-sunken hover:text-ink"
            >
              <Home size={15} aria-hidden="true" />
              <span className="sr-only">Console</span>
            </a>
          )}
        </li>

        {crumbs.map((crumb, index) => {
          const last = index === crumbs.length - 1

          return (
            <li
              key={`${crumb.label}-${index}`}
              className={cn('min-w-0 items-center gap-1', last ? 'flex' : 'hidden sm:flex')}
            >
              <ChevronRight size={13} className="shrink-0 text-ink-faint" aria-hidden="true" />
              {last ? (
                <span
                  aria-current="page"
                  className={cn('truncate font-medium text-ink', crumb.mono && 'font-mono text-xs')}
                >
                  {crumb.label}
                </span>
              ) : crumb.href ? (
                <a
                  href={crumb.href}
                  className={cn(
                    'truncate text-ink-muted transition-colors hover:text-ink hover:underline',
                    crumb.mono && 'font-mono text-xs',
                  )}
                >
                  {crumb.label}
                </a>
              ) : (
                <span className={cn('truncate text-ink-muted', crumb.mono && 'font-mono text-xs')}>
                  {crumb.label}
                </span>
              )}
            </li>
          )
        })}
      </ol>
    </nav>
  )
}
