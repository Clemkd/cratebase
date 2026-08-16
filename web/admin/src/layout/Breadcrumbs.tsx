import { ChevronRight, Home } from 'lucide-react'
import type { Collection } from '../api'
import { routeHref, type Route } from '../hooks/useRoute'
import { cn } from '../ui'
import { TAB_LABELS, adminLabel } from './navigation'

/** A step in the breadcrumb. Without an `href`, it locates the page without leading there. */
interface Crumb {
  label: string
  href?: string
  mono?: boolean
}

/**
 * Path of the current page.
 *
 * It follows the navigation column step by step. One more step — the collection's kind, for
 * example — would have the breadcrumb say what the left-hand menu doesn't.
 */
function trail(route: Route, collection: Collection | null): Crumb[] {
  if (route.kind === 'new') return [{ label: 'New collection' }]
  if (route.kind === 'logs') return [{ label: 'Logs' }]
  if (route.kind === 'files') return [{ label: 'Files' }]

  if (route.kind === 'admin') {
    return [
      { label: 'Admin', href: routeHref({ kind: 'admin', section: 'settings' }) },
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
 * Breadcrumb for the top bar.
 *
 * It opens with a home icon that leads back to the root from any screen: it's thus one click
 * away, without consuming the width of a label. Below `sm`, there isn't room to show the full
 * path: only the last step remains, which is enough to locate.
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
    <nav aria-label="Breadcrumb" className="min-w-0">
      <ol className="flex items-center gap-1 text-sm">
        <li className="flex shrink-0 items-center">
          {atRoot ? (
            <span
              aria-current="page"
              className="grid size-7 place-items-center rounded-[var(--radius-control)] text-ink"
            >
              <Home size={15} aria-hidden="true" />
              <span className="sr-only">Dashboard</span>
            </span>
          ) : (
            <a
              href={routeHref({ kind: 'home' })}
              title="Dashboard"
              className="grid size-7 place-items-center rounded-[var(--radius-control)] text-ink-muted transition-colors hover:bg-surface-sunken hover:text-ink"
            >
              <Home size={15} aria-hidden="true" />
              <span className="sr-only">Dashboard</span>
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
