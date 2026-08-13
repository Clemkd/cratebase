import { useCallback, useEffect, useState } from 'react'
import { api, describeFailure, type Collection } from '../api'

export interface CollectionsState {
  collections: Collection[]
  engine: string
  loading: boolean
  error: string | null
  reload: () => Promise<void>
}

/** Catalogue des collections et moteur de stockage, rechargés à la demande. */
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
 * Groupe d'appartenance d'une collection.
 *
 * Une seule définition pour la colonne de navigation et pour le fil d'Ariane : deux règles
 * jumelles finiraient par diverger, et le fil contredirait alors le menu de gauche.
 */
export function groupOf(collection: Collection): CollectionGroup {
  if (collection.isSystem) return 'system'
  if (collection.kind === 'Auth') return 'auth'
  if (collection.kind === 'View') return 'view'

  return 'data'
}

/** Répartit les collections dans les groupes de la barre latérale. */
export function groupCollections(collections: Collection[]): Record<CollectionGroup, Collection[]> {
  return {
    data: collections.filter((item) => groupOf(item) === 'data'),
    auth: collections.filter((item) => groupOf(item) === 'auth'),
    view: collections.filter((item) => groupOf(item) === 'view'),
    system: collections.filter((item) => groupOf(item) === 'system'),
  }
}
