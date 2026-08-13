import {
  createContext,
  useContext,
  useId,
  type AriaAttributes,
  type InputHTMLAttributes,
  type ReactNode,
  type TextareaHTMLAttributes,
} from 'react'
import { SelectMenu, type SelectMenuProps } from './SelectMenu'
import { cn, controlClasses } from './utils'

interface FieldContextValue {
  controlId: string
  describedBy?: string
  invalid: boolean
}

const FieldContext = createContext<FieldContextValue | null>(null)

/**
 * Attributs à poser sur le contrôle d'un `Field`.
 *
 * Passer par un contexte plutôt que par des props évite d'avoir à recopier `id`,
 * `aria-invalid` et `aria-describedby` sur chaque appel — c'est exactement ce qu'on oublie,
 * et l'oubli ne se voit qu'au lecteur d'écran.
 */
function useFieldAttributes<
  P extends {
    id?: string
    'aria-invalid'?: AriaAttributes['aria-invalid']
    'aria-describedby'?: string
  },
>(overrides: P): P {
  const field = useContext(FieldContext)

  if (!field) return overrides

  return {
    ...overrides,
    id: overrides.id ?? field.controlId,
    'aria-invalid': overrides['aria-invalid'] ?? (field.invalid ? true : undefined),
    'aria-describedby': overrides['aria-describedby'] ?? field.describedBy,
  }
}

export function Field({
  label,
  hint,
  error,
  required,
  className,
  children,
}: {
  label: ReactNode
  hint?: ReactNode
  error?: string
  required?: boolean
  className?: string
  children: ReactNode
}) {
  const base = useId()
  const controlId = `${base}-control`
  const hintId = `${base}-hint`
  const errorId = `${base}-error`

  const describedBy = [error ? errorId : null, hint ? hintId : null].filter(Boolean).join(' ')

  return (
    <FieldContext.Provider
      value={{ controlId, describedBy: describedBy || undefined, invalid: Boolean(error) }}
    >
      <div className={cn('space-y-1.5', className)}>
        <label
          htmlFor={controlId}
          className="flex items-center gap-1 text-xs font-medium text-ink-muted"
        >
          {label}
          {required && (
            <span className="text-danger" aria-label="obligatoire">
              *
            </span>
          )}
        </label>

        {children}

        {error && (
          <p id={errorId} role="alert" className="text-xs font-medium text-danger">
            {error}
          </p>
        )}

        {hint && (
          <p id={hintId} className="text-xs text-ink-faint">
            {hint}
          </p>
        )}
      </div>
    </FieldContext.Provider>
  )
}

export function Input({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return <input className={cn(controlClasses, 'h-10', className)} {...useFieldAttributes(props)} />
}

export function Textarea({ className, ...props }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return (
    <textarea
      rows={4}
      className={cn(controlClasses, 'resize-y py-2 leading-relaxed', className)}
      {...useFieldAttributes(props)}
    />
  )
}

/**
 * Liste de sélection d'un `Field`.
 *
 * Simple relais vers `SelectMenu` : il n'existe que pour brancher `id`, `aria-invalid` et
 * `aria-describedby` fournis par le `Field` englobant, que `SelectMenu` — utilisable hors
 * formulaire — n'a aucun moyen de deviner.
 */
export function Select<T extends string>(props: SelectMenuProps<T>) {
  const attributes = useFieldAttributes({
    id: props.id,
    'aria-invalid': props.invalid ? true : undefined,
    'aria-describedby': props['aria-describedby'],
  })

  return (
    <SelectMenu
      {...props}
      id={attributes.id}
      invalid={attributes['aria-invalid'] === true}
      aria-describedby={attributes['aria-describedby']}
    />
  )
}

export function Checkbox({
  label,
  hint,
  className,
  ...props
}: Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> & { label: ReactNode; hint?: ReactNode }) {
  const id = useId()

  return (
    <div className={cn('flex items-start gap-2', className)}>
      <input
        id={id}
        type="checkbox"
        className={cn(
          'mt-0.5 size-4 shrink-0 cursor-pointer rounded border-border-strong accent-brand',
          'disabled:cursor-not-allowed disabled:opacity-50',
        )}
        {...props}
      />
      <div className="min-w-0">
        <label htmlFor={id} className="cursor-pointer text-sm text-ink select-none">
          {label}
        </label>
        {hint && <p className="text-xs text-ink-faint">{hint}</p>}
      </div>
    </div>
  )
}

/** Choix unique parmi quelques options, rendu en segments. Plus lisible qu'un `select` court. */
export function SegmentedControl<T extends string>({
  value,
  options,
  onChange,
  label,
  className,
}: {
  value: T
  options: { value: T; label: ReactNode; title?: string }[]
  onChange: (value: T) => void
  label: string
  className?: string
}) {
  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cn(
        'inline-flex items-center gap-0.5 rounded-[var(--radius-control)] border border-border-subtle bg-surface-sunken p-0.5',
        className,
      )}
    >
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          role="radio"
          aria-checked={option.value === value}
          // Les segments peuvent n'être qu'une icône : le titre sert alors de nom accessible.
          aria-label={option.title}
          title={option.title}
          onClick={() => onChange(option.value)}
          className={cn(
            'inline-flex items-center gap-1.5 rounded-[calc(var(--radius-control)-0.2rem)] px-2.5 py-1 text-xs font-medium transition-colors',
            option.value === value
              ? 'bg-surface text-ink shadow-card'
              : 'text-ink-muted hover:text-ink',
          )}
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}
