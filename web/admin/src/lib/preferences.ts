/**
 * Persisted interface preferences.
 *
 * Local storage can be refused — strict private browsing, corporate policy — and reading it then
 * throws instead of returning `null`. All preferences therefore go through here: a single guard,
 * rather than a forgotten `try` somewhere that would produce a blank screen on load.
 */

/** Reads a persisted flag. */
export function readFlag(key: string, fallback: boolean): boolean {
  try {
    const stored = globalThis.localStorage?.getItem(key)

    return stored === null || stored === undefined ? fallback : stored === '1'
  } catch {
    return fallback
  }
}

/** Writes a persisted flag. No effect if storage is inaccessible. */
export function writeFlag(key: string, value: boolean): void {
  try {
    globalThis.localStorage?.setItem(key, value ? '1' : '0')
  } catch {
    // The preference holds for the session, it won't survive a reload.
  }
}
