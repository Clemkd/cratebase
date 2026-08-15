/**
 * Préférences d'interface persistées.
 *
 * Le stockage local peut être refusé — navigation privée stricte, politique d'entreprise — et sa
 * lecture lève alors au lieu de rendre `null`. Toutes les préférences passent donc par ici : une
 * seule garde, plutôt qu'un `try` oublié quelque part qui ferait écran blanc au chargement.
 */

/** Lit un drapeau persisté. */
export function readFlag(key: string, fallback: boolean): boolean {
  try {
    const stored = globalThis.localStorage?.getItem(key)

    return stored === null || stored === undefined ? fallback : stored === '1'
  } catch {
    return fallback
  }
}

/** Écrit un drapeau persisté. Sans effet si le stockage est inaccessible. */
export function writeFlag(key: string, value: boolean): void {
  try {
    globalThis.localStorage?.setItem(key, value ? '1' : '0')
  } catch {
    // La préférence tient pour la session, elle ne survivra pas au rechargement.
  }
}
