import { useEffect, useRef } from 'react'
import { api, session } from '../api'

/** A write announced by the server. */
export interface RealtimeMessage {
  action: 'create' | 'update' | 'delete'
  collection: string
  id: string
  record: Record<string, unknown> | null
}

/**
 * Realtime subscription to one or more topics.
 *
 * Two requests, and the browser is what forces it: an `EventSource` carries no header, so the
 * stream opens anonymous and the subscription — an ordinary request — attaches the token to it.
 * This is also what allows changing topics without reopening the stream.
 *
 * The reaction is passed by reference rather than as an effect dependency: a screen that
 * reloads its list on every event would redefine its function on every render, and the stream
 * would close and reopen each time.
 */
export function useRealtime(
  topics: string[],
  onMessage: (message: RealtimeMessage) => void,
  enabled = true,
): void {
  const react = useRef(onMessage)
  const key = topics.join(',')

  useEffect(() => {
    react.current = onMessage
  }, [onMessage])

  useEffect(() => {
    if (!enabled || key === '') return

    let source: EventSource | null = null
    let abandoned = false

    const listen = (client: EventSource, topic: string) => {
      client.addEventListener(topic, (event) => {
        try {
          react.current(JSON.parse((event as MessageEvent<string>).data) as RealtimeMessage)
        } catch {
          // Unreadable message: it's ignored rather than breaking the stream for the ones after it.
        }
      })
    }

    const open = () => {
      const client = new EventSource('/api/realtime')

      source = client

      client.addEventListener('connect', (event) => {
        const { clientId } = JSON.parse((event as MessageEvent<string>).data) as { clientId: string }

        if (abandoned || !session.token) return

        void api.realtime.subscribe(clientId, key.split(',')).catch(() => {
          // Subscription refused — realtime disabled, cap reached: the screen remains usable, it
          // simply stops updating on its own.
        })
      })

      for (const topic of key.split(',')) listen(client, topic)
    }

    open()

    return () => {
      abandoned = true
      source?.close()
    }
  }, [key, enabled])
}
