import type { ReactNode } from 'react'
import { Button } from '../common/Button'
import { Modal } from './Dialog'

interface ConfirmationDialogProps {
  open: boolean
  title: string
  message: ReactNode
  confirmLabel?: string
  cancelLabel?: string
  destructive?: boolean
  loading?: boolean
  onConfirm: () => void
  onCancel: () => void
}

/** Alert dialog for confirming consequential actions. Focus starts on Cancel (the safe choice). */
export function ConfirmationDialog({
  open, title, message, confirmLabel = 'Confirm', cancelLabel = 'Cancel', destructive, loading, onConfirm, onCancel,
}: ConfirmationDialogProps) {
  return (
    <Modal
      open={open}
      onClose={onCancel}
      title={title}
      size="sm"
      role="alertdialog"
      dismissible={!loading}
      footer={
        <>
          <Button onClick={onCancel} disabled={loading} data-autofocus>{cancelLabel}</Button>
          <Button variant={destructive ? 'danger' : 'primary'} onClick={onConfirm} loading={loading}>{confirmLabel}</Button>
        </>
      }
    >
      {message}
    </Modal>
  )
}
