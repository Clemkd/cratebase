import {
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
  type RefObject,
} from 'react'
import { Check, ChevronDown } from 'lucide-react'
import { cn, controlClasses } from './utils'

export interface SelectOption<T extends string> {
  value: T
  label: ReactNode
  /**
   * Icône de l'option, affichée dans la liste et reprise par le déclencheur une fois l'option
   * retenue.
   *
   * Portée par sa propre propriété plutôt que glissée dans `label` : le libellé sert aussi de nom
   * accessible et de texte de recherche au clavier, et une icône y ajouterait un nœud que ni l'un
   * ni l'autre ne sait lire. Le glyphe reste donc décoratif — c'est le libellé qui informe.
   */
  icon?: ReactNode
  /**
   * Texte de l'option, pour la saisie au vol et le nom accessible.
   *
   * Obligatoire dès que `label` n'est pas une chaîne : sans lui, on ne saurait ni annoncer
   * l'option ni la retrouver au clavier.
   */
  text?: string
  hint?: ReactNode
  disabled?: boolean
}

export type SelectMenuSize = 'sm' | 'md'

const SIZES: Record<SelectMenuSize, string> = {
  sm: 'h-8 text-xs',
  md: 'h-10 text-sm',
}

/** Espace laissé entre la bulle et le bord de la fenêtre. */
const MARGIN = 8
/** Hauteur en deçà de laquelle la bulle bascule au-dessus du déclencheur plutôt qu'en dessous. */
const MIN_SPACE = 180

interface Anchor {
  left: number
  width: number
  /** Renseigné quand la bulle s'ouvre vers le bas. */
  top?: number
  /** Renseigné quand elle s'ouvre vers le haut, mesuré depuis le bas de la fenêtre. */
  bottom?: number
  maxHeight: number
}

function optionText<T extends string>(option: SelectOption<T>): string {
  if (option.text !== undefined) return option.text

  return typeof option.label === 'string' ? option.label : option.value
}

/**
 * Ancrage d'une bulle sur son déclencheur, et conditions de fermeture.
 *
 * Partagé par la liste simple et la liste à choix multiples : ce sont les mêmes coordonnées, les
 * mêmes bascules haut/bas et les mêmes pièges de défilement. Deux copies auraient divergé au
 * premier correctif appliqué à une seule.
 */
function useMenuAnchor(
  trigger: RefObject<HTMLButtonElement | null>,
  menu: RefObject<HTMLDivElement | null>,
) {
  const [anchor, setAnchor] = useState<Anchor | null>(null)

  const open = anchor !== null
  const close = useCallback(() => setAnchor(null), [])

  const place = useCallback(() => {
    const rect = trigger.current?.getBoundingClientRect()

    if (!rect) return

    const below = globalThis.innerHeight - rect.bottom - MARGIN
    const above = rect.top - MARGIN
    // On ouvre vers le bas tant qu'il y reste de quoi lire quelques options ; sinon on bascule du
    // côté le plus large.
    const downward = below >= MIN_SPACE || below >= above

    const width = Math.max(rect.width, 200)
    const left = Math.max(MARGIN, Math.min(rect.left, globalThis.innerWidth - width - MARGIN))

    setAnchor({
      left,
      width,
      maxHeight: Math.max(120, Math.min(downward ? below : above, 320)),
      ...(downward
        ? { top: rect.bottom + 4 }
        : { bottom: globalThis.innerHeight - rect.top + 4 }),
    })
  }, [trigger])

  // Un défilement ou un redimensionnement rend la position mémorisée fausse : la bulle se
  // retrouverait détachée de son déclencheur. La capture est nécessaire — le défilement se produit
  // dans un conteneur interne (tableau, panneau), pas sur la fenêtre.
  useEffect(() => {
    if (!open) return

    const onScroll = (event: Event) => {
      const target = event.target

      // Le défilement de la bulle elle-même ne la décroche de rien : elle est posée en `fixed`, ses
      // coordonnées restent vraies. La fermer ici rendrait toute liste plus haute que son cadre
      // impossible à parcourir — à la molette comme aux flèches, puisque `scrollIntoView` défile
      // lui aussi.
      if (target instanceof Node && menu.current?.contains(target)) return

      close()
    }

    globalThis.addEventListener('scroll', onScroll, true)
    globalThis.addEventListener('resize', close)

    return () => {
      globalThis.removeEventListener('scroll', onScroll, true)
      globalThis.removeEventListener('resize', close)
    }
  }, [open, close, menu])

  // Clic extérieur. `pointerdown` et non `click` : la fermeture doit précéder l'activation de ce
  // qu'on vient de viser, sinon le premier clic hors de la bulle ne fait que la refermer.
  useEffect(() => {
    if (!open) return

    const onPointerDown = (event: PointerEvent) => {
      const target = event.target

      if (!(target instanceof Node)) return
      if (trigger.current?.contains(target) || menu.current?.contains(target)) return

      close()
    }

    globalThis.addEventListener('pointerdown', onPointerDown, true)
    return () => globalThis.removeEventListener('pointerdown', onPointerDown, true)
  }, [open, close, trigger, menu])

  return { anchor, open, close, place }
}

