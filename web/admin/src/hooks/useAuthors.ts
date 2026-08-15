import { useEffect, useRef, useState } from 'react'
import { api, type Collection, type LogEntry } from '../api'

/**
 * Champs qui peuvent nommer un compte, du plus parlant au plus technique.
 *
 * L'adresse électronique vient en dernier alors qu'elle existe toujours sur une collection de
 * comptes : elle identifie sûrement, mais une console qui affiche partout des adresses en
 * divulgue plus que nécessaire à quiconque regarde l'écran par-dessus l'épaule.
 */
const LABEL_FIELDS = ['name', 'displayName', 'fullName', 'username', 'title', 'label', 'email']

/** Clé d'un auteur : deux collections peuvent porter le même identifiant. */
export function authorKey(collection: string, id: string): string {
  return `${collection}/${id}`
}

/** Champ qui nommera les enregistrements d'une collection, ou `null` si aucun ne convient. */
function labelFieldOf(collection: Collection): string | null {
  for (const candidate of LABEL_FIELDS) {
    const field = collection.fields.find((entry) => entry.name === candidate)

    // Un champ multivalué ne nomme rien : il porte une liste, que rien ne garantit non vide.
    if (field && !field.multiple) return field.name
  }

  return null
}

/**
 * Résout les auteurs des entrées affichées vers un libellé lisible.
 *
 * Résolu côté console et non côté serveur, pour deux raisons. Le journal enregistre qui a agi au
 * moment où l'action a lieu ; y recopier un nom le figerait, et une entrée vieille d'un mois
 * afficherait un nom que son propriétaire a changé depuis. Et joindre les comptes à la lecture du
 * journal ferait dépendre l'écran d'exploitation des collections qu'il observe — celui qu'on ouvre
 * précisément quand quelque chose ne va pas.
 *
 * Une requête par collection et par page, pas une par ligne : les identifiants distincts sont
 * réunis en un seul filtre. Ce qui a déjà été résolu ne l'est pas deux fois, y compris en revenant
 * sur une page déjà vue.
 */
export function useAuthors(
  entries: LogEntry[],
  collections: Collection[],
): Map<string, string> {
  const [labels, setLabels] = useState<Map<string, string>>(new Map())

  // Le cache survit aux rendus sans en provoquer : c'est `labels` qui déclenche l'affichage, ce
  // registre ne sert qu'à ne pas redemander deux fois la même chose — y compris les échecs, sans
  // quoi une collection illisible serait réinterrogée à chaque frappe dans la recherche.
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

        // La collection a pu être supprimée depuis, ou ne porter aucun champ qui nomme quoi que ce
        // soit. L'identifiant abrégé reste alors le seul libellé honnête.
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
          // Comptes illisibles : l'écran d'exploitation continue de fonctionner avec des
          // identifiants. Faire échouer le journal parce qu'un nom manque serait disproportionné.
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
