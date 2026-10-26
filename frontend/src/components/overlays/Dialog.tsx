import { useEffect, useId, useRef, type ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { IconButton } from '../common/Button'
import styles from './overlays.module.css'

interface DialogBaseProps {
  open: boolean
  onClose: () => void
  title: ReactNode
  description?: ReactNode
  children?: ReactNode
  footer?: ReactNode
  /** Closing via Escape/backdrop is blocked while true (e.g. during submit). */
  dismissible?: boolean
  className?: string
  variant: 'modal' | 'drawer'
  size?: 'sm' | 'md' | 'lg'
  role?: 'dialog' | 'alertdialog'
}

/**
 * Shared native <dialog> behaviour: showModal() provides the focus trap, inert background,
 * Escape handling and top-layer stacking; we add focus restore and scroll lock.
 */
function DialogBase({ open, onClose, title, description, children, footer, dismissible = true, className, variant, size = 'md', role = 'dialog' }: DialogBaseProps) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const descId = useId()
  const returnFocus = useRef<HTMLElement | null>(null)

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    if (open && !dialog.open) {
      returnFocus.current = document.activeElement as HTMLElement | null
      if (typeof dialog.showModal === 'function') dialog.showModal()
      else dialog.setAttribute('open', '')
      document.documentElement.style.overflow = 'hidden'
      // Honour autoFocus consistently across browsers (React does not emit the attribute).
      dialog.querySelector<HTMLElement>('[data-autofocus], [autofocus]')?.focus()
    } else if (!open && dialog.open) {
      if (typeof dialog.close === 'function') dialog.close()
      else dialog.removeAttribute('open')
    }
    return () => {
      if (!open) return
      document.documentElement.style.overflow = ''
      returnFocus.current?.focus?.()
    }
  }, [open])

  return (
    <dialog
      ref={ref}
      role={role === 'alertdialog' ? 'alertdialog' : undefined}
      className={cx(styles.dialog, variant === 'drawer' ? styles.drawer : styles.modal, styles[size], className)}
      aria-labelledby={titleId}
      aria-describedby={description ? descId : undefined}
      onCancel={(e) => {
        e.preventDefault()
        if (dismissible) onClose()
      }}
      onMouseDown={(e) => {
        // Click on the backdrop (the dialog element itself, outside the panel).
        if (e.target === e.currentTarget && dismissible) onClose()
      }}
    >
      {open && (
        <div className={styles.panel}>
          <header className={styles.dialogHeader}>
            <div className={styles.dialogHeading}>
              <h2 id={titleId} className={styles.dialogTitle}>{title}</h2>
              {description && <p id={descId} className={styles.dialogDescription}>{description}</p>}
            </div>
            <IconButton icon="close" label="Close" onClick={onClose} disabled={!dismissible} />
          </header>
          <div className={styles.dialogBody}>{children}</div>
          {footer && <footer className={styles.dialogFooter}>{footer}</footer>}
        </div>
      )}
    </dialog>
  )
}

export type ModalProps = Omit<DialogBaseProps, 'variant'>

export function Modal(props: ModalProps) {
  return <DialogBase {...props} variant="modal" />
}

export function Drawer(props: ModalProps) {
  return <DialogBase {...props} variant="drawer" />
}