/** Style commun aux deux bulles. */
const MENU_CLASSES =
  // `overscroll-contain` : arrivé en bout de liste, le défilement se propagerait au conteneur en
  // dessous, qui bougerait sous la bulle et la ferait fermer.
  'fixed z-50 overflow-y-auto overscroll-contain rounded-[var(--radius-card)] ' +
  'border border-border-subtle bg-surface p-2 shadow-popover'

/**
 * Liste de sélection à bulle.
 *
 * Remplace `<select>`, qui est dessiné par le système et non par la page : sa liste déroulée reste
 * blanche sur fond blanc en thème sombre, et aucune règle CSS ne la corrige.
 *
 * La bulle est positionnée en `fixed`, aux coordonnées mesurées du déclencheur, et **non** en
 * `absolute` : plusieurs de ces listes vivent dans des tableaux à `overflow-x-auto` ou dans une
 * barre d'onglets à débordement masqué, qui rogneraient une bulle posée dans leur flux. Elle n'est
 * pas non plus téléportée dans `document.body` — certaines s'ouvrent depuis un `<dialog>`, dont le
 * calque supérieur recouvrirait alors la bulle.
 *
 * Les coordonnées mesurées deviennent fausses dès que la page défile ou change de taille : la bulle
 * se ferme dans ces deux cas, plutôt que de rester accrochée au vide.
 */
export interface SelectMenuProps<T extends string> {
  /** Chaîne vide : aucune option retenue, le substitut s'affiche. */
  value: T | ''
  options: SelectOption<T>[]
  onChange: (value: T) => void
  placeholder?: ReactNode
  disabled?: boolean
  size?: SelectMenuSize
  className?: string
  menuClassName?: string
  id?: string
  invalid?: boolean
  'aria-label'?: string
  'aria-describedby'?: string
}

