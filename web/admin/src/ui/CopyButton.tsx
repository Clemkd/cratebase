import { useEffect, useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { Button, type ButtonProps } from './Button'
import { copyToClipboard } from '../lib/clipboard'

// `value` is omitted from ButtonProps before being redefined: the native HTML attribute of the
// same name only accepts a string, whereas this button also wants a function evaluated on click.
interface CopyButtonProps extends Omit<ButtonProps, 'onClick' | 'children' | 'value' | 'icon'> {
  /** Text to copy, or a function evaluated on click to reflect the current state. */
  value: string | (() => string)
  /** Displayed label; if absent, the button shrinks to its icon. */
  label?: string
}

/**
 * Copy button with visual confirmation.
 *
 * The confirmation is essential here: the copy produces no visible effect on the page, and a
 * doubtful user would click again or retype the identifier by hand.
 */
export function CopyButton({
  value,
  label,
  variant = 'ghost',
  size = 'sm',
  ...props
}: CopyButtonProps) {
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!copied) return

    const timer = globalThis.setTimeout(() => setCopied(false), 1800)

    return () => globalThis.clearTimeout(timer)
  }, [copied])

  return (
    <Button
      variant={variant}
      size={size}
      aria-label={label ? undefined : 'Copy'}
      title={label ? undefined : 'Copy'}
      onClick={() => {
        void copyToClipboard(typeof value === 'function' ? value() : value).then(setCopied)
      }}
      {...props}
    >
      {copied ? (
        <Check size={14} className="text-success" aria-hidden="true" />
      ) : (
        <Copy size={14} aria-hidden="true" />
      )}
      {label && <span>{copied ? 'Copied' : label}</span>}
    </Button>
  )
}
