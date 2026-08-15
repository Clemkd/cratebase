import { useEffect, useState } from 'react'

/**
 * Valeur retardée : elle ne suit la source qu'après une pause.
 *
 * Sert aux filtres saisis au clavier. Interroger le serveur à chaque touche produit une requête par
 * caractère, dont toutes sauf la dernière sont jetées ; exiger une validation par Entrée oblige
 * l'utilisateur à deviner qu'il faut le faire, ce que rien dans une zone de saisie n'indique.
 */
export function useDebounced<T>(value: T, delay = 350): T {
  const [settled, setSettled] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delay)

    return () => clearTimeout(timer)
  }, [value, delay])

  return settled
}
