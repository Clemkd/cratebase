import { twMerge } from 'tailwind-merge'

/**
 * Fusionne des classes conditionnelles en résolvant les conflits Tailwind.
 *
 * La simple concaténation ne suffit pas : deux utilitaires de la même famille — `size-9` posé par
 * une primitive et `size-6` passé par l'appelant — cohabitent dans l'attribut, et c'est l'ordre de
 * la feuille de style, non celui de l'écriture, qui tranche. Autrement dit, une surcharge locale
 * perdrait au hasard des noms. `twMerge` fait gagner la dernière classe citée, toujours.
 */
export function cn(...parts: (string | false | null | undefined)[]): string {
  return twMerge(parts.filter(Boolean).join(' '))
}

/**
 * Classes communes à tous les contrôles de saisie.
 *
 * L'anneau de focus n'est pas neutralisé ici : la bordure qui vire à la couleur de marque signale
 * le champ actif à la souris, mais elle ne suffit pas au clavier — c'est l'anneau global posé par
 * `:focus-visible` qui doit rester visible.
 */
export const controlClasses = cn(
  'w-full rounded-[var(--radius-control)] border border-border-strong bg-surface px-3 text-sm text-ink',
  'placeholder:text-ink-faint transition-colors',
  'focus:border-brand',
  'disabled:cursor-not-allowed disabled:bg-surface-sunken disabled:text-ink-muted disabled:opacity-60',
  'aria-[invalid=true]:border-danger',
)
