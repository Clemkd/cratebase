import { useEffect, useRef, useState } from 'react'
import { api, type Collection, type LogEntry } from '../api'

/**
 * Fields that can name an account, from most meaningful to most technical.
 *
 * Email comes last even though it's always present on an accounts collection: it identifies
 * reliably, but a console that displays addresses everywhere discloses more than necessary to
 * anyone glancing over the screen.
 */
const LABEL_FIELDS = ['name', 'displayName', 'fullName', 'username', 'title', 'label', 'email']

/** Author key: two collections can carry the same identifier. */
export function authorKey(collection: string, id: string): string {
  return `${collection}/${id}`
}

/** Field that will name a collection's records, or `null` if none fits. */
function labelFieldOf(collection: Collection): string | null {
  for (const candidate of LABEL_FIELDS) {
    const field = collection.fields.find((entry) => entry.name === candidate)

    // A multi-value field names nothing: it carries a list, which nothing guarantees is non-empty.
    if (field && !field.multiple) return field.name
  }

  return null
}

/**
 * Resolves the authors of displayed entries into a readable label.
 *
 * Resolved on the console side and not on the server, for two reasons. The log records who acted
 * at the moment the action happened; copying a name into it would freeze it, and an entry a
 * month old would show a name its owner has since changed. And joining accounts at log read time
 * would make the operations screen depend on the collections it observes — the very screen you
 * open precisely when something is wrong.
 *
 * One request per collection per page, not one per row: distinct identifiers are gathered into a
 * single filter. Anything already resolved isn't resolved twice, including when revisiting a page
 * already seen.
 */
export function useAuthors(
  entries: LogEntry[],
  collections: Collection[],
): Map<string, string> {
  const [labels, setLabels] = useState<Map<string, string>>(new Map())

  // The cache survives renders without triggering them: it's `labels` that drives the display,
  // this registry only prevents asking for the same thing twice — including failures, otherwise
  // an unreadable collection would be re-queried on every keystroke in the search box.
  const known = useRef(new Set<string>())

  useEffect(() => {
    const wanted = new Map<string, Set<string>>()

    for (const entry of entries) {
      if (entry.authCollection === '' || entry.authId === '') continue
      if (known.current.has(authorKey(entry.authCollection, entry.authId))) continue

      const ids = wanted.get(entry.authCollection) ?? new Set<string>()

      ids.add(entry.authId)
      wanted.set(entry.authCollection, ids)
    }

    if (wanted.size === 0) return

    let abandoned = false

    const resolve = async () => {
      const found = new Map<string, string>()

      for (const [name, ids] of wanted) {
        const collection = collections.find((entry) => entry.name === name)
        const field = collection ? labelFieldOf(collection) : null

        // The collection may have been deleted since, or carry no field that names anything.
        // The shortened identifier then remains the only honest label.
        if (!collection || !field) {
          for (const id of ids) known.current.add(authorKey(name, id))
          continue
        }

        const list = [...ids]

        try {
          const page = await api.records.list(name, {
            filter: list.map((id) => `id = '${id.replaceAll("'", "\\'")}'`).join(' || '),
            fields: `id,${field}`,
            perPage: list.length,
            skipTotal: true,
          })

          for (const record of page.items) {
            const id = String(record.id ?? '')
            const value = record[field]

            if (id !== '' && typeof value === 'string' && value !== '') {
              found.set(authorKey(name, id), value)
            }
          }
        } catch {
          // Accounts unreadable: the operations screen keeps working with identifiers. Failing
          // the log because a name is missing would be disproportionate.
        }

        for (const id of list) known.current.add(authorKey(name, id))
      }

      if (abandoned || found.size === 0) return

      setLabels((current) => new Map([...current, ...found]))
    }

    void resolve()

    return () => {
      abandoned = true
    }
  }, [entries, collections])

  return labels
}
