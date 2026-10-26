import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { IconButton } from '../common/Button'
import { Icon, type IconName } from '../common/Icon'
import styles from './overlays.module.css'

type ToastTone = 'success' | 'error' | 'info' | 'warning'

interface Toast {
  id: number
  title: string
  message?: string
  tone: ToastTone
}

interface ToastApi {
  show: (toast: Omit<Toast, 'id'>) => void
  success: (title: string, message?: string) => void
  error: (title: string, message?: string) => void
  info: (title: string, message?: string) => void
}

const ToastContext = createContext<ToastApi | null>(null)

const icons: Record<ToastTone, IconName> = { success: 'success', error: 'error', info: 'info', warning: 'alert' }

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(1)

  const dismiss = useCallback((id: number) => setToasts((t) => t.filter((x) => x.id !== id)), [])

  const show = useCallback(
    (toast: Omit<Toast, 'id'>) => {
      const id = nextId.current++
      setToasts((t) => [...t.slice(-3), { ...toast, id }])
      // Errors stay until dismissed so they are not missed; others auto-dismiss.
      if (toast.tone !== 'error') setTimeout(() => dismiss(id), 6000)
    },
    [dismiss],
  )

  const api = useMemo<ToastApi>(
    () => ({
      show,
      success: (title, message) => show({ title, message, tone: 'success' }),
      error: (title, message) => show({ title, message, tone: 'error' }),
      info: (title, message) => show({ title, message, tone: 'info' }),
    }),
    [show],
  )

  return (
    <ToastContext.Provider value={api}>
      {children}
      {/* Live regions are always rendered so announcements are reliable. */}
      <div className={styles.toastRegion} aria-live="polite" aria-relevant="additions" role="region" aria-label="Notifications">
        {toasts.map((t) => (
          <ToastNotification key={t.id} toast={t} onDismiss={() => dismiss(t.id)} />
        ))}
      </div>
    </ToastContext.Provider>
  )
}

function ToastNotification({ toast, onDismiss }: { toast: Toast; onDismiss: () => void }) {
  return (
    <div className={cx(styles.toast, styles[`toast${toast.tone[0]!.toUpperCase()}${toast.tone.slice(1)}`])} role={toast.tone === 'error' ? 'alert' : 'status'}>
      <Icon name={icons[toast.tone]} size={18} className={styles.toastIcon} />
      <div className={styles.toastBody}>
        <p className={styles.toastTitle}>{toast.title}</p>
        {toast.message && <p className={styles.toastMessage}>{toast.message}</p>}
      </div>
      <IconButton icon="close" label="Dismiss notification" size="sm" onClick={onDismiss} />
    </div>
  )
}

export function useToast(): ToastApi {
  const ctx = useContext(ToastContext)
  if (!ctx) throw new Error('useToast must be used inside <ToastProvider>')
  return ctx
}
