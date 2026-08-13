import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import { AlertTriangle, CheckCircle2, Info, X } from 'lucide-react'
import { cn } from './utils'

export type ToastTone = 'info' | 'success' | 'error'

interface Toast {
  id: number
  tone: ToastTone
  message: string
}

interface ToastApi {
  push: (tone: ToastTone, message: string) => void
  info: (message: string) => void
  success: (message: string) => void
  error: (message: string) => void
}

const ToastContext = createContext<ToastApi | null>(null)

const TONES: Record<ToastTone, { icon: typeof Info; border: string; accent: string }> = {
  info: { icon: Info, border: 'border-border-strong', accent: 'text-ink-muted' },
  success: { icon: CheckCircle2, border: 'border-success/40', accent: 'text-success' },
  error: { icon: AlertTriangle, border: 'border-danger/40', accent: 'text-danger' },
}

/** Les échecs demandent une lecture, pas un coup d'œil : ils restent visibles plus longtemps. */
const LIFETIME_MS: Record<ToastTone, number> = { info: 4000, success: 4000, error: 8000 }

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(0)

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((toast) => toast.id !== id))
  }, [])

  const api = useMemo<ToastApi>(() => {
    const push = (tone: ToastTone, message: string) => {
      const id = ++nextId.current

      setToasts((current) => [...current.slice(-3), { id, tone, message }])
      globalThis.setTimeout(() => dismiss(id), LIFETIME_MS[tone])
    }

    return {
      push,
      info: (message) => push('info', message),
      success: (message) => push('success', message),
      error: (message) => push('error', message),
    }
  }, [dismiss])

  return (
    <ToastContext.Provider value={api}>
      {children}

      {/* Une seule région vivante pour toute l'application : plusieurs régions concurrentes se
          coupent la parole chez les lecteurs d'écran. */}
      <div
        aria-live="polite"
        aria-atomic="false"
        className="pointer-events-none fixed inset-x-0 bottom-0 z-50 flex flex-col items-center gap-2 p-4 sm:items-end"
      >
        {toasts.map((toast) => {
          const { icon: Icon, border, accent } = TONES[toast.tone]

          return (
            <div
              key={toast.id}
              role={toast.tone === 'error' ? 'alert' : undefined}
              className={cn(
                'pointer-events-auto flex w-full max-w-sm items-start gap-2.5 rounded-[var(--radius-card)]',
                'border bg-surface-raised px-4 py-3 text-sm shadow-popover',
                border,
              )}
            >
              <Icon size={16} className={cn('mt-0.5 shrink-0', accent)} aria-hidden="true" />
              <p className="min-w-0 flex-1 whitespace-pre-line text-ink">{toast.message}</p>
              <button
                type="button"
                aria-label="Fermer la notification"
                onClick={() => dismiss(toast.id)}
                className="-mr-1 shrink-0 rounded p-0.5 text-ink-faint hover:text-ink"
              >
                <X size={14} aria-hidden="true" />
              </button>
            </div>
          )
        })}
      </div>
    </ToastContext.Provider>
  )
}

export function useToast(): ToastApi {
  const api = useContext(ToastContext)

  if (!api) {
    throw new Error('useToast doit être utilisé dans un ToastProvider.')
  }

  return api
}
