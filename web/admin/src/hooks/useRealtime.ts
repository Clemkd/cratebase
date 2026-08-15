import { useEffect, useRef } from 'react'
import { api, session } from '../api'

/** Une écriture annoncée par le serveur. */
export interface RealtimeMessage {
  action: 'create' | 'update' | 'delete'
  collection: string
  id: string
  record: Record<string, unknown> | null
}

/**
 * Abonnement temps réel à un ou plusieurs sujets.
 *
 * Deux requêtes, et c'est le navigateur qui l'impose : une `EventSource` ne porte aucun en-tête,
 * donc le flux s'ouvre anonyme et l'abonnement — qui, lui, est une requête ordinaire — y attache le
 * jeton. C'est aussi ce qui permet de changer de sujets sans rouvrir le flux.
 *
 * La réaction est passée par référence plutôt que par dépendance de l'effet : un écran qui
 * recharge sa liste à chaque évènement redéfinirait sa fonction à chaque rendu, et le flux se
 * fermerait puis se rouvrirait à chaque fois.
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
          // Message illisible : on l'ignore plutôt que de casser le flux pour les suivants.
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
          // Abonnement refusé — temps réel fermé, plafond atteint : l'écran reste utilisable, il
          // ne se met simplement plus à jour tout seul.
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
