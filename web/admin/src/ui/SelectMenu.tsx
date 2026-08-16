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
   * Icon for the option, shown in the list and echoed by the trigger once the option is
   * selected.
   *
   * Carried by its own property rather than slipped into `label`: the label also serves as the
   * accessible name and the keyboard typeahead text, and an icon there would add a node that
   * neither can read. The glyph therefore stays decorative — it's the label that informs.
   */
  icon?: ReactNode
  /**
   * Text of the option, for typeahead and the accessible name.
   *
   * Required as soon as `label` isn't a string: without it, there's no way to announce the
   * option or find it by keyboard.
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

/** Space left between the flyout and the edge of the window. */
const MARGIN = 8
/** Height below which the flyout flips above the trigger instead of below. */
const MIN_SPACE = 180

interface Anchor {
  left: number
  width: number
  /** Set when the flyout opens downward. */
  top?: number
  /** Set when it opens upward, measured from the bottom of the window. */
  bottom?: number
  maxHeight: number
}

function optionText<T extends string>(option: SelectOption<T>): string {
  if (option.text !== undefined) return option.text

  return typeof option.label === 'string' ? option.label : option.value
}

/**
 * Anchoring of a flyout to its trigger, and closing conditions.
 *
 * Shared by the single-select and multi-select lists: they use the same coordinates, the same
 * up/down flips, and the same scroll traps. Two copies would have diverged the first time a fix
 * was applied to only one.
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
    // Opens downward as long as there's enough room to read a few options; otherwise it flips to
    // whichever side is larger.
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

  // A scroll or a resize makes the memoized position stale: the flyout would end up detached
  // from its trigger. Capture is necessary — the scroll happens in an inner container (table,
  // panel), not on the window.
  useEffect(() => {
    if (!open) return

    const onScroll = (event: Event) => {
      const target = event.target

      // The flyout's own scrolling doesn't detach it from anything: it's placed `fixed`, its
      // coordinates stay true. Closing it here would make any list taller than its frame
      // impossible to scroll through — with the wheel as with the arrows, since `scrollIntoView`
      // also scrolls.
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

  // Outside click. `pointerdown` rather than `click`: closing must precede the activation of
  // whatever was just targeted, otherwise the first click outside the flyout would only close it.
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

/** Style shared by both flyouts. */
const MENU_CLASSES =
  // `overscroll-contain`: reaching the end of the list, scrolling would propagate to the
  // container below, which would move under the flyout and close it.
  'fixed z-50 overflow-y-auto overscroll-contain rounded-[var(--radius-card)] ' +
  'border border-border-subtle bg-surface p-2 shadow-popover'

/**
 * Flyout select list.
 *
 * Replaces `<select>`, which is drawn by the system rather than the page: its dropdown stays
 * white on white in dark theme, and no CSS rule fixes it.
 *
 * The flyout is positioned `fixed`, at the trigger's measured coordinates, and **not**
 * `absolute`: several of these lists live inside tables with `overflow-x-auto` or a tab bar with
 * hidden overflow, which would clip a flyout placed in their flow. It also isn't teleported into
 * `document.body` — some open from a `<dialog>`, whose top layer would then cover the flyout.
 *
 * The measured coordinates go stale as soon as the page scrolls or resizes: the flyout closes in
 * both cases, rather than staying anchored to empty space.
 */
export interface SelectMenuProps<T extends string> {
  /** Empty string: no option selected, the placeholder shows. */
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
  placeholder = '— choose —',
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

  // The traversed option is scrolled into view: without this, arrow-key navigation exits the
  // visible frame as soon as the list scrolls.
  useEffect(() => {
    if (!open) return

    menu.current
      ?.querySelector<HTMLElement>(`#${CSS.escape(`${listId}-option-${active}`)}`)
      ?.scrollIntoView({ block: 'nearest' })
  }, [open, active, listId])

  const step = (offset: number) => {
    if (options.length === 0) return

    let next = active

    // Disabled options are skipped, without ever looping indefinitely.
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

  /** Typeahead: letters typed in quick succession compose a prefix to search for. */
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
    // Closed, the list changes value as you type, just as a native `select` would.
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
        // Focus returns to the trigger: without this it falls back to the top of the document,
        // and keyboard navigation restarts from the top of the page.
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

  // No wrapper around the two: since the flyout is `fixed`, it needs no positioned ancestor, and
  // an intermediate container would impose its own width on the trigger — which would prevent
  // compact calls ("+ constraint", "+ field") from sizing themselves to their content.
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
          // A click on an option must not remove focus from the trigger: it's the trigger that
          // carries `aria-activedescendant`, so it's the trigger that must remain the active element.
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
            <p className="px-2 py-1.5 text-xs text-ink-faint">No option available.</p>
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
  /** Retained set. Empty: no restriction — that's for the placeholder to say. */
  values: T[]
  options: SelectOption<T>[]
  onChange: (values: T[]) => void
  /** Shown when nothing is selected. "All", not "— choose —". */
  placeholder?: ReactNode
  disabled?: boolean
  size?: SelectMenuSize
  className?: string
  id?: string
  'aria-label'?: string
}

/**
 * Multi-select list.
 *
 * Two behavioral differences from the single-select list, and they're enough to justify its
 * separate existence: the flyout <b>stays open</b> after a choice — you come here to check off
 * several rows, closing it on every click would triple the gestures —, and an empty set doesn't
 * mean "nothing" but "everything", since a filter that excludes nothing doesn't filter.
 */
export function MultiSelectMenu<T extends string>({
  values,
  options,
  onChange,
  placeholder = 'All',
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
              {/* The count of the rest rather than listing them: in a column header, three
                  labels in a row overflow before they're read. */}
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
                    {/* A checkbox rather than a checkmark: in a multi-select list, a sign that
                        only exists checked doesn't convey that several can be checked. */}
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
