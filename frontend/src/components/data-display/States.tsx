import type { ReactNode } from 'react'
import { errorMessage } from '@/services/apiClient'
import { Button } from '../common/Button'
import { Icon, type IconName } from '../common/Icon'
import { Spinner } from '../common/Spinner'
import styles from './display.module.css'

interface EmptyStateProps {
  title: string
  description?: ReactNode
  icon?: IconName
  action?: ReactNode
}

export function EmptyState({ title, description, icon = 'info', action }: EmptyStateProps) {
  return (
    <div className={styles.state}>
      <span className={styles.stateIcon}><Icon name={icon} size={24} /></span>
      <h3 className={styles.stateTitle}>{title}</h3>
      {description && <p className={styles.stateText}>{description}</p>}
      {action && <div className={styles.stateAction}>{action}</div>}
    </div>
  )
}

export function LoadingState({ label = 'Loading' }: { label?: string }) {
  return (
    <div className={styles.state}>
      <Spinner size="lg" label={label} />
      <p className={styles.stateText} aria-hidden="true">{label}…</p>
    </div>
  )
}

interface ErrorStateProps {
  error: unknown
  title?: string
  onRetry?: () => void
}

export function ErrorState({ error, title = 'Could not load data', onRetry }: ErrorStateProps) {
  return (
    <div className={styles.state} role="alert">
      <span className={`${styles.stateIcon} ${styles.stateIconDanger}`}><Icon name="error" size={24} /></span>
      <h3 className={styles.stateTitle}>{title}</h3>
      <p className={styles.stateText}>{errorMessage(error)}</p>
      {onRetry && (
        <div className={styles.stateAction}>
          <Button icon="refresh" onClick={onRetry}>Try again</Button>
        </div>
      )}
    </div>
  )
}
