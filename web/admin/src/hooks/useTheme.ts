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
    // Storage inaccessible: the system preference is authoritative, with no further consequence.
    return 'system'
  }
}

const darkQuery = globalThis.matchMedia?.('(prefers-color-scheme: dark)')

function compute(preference: ThemePreference): ThemeState {
  const resolved: ResolvedTheme =
    preference === 'system' ? (darkQuery?.matches ? 'dark' : 'light') : preference

  return { preference, resolved }
}

// The snapshot is memoized: `useSyncExternalStore` compares by identity, so rebuilding one on
// every read would cause a render loop.
let snapshot: ThemeState = compute(readPreference())

function publish(next: ThemeState) {
  snapshot = next
  globalThis.document?.documentElement.classList.toggle('dark', next.resolved === 'dark')
  listeners.forEach((listener) => listener())
}

// The system preference can change during the session — automatic switch by the operating system
// at sunset, for example.
darkQuery?.addEventListener('change', () => {
  if (snapshot.preference === 'system') publish(compute('system'))
})

export function setThemePreference(next: ThemePreference) {
  try {
    if (next === 'system') globalThis.localStorage?.removeItem(STORAGE_KEY)
    else globalThis.localStorage?.setItem(STORAGE_KEY, next)
  } catch {
    // The theme stays applied for the session, it simply won't survive a reload.
  }

  publish(compute(next))
}

/**
 * Console theme.
 *
 * The state lives outside React: the class is set on `<html>` before the first render by an
 * inline script, and several components can read the preference without going out of sync.
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