export function SelectMenu<T extends string>({
  value,
  options,
  onChange,
  placeholder = '— choisir —',
  disabled = false,
  size = 'md',
  className,
  menuClassName,
  id,
  invalid,
  'aria-label': ariaLabel,
  'aria-describedby': ariaDescribedBy,
}: SelectMenuProps<T>) {
  const listId = useId()
  const optionId = (index: number) => `${listId}-option-${index}`

  const trigger = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)
  const typed = useRef({ buffer: '', at: 0 })

  const [active, setActive] = useState(0)

  const { anchor, open, close, place } = useMenuAnchor(trigger, menu)

  const selectedIndex = options.findIndex((option) => option.value === value)
  const selected = selectedIndex === -1 ? null : options[selectedIndex]

  const closeAndFocus = useCallback(() => {
    close()
    trigger.current?.focus()
  }, [close])

  const toggle = () => {
    if (disabled) return

    if (open) {
      close()
      return
    }

    setActive(selectedIndex === -1 ? 0 : selectedIndex)
    place()
  }

  // L'option parcourue est amenée dans la vue : sans cela, la navigation aux flèches sort du cadre
  // visible dès que la liste défile.
  useEffect(() => {
    if (!open) return

    menu.current
      ?.querySelector<HTMLElement>(`#${CSS.escape(`${listId}-option-${active}`)}`)
      ?.scrollIntoView({ block: 'nearest' })
  }, [open, active, listId])

  const step = (offset: number) => {
    if (options.length === 0) return

    let next = active

    // On saute les options désactivées, sans jamais boucler indéfiniment.
    for (let attempt = 0; attempt < options.length; attempt += 1) {
      next = (next + offset + options.length) % options.length

      if (!options[next]?.disabled) break
    }

    setActive(next)
  }

  const edge = (from: 'start' | 'end') => {
    const order = from === 'start' ? options : [...options].reverse()
    const found = order.find((option) => !option.disabled)

    if (found) setActive(options.indexOf(found))
  }

  const choose = (index: number) => {
    const option = options[index]

    if (!option || option.disabled) return

    onChange(option.value)
    closeAndFocus()
  }

  /** Saisie au vol : les lettres frappées coup sur coup composent un préfixe à rechercher. */
  const typeahead = (key: string) => {
    const now = Date.now()

    typed.current.buffer = now - typed.current.at > 700 ? key : typed.current.buffer + key
    typed.current.at = now

    const prefix = typed.current.buffer.toLowerCase()
    const found = options.findIndex(
      (option) => !option.disabled && optionText(option).toLowerCase().startsWith(prefix),
    )

    const option = options[found]

    if (!option) return

    setActive(found)
    // Fermée, la liste change de valeur à la frappe, comme le ferait un `select` natif.
    if (!open) onChange(option.value)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    if (disabled) return

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault()
        if (!open) {
          setActive(selectedIndex === -1 ? 0 : selectedIndex)
          place()
        } else step(1)
        return

      case 'ArrowUp':
        event.preventDefault()
        if (!open) {
          setActive(selectedIndex === -1 ? 0 : selectedIndex)
          place()
        } else step(-1)
        return

      case 'Home':
        if (!open) return
        event.preventDefault()
        edge('start')
        return

      case 'End':
        if (!open) return
        event.preventDefault()
        edge('end')
        return

      case 'Enter':
      case ' ':
        event.preventDefault()
        if (open) choose(active)
        else {
          setActive(selectedIndex === -1 ? 0 : selectedIndex)
          place()
        }
        return

      case 'Escape':
        if (!open) return
        event.preventDefault()
        // Le focus revient au déclencheur : sans cela il retombe en tête de document, et la
        // navigation au clavier repart du haut de la page.
        closeAndFocus()
        return

      case 'Tab':
        if (open) close()
        return

      default:
        if (event.key.length === 1 && !event.metaKey && !event.ctrlKey && !event.altKey) {
          event.preventDefault()
          typeahead(event.key)
        }
    }
  }

  // Pas d'enveloppe autour des deux : la bulle étant en `fixed`, elle n'a besoin d'aucun ancêtre
  // positionné, et un conteneur intermédiaire imposerait sa propre largeur au déclencheur — ce qui
  // empêcherait les appels compacts (« + contrainte », « + champ ») de se dimensionner sur leur
  // contenu.
  return (
    <>
      <button
        ref={trigger}
        id={id}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-activedescendant={open ? optionId(active) : undefined}
        aria-label={ariaLabel}
        aria-describedby={ariaDescribedBy}
        aria-invalid={invalid || undefined}
        disabled={disabled}
        onClick={toggle}
        onKeyDown={onKeyDown}
        className={cn(
          controlClasses,
          SIZES[size],
          'flex cursor-pointer items-center justify-between gap-2 text-left',
          className,
        )}
      >
        <span
          className={cn(
            'flex min-w-0 items-center gap-2',
            selected ? 'text-ink' : 'text-ink-faint',
          )}
        >
          {selected?.icon && <span className="flex shrink-0 items-center">{selected.icon}</span>}
          <span className="truncate">{selected ? selected.label : placeholder}</span>
        </span>
        <ChevronDown
          size={14}
          aria-hidden="true"
          className={cn('shrink-0 text-ink-faint transition-transform', open && 'rotate-180')}
        />
      </button>

      {anchor && (
        <div
          ref={menu}
          role="listbox"
          id={listId}
          aria-label={ariaLabel}
          // Le clic sur une option ne doit pas retirer le focus du déclencheur : c'est lui qui porte
          // `aria-activedescendant`, donc lui qui doit rester l'élément actif.
          onMouseDown={(event) => event.preventDefault()}
          style={{
            left: anchor.left,
            width: anchor.width,
            maxHeight: anchor.maxHeight,
            ...(anchor.top !== undefined ? { top: anchor.top } : { bottom: anchor.bottom }),
          }}
          className={cn(MENU_CLASSES, menuClassName)}
        >
          {options.length === 0 && (
            <p className="px-2 py-1.5 text-xs text-ink-faint">Aucune option disponible.</p>
          )}

          <ul className="space-y-0.5">
            {options.map((option, index) => {
              const isSelected = option.value === value

              return (
                <li key={option.value}>
                  <div
                    id={optionId(index)}
                    role="option"
                    aria-selected={isSelected}
                    aria-disabled={option.disabled || undefined}
                    onClick={() => choose(index)}
                    onMouseEnter={() => !option.disabled && setActive(index)}
                    className={cn(
                      'flex cursor-pointer items-center gap-2 rounded-[var(--radius-control)] px-2 py-1.5 text-[13px]',
                      option.disabled && 'cursor-not-allowed opacity-50',
                      index === active && !option.disabled && 'bg-surface-sunken',
                      isSelected ? 'font-medium text-brand' : 'text-ink',
                    )}
                  >
                    <Check
                      size={13}
                      aria-hidden="true"
                      className={cn('shrink-0', isSelected ? 'opacity-100' : 'opacity-0')}
                    />
                    {option.icon && (
                      <span className="flex shrink-0 items-center">{option.icon}</span>
                    )}
                    <span className="min-w-0 flex-1">
                      <span className="block truncate">{option.label}</span>
                      {option.hint && (
                        <span className="block truncate text-xs text-ink-muted">{option.hint}</span>
                      )}
                    </span>
                  </div>
                </li>
              )
            })}
          </ul>
        </div>
      )}
    </>
  )
}

