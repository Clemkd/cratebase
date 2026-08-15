import {
  FolderOpen,
  Gauge,
  HardDrive,
  KeyRound,
  ScrollText,
  ShieldCheck,
  SlidersHorizontal,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
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
 * Sections de l'espace d'administration, dans l'ordre d'affichage.
 *
 * Les paramètres ouvrent la liste : c'est la section qu'on vient régler, et le tableau de bord —
 * qui ne modifie rien — a quitté l'administration pour la racine de la console.
 */
export const ADMIN_ITEMS: { id: AdminSection; label: string; icon: LucideIcon }[] = [
  { id: 'settings', label: 'Paramètres', icon: SlidersHorizontal },
  { id: 'storage', label: 'Stockage', icon: HardDrive },
  { id: 'superusers', label: 'Super-admins', icon: ShieldCheck },
  { id: 'providers', label: "Fournisseurs d'identité", icon: KeyRound },
]

export function adminLabel(section: AdminSection): string {
  return ADMIN_ITEMS.find((entry) => entry.id === section)?.label ?? ''
}

/** Icône du tableau de bord, qui est aussi la racine de la console. */
export const DASHBOARD_ICON: LucideIcon = Gauge

/** Icône de l'écran des journaux, partagée par la colonne et le fil d'Ariane. */
export const LOGS_ICON: LucideIcon = ScrollText

/** Icône de l'écran des fichiers. */
export const FILES_ICON: LucideIcon = FolderOpen
