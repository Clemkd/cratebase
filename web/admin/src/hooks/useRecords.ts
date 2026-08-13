import { useCallback, useEffect, useState } from 'react'
import { api, describeFailure, type Page, type RecordValue } from '../api'

export interface RecordsQuery {
  page: number
  perPage: number
  filter: string
  sort: string
}

export interface RecordsState {
  result: Page<RecordValue> | null
  loading: boolean
  error: string | null
  reload: () => Promise<void>
}

/**
 * Page d'enregistrements d'une collection.
 *
 * Le résultat précédent est conservé pendant un rechargement : vider la table à chaque frappe de
 * pagination ferait sauter la mise en page, et l'utilisateur perdrait le repère de la ligne qu'il
 * suivait.
 */
export function useRecords(collection: string, query: RecordsQuery): RecordsState {
  const [result, setResult] = useState<Page<RecordValue> | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const { page, perPage, filter, sort } = query

  const reload = useCallback(async () => {
    setLoading(true)

    try {
      setResult(await api.records.list(collection, { page, perPage, filter, sort }))
      setError(null)
    } catch (failure) {
      setError(describeFailure(failure))
    } finally {
      setLoading(false)
    }
  }, [collection, page, perPage, filter, sort])

  useEffect(() => {
    void reload()
  }, [reload])

  return { result, loading, error, reload }
}