export interface MultiSelectMenuProps<T extends string> {
  /** Ensemble retenu. Vide : aucune restriction — c'est au substitut de le dire. */
  values: T[]
  options: SelectOption<T>[]
  onChange: (values: T[]) => void
  /** Affiché quand rien n'est retenu. « Tous », et non « — choisir — ». */
  placeholder?: ReactNode
  disabled?: boolean
  size?: SelectMenuSize
  className?: string
  id?: string
  'aria-label'?: string
}

/**
 * Liste à choix multiples.
 *
 * Deux différences de comportement avec la liste simple, et elles suffisent à en justifier
 * l'existence séparée : la bulle <b>reste ouverte</b> après un choix — on vient y cocher plusieurs
 * lignes, la refermer à chaque clic tripleraient les gestes —, et un ensemble vide ne signifie pas
 * « rien » mais « tout », puisqu'un filtre qui n'exclut rien ne filtre pas.
 */
export function MultiSelectMenu<T extends string>({
  values,
  options,
  onChange,
  placeholder = 'Tous',
  disabled = false,
  size = 'md',
  className,
  id,
  'aria-label': ariaLabel,
}: MultiSelectMenuProps<T>) {
  const listId = useId()
  const optionId = (index: number) => `${listId}-option-${index}`

  const trigger = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLDivElement>(null)

  const [active, setActive] = useState(0)

  const { anchor, open, close, place } = useMenuAnchor(trigger, menu)

  const retained = options.filter((option) => values.includes(option.value))

  const toggleOption = (index: number) => {
    const option = options[index]

    if (!option || option.disabled) return

    onChange(
      values.includes(option.value)
        ? values.filter((entry) => entry !== option.value)
        : [...values, option.value],
    )
  }

  const step = (offset: number) => {
    if (options.length === 0) return

    let next = active

    for (let attempt = 0; attempt < options.length; attempt += 1) {
      next = (next + offset + options.length) % options.length

      if (!options[next]?.disabled) break
    }

    setActive(next)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    if (disabled) return

    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowUp':
        event.preventDefault()
        if (open) step(event.key === 'ArrowDown' ? 1 : -1)
        else place()
        return

      case 'Enter':
      case ' ':
        event.preventDefault()
        if (open) toggleOption(active)
        else place()
        return

      case 'Escape':
        if (!open) return
        event.preventDefault()
        close()
        trigger.current?.focus()
        return

      case 'Tab':
        if (open) close()
    }
  }

  return (
    <>
      <button
        ref={trigger}
        id={id}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-activedescendant={open ? optionId(active) : undefined}
        aria-label={ariaLabel}
        disabled={disabled}
        onClick={() => (open ? close() : place())}
        onKeyDown={onKeyDown}
        className={cn(
          controlClasses,
          SIZES[size],
          'flex cursor-pointer items-center justify-between gap-2 text-left',
          className,
        )}
      >
        <span
          className={cn(
            'flex min-w-0 items-center gap-1',
            retained.length > 0 ? 'text-ink' : 'text-ink-faint',
          )}
        >
          {retained.length === 0 ? (
            <span className="truncate">{placeholder}</span>
          ) : (
            <>
              {retained[0]?.icon && (
                <span className="flex shrink-0 items-center">{retained[0].icon}</span>
              )}
              <span className="truncate">{retained[0]?.label}</span>
              {/* Le compte des autres plutôt que leur énumération : dans un en-tête de colonne,
                  trois libellés à la suite débordent avant d'être lus. */}
              {retained.length > 1 && (
                <span className="shrink-0 text-ink-muted">+{retained.length - 1}</span>
              )}
            </>
          )}
        </span>
        <ChevronDown
          size={14}
          aria-hidden="true"
          className={cn('shrink-0 text-ink-faint transition-transform', open && 'rotate-180')}
        />
      </button>

      {open && anchor && (
        <div
          ref={menu}
          role="listbox"
          aria-multiselectable="true"
          id={listId}
          aria-label={ariaLabel}
          onMouseDown={(event) => event.preventDefault()}
          style={{
            left: anchor.left,
            width: anchor.width,
            maxHeight: anchor.maxHeight,
            ...(anchor.top !== undefined ? { top: anchor.top } : { bottom: anchor.bottom }),
          }}
          className={MENU_CLASSES}
        >
          <ul className="space-y-0.5">
            {options.map((option, index) => {
              const isSelected = values.includes(option.value)

              return (
                <li key={option.value}>
                  <div
                    id={optionId(index)}
                    role="option"
                    aria-selected={isSelected}
                    aria-disabled={option.disabled || undefined}
                    onClick={() => toggleOption(index)}
                    onMouseEnter={() => !option.disabled && setActive(index)}
                    className={cn(
                      'flex cursor-pointer items-center gap-2 rounded-[var(--radius-control)] px-2 py-1.5 text-[13px]',
                      option.disabled && 'cursor-not-allowed opacity-50',
                      index === active && !option.disabled && 'bg-surface-sunken',
                      isSelected ? 'font-medium text-brand' : 'text-ink',
                    )}
                  >
                    {/* Une case et non une coche : dans une liste à choix multiples, un signe qui
                        n'existe que coché ne dit pas qu'on peut en cocher plusieurs. */}
                    <span
                      aria-hidden="true"
                      className={cn(
                        'flex size-3.5 shrink-0 items-center justify-center rounded-[4px] border',
                        isSelected ? 'border-brand bg-brand text-brand-ink' : 'border-border-strong',
                      )}
                    >
                      {isSelected && <Check size={10} />}
                    </span>
                    {option.icon && (
                      <span className="flex shrink-0 items-center">{option.icon}</span>
                    )}
                    <span className="min-w-0 flex-1 truncate">{option.label}</span>
                  </div>
                </li>
              )
            })}
          </ul>
        </div>
      )}
    </>
  )
}
