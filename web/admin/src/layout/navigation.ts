import { Database, Eye, Settings2, Users } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { CollectionGroup } from '../hooks/useCollections'
import type { CollectionTab } from '../hooks/useRoute'

export const TAB_LABELS: Record<CollectionTab, string> = {
  records: 'Enregistrements',
  accounts: 'Comptes',
  general: 'Général',
  fields: 'Champs',
  indexes: 'Index',
  rules: "Règles d'accès",
}

/**
 * Groupes de la colonne de navigation, dans l'ordre d'affichage.
 *
 * L'ordre n'est pas alphabétique : il descend de ce qu'on manipule tous les jours — les données —
 * vers ce qu'on ne touche qu'à la configuration. Les collections système ferment la liste parce
 * qu'elles appartiennent au moteur, pas au projet.
 */
export const GROUPS: { id: CollectionGroup; label: string; icon: LucideIcon }[] = [
  { id: 'data', label: 'Données', icon: Database },
  { id: 'auth', label: 'Comptes', icon: Users },
  { id: 'view', label: 'Vues', icon: Eye },
  { id: 'system', label: 'Système', icon: Settings2 },
]

export function groupLabel(group: CollectionGroup): string {
  return GROUPS.find((entry) => entry.id === group)?.label ?? ''
}
