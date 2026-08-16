import { useEffect, useState } from 'react'

/**
 * Delayed value: it only follows the source after a pause.
 *
 * Used for keyboard-typed filters. Querying the server on every keystroke produces one request
 * per character, all but the last of which are discarded; requiring an Enter to confirm forces
 * the user to guess that they need to, something nothing in an input field indicates.
 */
export function useDebounced<T>(value: T, delay = 350): T {
  const [settled, setSettled] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delay)

    return () => clearTimeout(timer)
  }, [value, delay])

  return settled
}
