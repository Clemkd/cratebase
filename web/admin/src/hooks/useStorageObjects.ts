import { useCallback, useEffect, useState } from 'react'
import { api, describeFailure, type Page, type StoredObject } from '../api'

export interface StorageQuery {
  page: number
  perPage: number
  collection: string
  q: string
  /** `files`, `thumbs`, ou chaîne vide pour les deux. */
  kind: string
  orphansOnly: boolean
}

export interface StorageObjectsState {
  result: (Page<StoredObject> & { orphans: number }) | null
  loading: boolean
  error: string | null
  reload: () => Promise<void>
}

/** Inventaire paginé du magasin de fichiers. */
export function useStorageObjects(query: StorageQuery): StorageObjectsState {
  const [result, setResult] = useState<(Page<StoredObject> & { orphans: number }) | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const { page, perPage, collection, q, kind, orphansOnly } = query

  const reload = useCallback(async () => {
    setLoading(true)

    try {
      setResult(
        await api.storage.objects({
          page,
          perPage,
          collection: collection === '' ? undefined : collection,
          q: q === '' ? undefined : q,
          kind: kind === '' ? undefined : kind,
          orphans: orphansOnly ? true : undefined,
        }),
      )
      setError(null)
    } catch (failure) {
      setError(describeFailure(failure))
    } finally {
      setLoading(false)
    }
  }, [page, perPage, collection, q, kind, orphansOnly])

  useEffect(() => {
    void reload()
  }, [reload])

  return { result, loading, error, reload }
}
