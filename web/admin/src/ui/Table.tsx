import type { HTMLAttributes, ReactNode, TdHTMLAttributes, ThHTMLAttributes } from 'react'
import { ArrowDown, ArrowUp, ChevronsUpDown } from 'lucide-react'
import { cn } from './utils'

export type SortDirection = 'asc' | 'desc' | null

/**
 * Tableau de données.
 *
 * Le défilement horizontal appartient au tableau, jamais à la page : une grille large doit pouvoir
 * s'étendre sans que la mise en page entière se mette à glisser sous le doigt.
 */
export function Table({
  children,
  className,
  caption,
  bare = false,
}: {
  children: ReactNode
  className?: string
  caption?: string
  /** Sans cadre : le tableau est déjà posé dans une carte qui porte la bordure. */
  bare?: boolean
}) {
  return (
    <div
      className={cn(
        'overflow-x-auto',
        !bare && 'rounded-[var(--radius-card)] border border-border-subtle bg-surface shadow-card',
        className,
      )}
    >
      <table className="w-full border-collapse text-sm">
        {caption && <caption className="sr-only">{caption}</caption>}
        {children}
      </table>
    </div>
  )
}

export function THead({ children }: { children: ReactNode }) {
  return <thead className="bg-surface-sunken">{children}</thead>
}

export function TBody({ children }: { children: ReactNode }) {
  return <tbody className="divide-y divide-border-subtle">{children}</tbody>
}

export function Tr({
  selected,
  muted,
  className,
  ...props
}: HTMLAttributes<HTMLTableRowElement> & { selected?: boolean; muted?: boolean }) {
  return (
    <tr
      className={cn(
        'transition-colors',
        selected ? 'bg-brand-subtle' : muted ? 'bg-surface-sunken/60' : 'hover:bg-surface-hover',
        className,
      )}
      {...props}
    />
  )
}

export function Th({ className, children, ...props }: ThHTMLAttributes<HTMLTableCellElement>) {
  return (
    <th
      scope="col"
      className={cn(
        'border-b border-border-subtle px-3 py-2.5 text-left text-xs font-semibold whitespace-nowrap text-ink-muted',
        className,
      )}
      {...props}
    >
      {children}
    </th>
  )
}

export function Td({ className, children, ...props }: TdHTMLAttributes<HTMLTableCellElement>) {
  return (
    <td className={cn('px-3 py-2 align-middle', className)} {...props}>
      {children}
    </td>
  )
}

/** En-tête cliquable. Le cycle est croissant → décroissant → aucun tri. */
export function SortableTh({
  label,
  direction,
  onSort,
  className,
}: {
  label: string
  direction: SortDirection
  onSort: () => void
  className?: string
}) {
  const Icon = direction === 'asc' ? ArrowUp : direction === 'desc' ? ArrowDown : ChevronsUpDown

  return (
    <Th
      aria-sort={direction === 'asc' ? 'ascending' : direction === 'desc' ? 'descending' : 'none'}
      className={cn('p-0', className)}
    >
      <button
        type="button"
        onClick={onSort}
        className="flex w-full items-center gap-1.5 px-3 py-2.5 text-left transition-colors hover:text-ink"
      >
        <span className="truncate">{label}</span>
        <Icon
          size={12}
          aria-hidden="true"
          className={cn('shrink-0', direction ? 'text-brand' : 'text-ink-faint')}
        />
      </button>
    </Th>
  )
}
