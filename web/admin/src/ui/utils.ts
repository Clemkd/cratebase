import { twMerge } from 'tailwind-merge'

/**
 * Merges conditional classes while resolving Tailwind conflicts.
 *
 * Simple concatenation isn't enough: two utilities from the same family — `size-9` set by a
 * primitive and `size-6` passed by the caller — coexist in the attribute, and it's the
 * stylesheet's order, not the order they're written in, that decides. In other words, a local
 * override would lose at the whim of class names. `twMerge` always makes the last class cited win.
 */
export function cn(...parts: (string | false | null | undefined)[]): string {
  return twMerge(parts.filter(Boolean).join(' '))
}

/**
 * Classes shared by every input control.
 *
 * The focus ring isn't neutralized here: the border that turns to the brand color signals the
 * active field to a mouse user, but it isn't enough for keyboard use — it's the global ring set
 * by `:focus-visible` that must remain visible.
 */
export const controlClasses = cn(
  'w-full rounded-[var(--radius-control)] border border-border-strong bg-surface px-3 text-sm text-ink',
  'placeholder:text-ink-faint transition-colors',
  'focus:border-brand',
  'disabled:cursor-not-allowed disabled:bg-surface-sunken disabled:text-ink-muted disabled:opacity-60',
  'aria-[invalid=true]:border-danger',
)
