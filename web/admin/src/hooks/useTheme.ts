import { useSyncExternalStore } from 'react'

export type ThemePreference = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

export interface ThemeState {
  preference: ThemePreference
  resolved: ResolvedTheme
}

const STORAGE_KEY = 'cratebase.theme'

const listeners = new Set<() => void>()

function readPreference(): ThemePreference {
  try {
    const stored = globalThis.localStorage?.getItem(STORAGE_KEY)

    return stored === 'dark' || stored === 'light' ? stored : 'system'
  } catch {
    // Stockage inaccessible : la préférence système fait foi, sans plus de conséquence.
    return 'system'
  }
}

const darkQuery = globalThis.matchMedia?.('(prefers-color-scheme: dark)')

function compute(preference: ThemePreference): ThemeState {
  const resolved: ResolvedTheme =
    preference === 'system' ? (darkQuery?.matches ? 'dark' : 'light') : preference

  return { preference, resolved }
}

// L'instantané est mémorisé : `useSyncExternalStore` compare par identité, donc en reconstruire un
// à chaque lecture provoquerait une boucle de rendu.
let snapshot: ThemeState = compute(readPreference())

function publish(next: ThemeState) {
  snapshot = next
  globalThis.document?.documentElement.classList.toggle('dark', next.resolved === 'dark')
  listeners.forEach((listener) => listener())
}

// La préférence système peut changer pendant la session — bascule automatique du système
// d'exploitation au coucher du soleil, par exemple.
darkQuery?.addEventListener('change', () => {
  if (snapshot.preference === 'system') publish(compute('system'))
})

export function setThemePreference(next: ThemePreference) {
  try {
    if (next === 'system') globalThis.localStorage?.removeItem(STORAGE_KEY)
    else globalThis.localStorage?.setItem(STORAGE_KEY, next)
  } catch {
    // Le thème reste appliqué pour la session, il ne survivra simplement pas au rechargement.
  }

  publish(compute(next))
}

/**
 * Thème de la console.
 *
 * L'état vit hors de React : la classe est posée sur `<html>` avant le premier rendu par un script
 * en ligne, et plusieurs composants peuvent lire la préférence sans se désynchroniser.
 */
export function useTheme(): ThemeState & { setPreference: (next: ThemePreference) => void } {
  const state = useSyncExternalStore(
    (listener) => {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    () => snapshot,
    () => snapshot,
  )

  return { ...state, setPreference: setThemePreference }
}
