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
  records: 'Records',
  accounts: 'Accounts',
  general: 'General',
  fields: 'Fields',
  indexes: 'Indexes',
  rules: 'Access rules',
}

/**
 * Sections of the admin area, in display order.
 *
 * Settings opens the list: it's the section you come to adjust, and the dashboard — which
 * changes nothing — has moved out of admin to the console's root.
 */
export const ADMIN_ITEMS: { id: AdminSection; label: string; icon: LucideIcon }[] = [
  { id: 'settings', label: 'Settings', icon: SlidersHorizontal },
  { id: 'storage', label: 'Storage', icon: HardDrive },
  { id: 'superusers', label: 'Superusers', icon: ShieldCheck },
  { id: 'providers', label: 'Identity providers', icon: KeyRound },
]

export function adminLabel(section: AdminSection): string {
  return ADMIN_ITEMS.find((entry) => entry.id === section)?.label ?? ''
}

/** Dashboard icon, which is also the console's root. */
export const DASHBOARD_ICON: LucideIcon = Gauge

/** Icon for the logs screen, shared by the sidebar and the breadcrumb. */
export const LOGS_ICON: LucideIcon = ScrollText

/** Icon for the files screen. */
export const FILES_ICON: LucideIcon = FolderOpen
