import { useEffect, useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { Button, type ButtonProps } from './Button'
import { copyToClipboard } from '../lib/clipboard'

// `value` est écarté de ButtonProps avant d'être redéfini : l'attribut HTML natif du même nom
// n'accepte qu'une chaîne, là où ce bouton veut aussi une fonction évaluée au clic.
interface CopyButtonProps extends Omit<ButtonProps, 'onClick' | 'children' | 'value' | 'icon'> {
  /** Texte à copier, ou fonction évaluée au clic pour refléter l'état du moment. */
  value: string | (() => string)
  /** Libellé affiché ; absent, le bouton se réduit à son icône. */
  label?: string
}

/**
 * Bouton de copie avec confirmation visuelle.
 *
 * La confirmation est indispensable ici : la copie ne produit aucun effet visible dans la page, et
 * l'utilisateur qui doute reclique ou recopie l'identifiant à la main.
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
      aria-label={label ? undefined : 'Copier'}
      title={label ? undefined : 'Copier'}
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
      {label && <span>{copied ? 'Copié' : label}</span>}
    </Button>
  )
}
