import {
  Database,
  Eye,
  FolderOpen,
  Gauge,
  HardDrive,
  KeyRound,
  ScrollText,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
  Users,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { CollectionGroup } from '../hooks/useCollections'
import type { AdminSection, CollectionTab } from '../hooks/useRoute'

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

/**
 * Sections de l'espace d'administration, dans l'ordre d'affichage.
 *
 * L'aperçu ouvre la liste parce qu'il ne modifie rien : on y entre pour savoir où on est avant de
 * toucher à quoi que ce soit.
 */
export const ADMIN_ITEMS: { id: AdminSection; label: string; icon: LucideIcon }[] = [
  { id: 'overview', label: 'Aperçu', icon: Gauge },
  { id: 'settings', label: 'Paramètres', icon: SlidersHorizontal },
  { id: 'storage', label: 'Stockage', icon: HardDrive },
  { id: 'superusers', label: 'Superadministrateurs', icon: ShieldCheck },
  { id: 'providers', label: "Fournisseurs d'identité", icon: KeyRound },
]

export function adminLabel(section: AdminSection): string {
  return ADMIN_ITEMS.find((entry) => entry.id === section)?.label ?? ''
}

/** Icône de l'écran des journaux, partagée par la colonne et le fil d'Ariane. */
export const LOGS_ICON: LucideIcon = ScrollText

/** Icône de l'écran des fichiers. */
export const FILES_ICON: LucideIcon = FolderOpen
