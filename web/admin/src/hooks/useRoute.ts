import { useCallback, useEffect, useState } from 'react'

/** Views of a collection, as they appear in the tab bar. */
export type CollectionTab = 'records' | 'accounts' | 'general' | 'fields' | 'indexes' | 'rules'

/** Views that make up the schema editor. They share a single draft. */
export type SchemaTab = Extract<CollectionTab, 'general' | 'fields' | 'indexes' | 'rules'>

export const SCHEMA_TABS: SchemaTab[] = ['general', 'fields', 'indexes', 'rules']

/** Sections of the admin area. */
export type AdminSection = 'settings' | 'storage' | 'superusers' | 'providers'

export const ADMIN_SECTIONS: AdminSection[] = ['settings', 'storage', 'superusers', 'providers']

export type Route =
  /**
   * Dashboard, and root of the console.
   *
   * Two addresses for the same screen would be one too many: the home icon, the logo, and the
   * menu entry all lead here, so here is the root.
   */
  | { kind: 'home' }
  | { kind: 'new'; section: SchemaTab }
  | {
      kind: 'collection'
      name: string
      tab: CollectionTab
      /**
       * Record to highlight on arrival, designated by its identifier.
       *
       * Carried by the address rather than by application state: this is what lets a link — the
       * author of a log entry, for example — designate a specific record, and lets the "back"
       * arrow undo that framing.
       */
      focus?: string
    }
  | { kind: 'logs' }
  | { kind: 'files' }
  | { kind: 'admin'; section: AdminSection }

/**
 * URL fragment for each view.
 *
 * `records` has none: it's the landing view, and giving it a segment would create two URLs for
 * the same screen.
 */
const SEGMENTS: Record<CollectionTab, string> = {
  records: '',
  accounts: 'accounts',
  general: 'general',
  fields: 'fields',
  indexes: 'indexes',
  rules: 'rules',
}

/** URL fragment for each admin section. Settings is the landing view. */
const ADMIN_SEGMENTS: Record<AdminSection, string> = {
  settings: '',
  storage: 'storage',
  superusers: 'superusers',
  providers: 'providers',
}

/** Legacy addresses, still present in bookmarks. `schema` used to open the whole schema. */
const ALIASES: Record<string, CollectionTab> = {
  schema: 'general',
  accounts: 'accounts',
  fields: 'fields',
  rules: 'rules',
}

function tabFrom(segment: string | undefined): CollectionTab | null {
  if (!segment) return null

  const match = (Object.keys(SEGMENTS) as CollectionTab[]).find(
    (tab) => SEGMENTS[tab] !== '' && SEGMENTS[tab] === segment,
  )

  return match ?? ALIASES[segment] ?? null
}

function sectionFrom(segment: string | undefined): SchemaTab | null {
  const tab = tabFrom(segment)

  return tab && (SCHEMA_TABS as CollectionTab[]).includes(tab) ? (tab as SchemaTab) : null
}

function adminSectionFrom(segment: string | undefined): AdminSection | null {
  if (!segment) return null

  return ADMIN_SECTIONS.find((section) => ADMIN_SEGMENTS[section] === segment) ?? null
}

function parse(hash: string): Route {
  // The query part is stripped before splitting: without that, `accounts?r=…` would be taken for
  // a view name, and no view is named that.
  const [path = '', query] = hash.replace(/^#\/?/, '').split('?')
  const segments = path.split('/').filter(Boolean)
  const focus = new URLSearchParams(query ?? '').get('r') ?? undefined

  if (segments[0] === 'logs') {
    return { kind: 'logs' }
  }

  if (segments[0] === 'files') {
    return { kind: 'files' }
  }

  if (segments[0] === 'admin') {
    return { kind: 'admin', section: adminSectionFrom(segments[1]) ?? 'settings' }
  }

  if (segments[0] === 'new') {
    return { kind: 'new', section: sectionFrom(segments[1]) ?? 'general' }
  }

  if (segments[0] === 'c' && segments[1]) {
    return {
      kind: 'collection',
      name: decodeURIComponent(segments[1]),
      tab: tabFrom(segments[2]) ?? 'records',
      focus,
    }
  }

  return { kind: 'home' }
}

export function routeHref(route: Route): string {
  if (route.kind === 'home') return '#/'

  if (route.kind === 'logs') return '#/logs'

  if (route.kind === 'files') return '#/files'

  if (route.kind === 'admin') {
    return route.section === 'settings'
      ? '#/admin'
      : `#/admin/${ADMIN_SEGMENTS[route.section]}`
  }

  if (route.kind === 'new') {
    return route.section === 'general' ? '#/new' : `#/new/${SEGMENTS[route.section]}`
  }

  const base = `#/c/${encodeURIComponent(route.name)}`
  const path = route.tab === 'records' ? base : `${base}/${SEGMENTS[route.tab]}`

  return route.focus ? `${path}?r=${encodeURIComponent(route.focus)}` : path
}

/**
 * Routing by URL fragment.
 *
 * The fragment is enough here: the console is served as an SPA fallback by ASP.NET Core, and a
 * real path would require every deep URL to be redirected to `index.html` — one more condition to
 * maintain in the host, for zero benefit on an admin application.
 */
export function useRoute(): { route: Route; navigate: (route: Route) => void } {
  const [route, setRoute] = useState<Route>(() => parse(globalThis.location?.hash ?? ''))

  useEffect(() => {
    const onChange = () => setRoute(parse(globalThis.location.hash))

    globalThis.addEventListener('hashchange', onChange)
    return () => globalThis.removeEventListener('hashchange', onChange)
  }, [])

  const navigate = useCallback((next: Route) => {
    const href = routeHref(next)

    if (globalThis.location.hash === href) {
      setRoute(next)
      return
    }

    globalThis.location.hash = href
  }, [])

  return { route, navigate }
}
