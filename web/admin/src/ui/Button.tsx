import type { ButtonHTMLAttributes, ReactNode, Ref } from 'react'
import { Spinner } from './Feedback'
import { cn } from './utils'

export type ButtonVariant = 'primary' | 'secondary' | 'outline' | 'ghost' | 'danger'
export type ButtonSize = 'sm' | 'md' | 'lg' | 'icon'

const VARIANTS: Record<ButtonVariant, string> = {
  primary: 'bg-brand text-brand-ink hover:bg-brand-hover',
  secondary: 'bg-surface-sunken text-ink hover:bg-border-subtle',
  outline: 'border border-border-strong text-ink hover:bg-surface-sunken',
  ghost: 'text-ink-muted hover:bg-surface-sunken hover:text-ink',
  danger: 'bg-danger text-danger-ink hover:opacity-90',
}

const SIZES: Record<ButtonSize, string> = {
  sm: 'h-8 px-3 text-sm',
  md: 'h-10 px-4 text-sm',
  lg: 'h-11 px-5 text-base',
  icon: 'size-9',
}

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant
  size?: ButtonSize
  loading?: boolean
  /** Icône placée avant le libellé. Remplacée par l'indicateur d'attente pendant le chargement. */
  icon?: ReactNode
  /** Depuis React 19, `ref` est une prop ordinaire : plus besoin de `forwardRef`. */
  ref?: Ref<HTMLButtonElement>
}

/**
 * Bouton.
 *
 * `type="button"` par défaut, surchargeable : la console place des boutons d'action à l'intérieur
 * de formulaires, et le défaut HTML — « submit » — y déclencherait l'envoi au moindre clic.
 */
export function Button({
  variant = 'secondary',
  size = 'md',
  loading = false,
  icon,
  className,
  children,
  disabled,
  ...props
}: ButtonProps) {
  return (
    <button
      type="button"
      disabled={disabled ?? loading}
      aria-busy={loading || undefined}
      className={cn(
        'inline-flex shrink-0 items-center justify-center gap-2 rounded-[var(--radius-control)]',
        'font-medium whitespace-nowrap transition-colors',
        'disabled:pointer-events-none disabled:opacity-50',
        VARIANTS[variant],
        SIZES[size],
        className,
      )}
      {...props}
    >
      {loading ? <Spinner size={size === 'sm' ? 14 : 16} /> : icon}
      {children}
    </button>
  )
}
