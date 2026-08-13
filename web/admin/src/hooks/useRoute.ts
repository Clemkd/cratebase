import { useCallback, useEffect, useState } from 'react'

/** Vues d'une collection, telles qu'elles apparaissent dans la barre d'onglets. */
export type CollectionTab = 'records' | 'accounts' | 'general' | 'fields' | 'indexes' | 'rules'

/** Vues qui composent l'éditeur de schéma. Elles partagent un même brouillon. */
export type SchemaTab = Extract<CollectionTab, 'general' | 'fields' | 'indexes' | 'rules'>

export const SCHEMA_TABS: SchemaTab[] = ['general', 'fields', 'indexes', 'rules']

export type Route =
  | { kind: 'home' }
  | { kind: 'new'; section: SchemaTab }
  | { kind: 'collection'; name: string; tab: CollectionTab }

/**
 * Fragment d'URL de chaque vue.
 *
 * `records` n'en a pas : c'est la vue d'arrivée, et lui donner un segment ferait deux URL pour un
 * même écran. Les segments sont en français comme le reste de l'interface ; les identifiants de
 * code restent en anglais.
 */
const SEGMENTS: Record<CollectionTab, string> = {
  records: '',
  accounts: 'comptes',
  general: 'general',
  fields: 'champs',
  indexes: 'index',
  rules: 'regles',
}

/** Anciennes adresses, encore présentes dans des favoris. `schema` ouvrait le schéma entier. */
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

function parse(hash: string): Route {
  const segments = hash.replace(/^#\/?/, '').split('/').filter(Boolean)

  if (segments[0] === 'nouvelle') {
    return { kind: 'new', section: sectionFrom(segments[1]) ?? 'general' }
  }

  if (segments[0] === 'c' && segments[1]) {
    return {
      kind: 'collection',
      name: decodeURIComponent(segments[1]),
      tab: tabFrom(segments[2]) ?? 'records',
    }
  }

  return { kind: 'home' }
}

export function routeHref(route: Route): string {
  if (route.kind === 'home') return '#/'

  if (route.kind === 'new') {
    return route.section === 'general' ? '#/nouvelle' : `#/nouvelle/${SEGMENTS[route.section]}`
  }

  const base = `#/c/${encodeURIComponent(route.name)}`

  return route.tab === 'records' ? base : `${base}/${SEGMENTS[route.tab]}`
}

/**
 * Routage par fragment d'URL.
 *
 * Le fragment suffit ici : la console est servie en repli SPA par ASP.NET Core, et un chemin réel
 * exigerait que chaque URL profonde soit renvoyée vers `index.html` — une condition de plus à tenir
 * dans l'hôte, pour un gain nul sur une application d'administration.
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
