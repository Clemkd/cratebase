import { useCallback, useEffect, useState } from 'react'
import { api, describeFailure, type Collection } from '../api'

export interface CollectionsState {
  collections: Collection[]
  engine: string
  loading: boolean
  error: string | null
  reload: () => Promise<void>
}

/** Catalog of collections and storage engine, reloaded on demand. */
export function useCollections(): CollectionsState {
  const [collections, setCollections] = useState<Collection[]>([])
  const [engine, setEngine] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)

    try {
      const [health, list] = await Promise.all([api.health(), api.collections.list()])

      setEngine(health.engine)
      setCollections(list.items)
      setError(null)
    } catch (failure) {
      setError(describeFailure(failure))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void reload()
  }, [reload])

  return { collections, engine, loading, error, reload }
}

export type CollectionGroup = 'data' | 'auth' | 'view' | 'system'

/**
 * Group a collection belongs to.
 *
 * A single definition for the navigation column and the breadcrumb: two twin rules would
 * eventually diverge, and the breadcrumb would then contradict the left-hand menu.
 */
export function groupOf(collection: Collection): CollectionGroup {
  if (collection.isSystem) return 'system'
  if (collection.kind === 'Auth') return 'auth'
  if (collection.kind === 'View') return 'view'

  return 'data'
}

/** Splits collections into the sidebar's groups. */
export function groupCollections(collections: Collection[]): Record<CollectionGroup, Collection[]> {
  return {
    data: collections.filter((item) => groupOf(item) === 'data'),
    auth: collections.filter((item) => groupOf(item) === 'auth'),
    view: collections.filter((item) => groupOf(item) === 'view'),
    system: collections.filter((item) => groupOf(item) === 'system'),
  }
}

const RANK: Record<CollectionGroup, number> = { data: 0, auth: 1, view: 2, system: 3 }

/**
 * Display order of the column: a single list, from everyday use toward the engine.
 *
 * Group membership decides the rank but no longer splits the list into titled sections. Three
 * capitalized subheadings, each followed by one or two names, produced more decorative lines than
 * destinations — and a column of five collections took up eleven lines. The group stays legible,
 * carried by each entry's icon; the ordering does the rest of the work by pushing system
 * collections to the bottom of the list, where nobody looks for them.
 */
export function sortCollections(collections: Collection[]): Collection[] {
  return [...collections].sort(
    (left, right) =>
      RANK[groupOf(left)] - RANK[groupOf(right)] ||
      left.name.localeCompare(right.name, 'en', { sensitivity: 'base' }),
  )
}
