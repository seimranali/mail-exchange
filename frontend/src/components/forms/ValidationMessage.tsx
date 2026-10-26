import type { ReactNode } from 'react'
import { Icon } from '../common/Icon'
import styles from './forms.module.css'

interface ValidationMessageProps {
  id?: string
  children: ReactNode
}

export function ValidationMessage({ id, children }: ValidationMessageProps) {
  return (
    <p id={id} className={styles.error} role="alert">
      <Icon name="error" size={14} />
      <span>{children}</span>
    </p>
  )
}
