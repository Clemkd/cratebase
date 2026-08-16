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
 * Attributes to place on a `Field`'s control.
 *
 * Going through a context rather than props avoids having to copy `id`, `aria-invalid`, and
 * `aria-describedby` into every call — it's exactly what gets forgotten, and the omission only
 * shows up with a screen reader.
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
            <span className="text-danger" aria-label="required">
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
 * Select list for a `Field`.
 *
 * A simple relay to `SelectMenu`: it exists only to wire up `id`, `aria-invalid`, and
 * `aria-describedby` supplied by the enclosing `Field`, which `SelectMenu` — usable outside a
 * form — has no way to guess.
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

/** Single choice among a few options, rendered as segments. More readable than a short `select`. */
export function SegmentedControl<T extends string>({
  value,
  options,
  onChange,
  label,
  className,
}: {
  value: T
  options: {
    value: T
    label: ReactNode
    /** Native tooltip. No longer the accessible name: see `srLabel`. */
    title?: string
    /**
     * Accessible name of the segment.
     *
     * Only set it for a segment with no visible text. If a readable label is present, an
     * `aria-label` would replace it for a screen reader: the voice command "click Locked" would
     * then stop finding the button that carries that very word.
     */
    srLabel?: string
  }[]
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
          // Segments can be just an icon: the caller then supplies their accessible name.
          aria-label={option.srLabel}
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
