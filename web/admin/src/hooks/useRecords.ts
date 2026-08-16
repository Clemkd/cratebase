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
 * Page of records from a collection.
 *
 * The previous result is kept during a reload: clearing the table on every pagination keystroke
 * would make the layout jump, and the user would lose track of the row they were following.
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
